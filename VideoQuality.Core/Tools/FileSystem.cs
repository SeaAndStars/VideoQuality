namespace VideoQuality.Core.Tools;

/// <summary>Small file helpers shared by the cleanup paths.</summary>
public static class FileSystem
{
    /// <summary>
    /// Deletes a temporary or partial file if it is there. A file that cannot be deleted (still open,
    /// read-only, a directory in the way) is left behind: cleanup must not turn success into failure.
    /// </summary>
    /// <returns>Whether the file is gone.</returns>
    public static bool TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Files matching <paramref name="pattern"/>; empty when the folder cannot be read.</summary>
    public static IReadOnlyList<string> SafeEnumerateFiles(string directory, string pattern)
    {
        try
        {
            return [.. Directory.EnumerateFiles(directory, pattern)];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
