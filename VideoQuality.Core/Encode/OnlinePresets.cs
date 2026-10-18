using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoQuality.Core.Localization;

namespace VideoQuality.Core.Encode;

/// <summary>A preset file found online, with the presets it holds.</summary>
public sealed record OnlinePreset(string FileName, string Url, EncodePreset Preset);

/// <summary>Where online presets come from: a folder of a GitHub repository, or one file's URL.</summary>
public sealed partial record PresetSource(string Owner, string Repository, string Folder, string Branch, string? FileUrl)
{
    /// <summary>This project's community presets.</summary>
    public const string Default = "SeaAndStars/VideoQuality/presets@main";

    /// <summary>
    /// <c>owner/repo[/folder][@branch]</c>, a GitHub folder link
    /// (<c>https://github.com/owner/repo/tree/branch/folder</c>), or the URL of one <c>.json</c> file.
    /// </summary>
    public static PresetSource Parse(string text)
    {
        string source = text.Trim();
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
        {
            Match tree = TreeLink().Match(uri.AbsolutePath);
            if (uri.Host == "github.com" && tree.Success)
            {
                return new PresetSource(tree.Groups[1].Value, tree.Groups[2].Value, tree.Groups[4].Value.Trim('/'), tree.Groups[3].Value, null);
            }

            return new PresetSource(string.Empty, string.Empty, string.Empty, string.Empty, source);
        }

        Match shortForm = ShortForm().Match(source);
        if (!shortForm.Success)
        {
            throw new FormatException(Loc.T("presets.error.source", text));
        }

        return new PresetSource(
            shortForm.Groups[1].Value,
            shortForm.Groups[2].Value,
            shortForm.Groups[3].Value.Trim('/'),
            shortForm.Groups[4].Success ? shortForm.Groups[4].Value : "main",
            null);
    }

    /// <summary>The GitHub API URL that lists the folder.</summary>
    public string ListUrl => $"https://api.github.com/repos/{Owner}/{Repository}/contents/{Folder}?ref={Uri.EscapeDataString(Branch)}";

    [GeneratedRegex(@"^([\w.-]+)/([\w.-]+)((?:/[^@]+)?)(?:@(.+))?$")]
    private static partial Regex ShortForm();

    [GeneratedRegex(@"^/([\w.-]+)/([\w.-]+)/tree/([^/]+)(/.*)?$")]
    private static partial Regex TreeLink();
}

/// <summary>Reads preset files from GitHub (or any URL) over HTTPS.</summary>
public sealed class OnlinePresets(HttpClient http)
{
    /// <summary>Every preset in the source's <c>.json</c> files; unreadable files are skipped.</summary>
    public async Task<IReadOnlyList<OnlinePreset>> ListAsync(string source, CancellationToken cancellationToken = default)
    {
        PresetSource parsed = PresetSource.Parse(source);
        List<(string Name, string Url)> files = parsed.FileUrl is { } single
            ? [(Path.GetFileName(new Uri(single).AbsolutePath), single)]
            : await ListFolderAsync(parsed, cancellationToken).ConfigureAwait(false);

        var found = new List<OnlinePreset>();
        foreach ((string name, string url) in files)
        {
            string json = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            try
            {
                found.AddRange(PresetStore.Parse(json).Select(p => new OnlinePreset(name, url, p)));
            }
            catch (JsonException ex)
            {
                Diagnostics.VqLog.Warn("presets", $"skipping {url}", ex);
            }
        }

        return found;
    }

    private async Task<List<(string Name, string Url)>> ListFolderAsync(PresetSource source, CancellationToken cancellationToken)
    {
        using JsonDocument listing = JsonDocument.Parse(await GetAsync(source.ListUrl, cancellationToken).ConfigureAwait(false));
        if (listing.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(Loc.T("presets.error.notFolder", source.ListUrl));
        }

        return [.. listing.RootElement.EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "file" &&
                           item.GetProperty("name").GetString()!.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.GetProperty("name").GetString()!, item.GetProperty("download_url").GetString()!))
            .OrderBy(file => file.Item1, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("VideoQuality");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(Loc.T("presets.error.offline", ex.Message), ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(response.StatusCode switch
                {
                    HttpStatusCode.NotFound => Loc.T("presets.error.notFound", url),
                    HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => Loc.T("presets.error.rateLimited"),
                    _ => Loc.T("presets.error.http", (int)response.StatusCode, url),
                });
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
