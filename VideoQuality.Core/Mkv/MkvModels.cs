using System.Globalization;
using System.Text.Json;
using VideoQuality.Core.Localization;

namespace VideoQuality.Core.Mkv;

public enum MkvTrackType
{
    Video,
    Audio,
    Subtitles,
    Other,
}

/// <summary>One track as mkvmerge identifies it.</summary>
public sealed record MkvTrack(
    int Id,
    MkvTrackType Type,
    string Codec,
    string CodecId,
    string Language,
    string Name,
    bool Default,
    bool Forced,
    string Details)
{
    /// <summary>File extension mkvextract's output should get for this codec.</summary>
    public string Extension => MkvExtensions.ForCodec(CodecId, Type);
}

public sealed record MkvAttachment(int Id, string FileName, string MimeType, long Size, string Description);

/// <summary>What `mkvmerge -J` reports for a file.</summary>
public sealed record MkvFileInfo(
    string Path,
    string Container,
    double DurationSeconds,
    string Title,
    IReadOnlyList<MkvTrack> Tracks,
    IReadOnlyList<MkvAttachment> Attachments,
    int ChapterEntries)
{
    /// <summary>Parses mkvmerge's JSON identification (identification-format version 12+).</summary>
    public static MkvFileInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("errors", out JsonElement errors) && errors.GetArrayLength() > 0)
        {
            throw new InvalidDataException(string.Join("; ", errors.EnumerateArray().Select(e => e.GetString())));
        }

        JsonElement container = root.GetProperty("container");
        if (!container.TryGetProperty("recognized", out JsonElement recognized) || !recognized.GetBoolean())
        {
            throw new InvalidDataException(Loc.T("mkv.error.unrecognised"));
        }

        JsonElement containerProps = container.TryGetProperty("properties", out JsonElement cp) ? cp : default;
        double duration = containerProps.ValueKind == JsonValueKind.Object && Number(containerProps, "duration") is long ns
            ? ns / 1e9
            : 0;

        var tracks = new List<MkvTrack>();
        foreach (JsonElement t in root.GetProperty("tracks").EnumerateArray())
        {
            JsonElement p = t.GetProperty("properties");
            tracks.Add(new MkvTrack(
                t.GetProperty("id").GetInt32(),
                t.GetProperty("type").GetString() switch
                {
                    "video" => MkvTrackType.Video,
                    "audio" => MkvTrackType.Audio,
                    "subtitles" => MkvTrackType.Subtitles,
                    _ => MkvTrackType.Other,
                },
                Text(t, "codec"),
                Text(p, "codec_id"),
                Text(p, "language_ietf") is { Length: > 0 } ietf ? ietf : Text(p, "language"),
                Text(p, "track_name"),
                Flag(p, "default_track"),
                Flag(p, "forced_track"),
                Details(t.GetProperty("type").GetString(), p)));
        }

        var attachments = new List<MkvAttachment>();
        if (root.TryGetProperty("attachments", out JsonElement attached))
        {
            foreach (JsonElement a in attached.EnumerateArray())
            {
                attachments.Add(new MkvAttachment(
                    a.GetProperty("id").GetInt32(),
                    Text(a, "file_name"),
                    Text(a, "content_type"),
                    Number(a, "size") ?? 0,
                    Text(a, "description")));
            }
        }

        int chapters = root.TryGetProperty("chapters", out JsonElement ch)
            ? ch.EnumerateArray().Sum(c => (int)(Number(c, "num_entries") ?? 0))
            : 0;

        return new MkvFileInfo(
            Text(root, "file_name"),
            Text(container, "type"),
            duration,
            containerProps.ValueKind == JsonValueKind.Object ? Text(containerProps, "title") : string.Empty,
            tracks,
            attachments,
            chapters);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    /// <summary>An integer property; null when it is missing or not a number.</summary>
    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string Details(string? type, JsonElement p) => type switch
    {
        "video" => Text(p, "pixel_dimensions"),
        "audio" => string.Join(" · ", new[]
        {
            Number(p, "audio_channels") is long channels ? $"{channels} ch" : null,
            Number(p, "audio_sampling_frequency") is long hz ? (hz / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz" : null,
        }.Where(s => s is not null)),
        _ => string.Empty,
    };
}

/// <summary>Output extensions for mkvextract, by Matroska codec ID.</summary>
public static class MkvExtensions
{
    public static string ForCodec(string codecId, MkvTrackType type) => codecId switch
    {
        "V_MPEG4/ISO/AVC" => ".h264",
        "V_MPEGH/ISO/HEVC" => ".hevc",
        "V_AV1" or "V_VP8" or "V_VP9" => ".ivf",
        "V_MPEG2" or "V_MPEG1" => ".m2v",
        "A_AAC" or "A_AAC/MPEG4/LC" or "A_AAC/MPEG2/LC" => ".aac",
        "A_FLAC" => ".flac",
        "A_OPUS" => ".opus",
        "A_VORBIS" => ".ogg",
        "A_AC3" => ".ac3",
        "A_EAC3" => ".eac3",
        "A_DTS" => ".dts",
        "A_TRUEHD" => ".thd",
        "A_MPEG/L3" => ".mp3",
        "A_MPEG/L2" => ".mp2",
        "A_PCM/INT/LIT" or "A_PCM/INT/BIG" or "A_PCM/FLOAT/IEEE" => ".wav",
        "S_TEXT/ASS" or "S_ASS" => ".ass",
        "S_TEXT/SSA" or "S_SSA" => ".ssa",
        "S_TEXT/UTF8" => ".srt",
        "S_TEXT/WEBVTT" => ".vtt",
        "S_HDMV/PGS" => ".sup",
        "S_VOBSUB" => ".sub",
        _ => type switch
        {
            MkvTrackType.Video => ".mkv",
            MkvTrackType.Audio => ".mka",
            MkvTrackType.Subtitles => ".mks",
            _ => ".bin",
        },
    };
}
