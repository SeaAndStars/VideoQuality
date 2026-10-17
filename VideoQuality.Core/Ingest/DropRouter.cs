using VideoQuality.Core.Localization;
namespace VideoQuality.Core.Ingest;

/// <summary>Where files arrived: a drop zone, or anywhere else (startup args, Open With, paste).</summary>
public enum DropZone
{
    /// <summary>The first file becomes the reference if none is set; everything else is an encode.</summary>
    Auto,

    /// <summary>The first file becomes the reference; any others are queued as encodes.</summary>
    Reference,

    /// <summary>Every file is an encode.</summary>
    Encodes,
}

/// <summary>A path that will not be added, and why.</summary>
public sealed record RejectedPath(string Path, string Reason);

/// <summary>What to do with one set of incoming paths.</summary>
public sealed record DropPlan
{
    /// <summary>The new reference, or null to keep the current one.</summary>
    public string? Reference { get; init; }

    /// <summary>Encodes to add, in order, already expanded from folders and de-duplicated.</summary>
    public IReadOnlyList<string> Encodes { get; init; } = [];

    public IReadOnlyList<RejectedPath> Rejected { get; init; } = [];

    /// <summary>Paths that were already loaded (or repeated in this drop); ignored.</summary>
    public IReadOnlyList<string> Duplicates { get; init; } = [];

    /// <summary>
    /// The reference changes while encodes exist, so every score goes stale: ask once before
    /// re-scoring them.
    /// </summary>
    public bool NeedsConfirm { get; init; }

    /// <summary>
    /// Several files were dropped on the reference zone: the first became the reference, the
    /// rest were queued as encodes. The UI offers Undo for this.
    /// </summary>
    public bool ReferenceOverflowed { get; init; }

    public bool IsEmpty => Reference is null && Encodes.Count == 0;
}

/// <summary>
/// Decides which incoming paths become the reference, which become encodes, and which are
/// rejected. Pure apart from reading the file system, so every entry point (drop zones, file
/// pickers, startup arguments, Open With, paste) shares one tested rule set.
/// </summary>
public static class DropRouter
{
    /// <summary>Containers and elementary streams the app accepts (.y4m makes a good lossless reference).</summary>
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".mov", ".webm", ".avi", ".ts", ".m2ts", ".ivf", ".y4m",
    };

    public static string NonLocalReason => Loc.T("drop.nonLocal");

    /// <param name="paths">Incoming paths; null marks an item with no local path (smb://, mtp://).</param>
    /// <param name="zone">Where the paths arrived.</param>
    /// <param name="currentReference">The loaded reference, if any.</param>
    /// <param name="currentEncodes">Paths of encodes already in the table.</param>
    public static DropPlan Plan(
        IReadOnlyList<string?> paths,
        DropZone zone,
        string? currentReference,
        IReadOnlyCollection<string> currentEncodes)
    {
        var rejected = new List<RejectedPath>();
        var duplicates = new List<string>();
        var files = new List<string>();

        foreach (string? path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                rejected.Add(new RejectedPath(path ?? string.Empty, NonLocalReason));
                continue;
            }

            string full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                files.AddRange(ScanFolder(full));
                continue;
            }

            if (!File.Exists(full))
            {
                rejected.Add(new RejectedPath(full, Loc.T("drop.notFound")));
                continue;
            }

            if (!IsVideo(full))
            {
                string extension = Path.GetExtension(full);
                rejected.Add(new RejectedPath(full, extension.Length > 0 ? Loc.T("drop.notVideoExtension", extension) : Loc.T("drop.notVideo")));
                continue;
            }

            files.Add(full);
        }

        // De-duplicate against what is loaded and within this drop.
        var known = new HashSet<string>(currentEncodes.Select(Path.GetFullPath), StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(currentReference))
        {
            known.Add(Path.GetFullPath(currentReference));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        foreach (string file in files)
        {
            if (!seen.Add(file))
            {
                duplicates.Add(file);
            }
            else
            {
                unique.Add(file);
            }
        }

        bool hasReference = !string.IsNullOrWhiteSpace(currentReference);
        bool takesReference = zone == DropZone.Reference || (zone == DropZone.Auto && !hasReference);

        string? reference = null;
        var encodes = new List<string>();

        foreach (string file in unique)
        {
            if (takesReference && reference is null)
            {
                // Re-dropping the current reference on its own zone is a no-op, not a replacement.
                if (hasReference && string.Equals(file, Path.GetFullPath(currentReference!), StringComparison.Ordinal))
                {
                    duplicates.Add(file);
                    takesReference = false;
                    continue;
                }

                reference = file;
                continue;
            }

            if (known.Contains(file))
            {
                duplicates.Add(file);
                continue;
            }

            encodes.Add(file);
        }

        // A file that is already an encode can become the reference; it then leaves the encodes.
        int remainingEncodes = currentEncodes.Count(e => reference is null ||
            !string.Equals(Path.GetFullPath(e), reference, StringComparison.Ordinal));

        return new DropPlan
        {
            Reference = reference,
            Encodes = encodes,
            Rejected = rejected,
            Duplicates = duplicates,
            NeedsConfirm = reference is not null && hasReference && remainingEncodes > 0,
            ReferenceOverflowed = zone == DropZone.Reference && reference is not null && encodes.Count > 0,
        };
    }

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>Video files directly inside a folder, sorted by name. Subfolders are not walked.</summary>
    private static IEnumerable<string> ScanFolder(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(IsVideo)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
