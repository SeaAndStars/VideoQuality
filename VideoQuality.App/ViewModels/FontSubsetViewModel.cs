using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core.Fonts;

namespace VideoQuality.App.ViewModels;

/// <summary>One subset font in the result table.</summary>
public sealed record SubsetFontRow(string NewFamily, string Originals, string SourceFile, int Characters, string Sizes, double Ratio);

/// <summary>A warning line in the report, with a severity for its icon.</summary>
public sealed record SubsetNote(ToastKind Kind, string Text);

/// <summary>Font Subset page: scripts + font folder → renamed scripts and subset fonts.</summary>
public partial class FontSubsetViewModel : ObservableObject
{
    public ObservableCollection<string> Scripts { get; } = [];

    public ObservableCollection<SubsetFontRow> Fonts { get; } = [];

    public ObservableCollection<SubsetNote> Notes { get; } = [];

    [ObservableProperty]
    public partial string FontDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Drop .ass scripts (or their folder) to start.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyCanExecuteChangedFor(nameof(SendToMuxCommand))]
    public partial FontSubsetReport? Result { get; set; }

    public bool HasScripts => Scripts.Count > 0;

    public bool HasResult => Result is not null;

    /// <summary>Raised by "Send to Mux" with the renamed scripts and the subset fonts.</summary>
    public event EventHandler<(IReadOnlyList<string> Scripts, IReadOnlyList<string> Fonts)>? SendToMuxRequested;

    public event EventHandler<ToastRequest>? ToastRequested;

    public FontSubsetViewModel()
    {
        Scripts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasScripts));
            RunCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Adds scripts from files and folders; fills in the font and output folders.</summary>
    public void AddPaths(IEnumerable<string?> paths)
    {
        foreach (string path in paths.OfType<string>())
        {
            if (Directory.Exists(path))
            {
                foreach (string script in Directory.EnumerateFiles(path, "*.ass").Concat(Directory.EnumerateFiles(path, "*.ssa"))
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    AddScript(script);
                }
            }
            else if (File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".ass" or ".ssa")
            {
                AddScript(path);
            }
            else if (File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc")
            {
                FontDirectory = Path.GetDirectoryName(path)!;
            }
        }
    }

    private void AddScript(string path)
    {
        string full = Path.GetFullPath(path);
        if (Scripts.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Scripts.Add(full);
        string folder = Path.GetDirectoryName(full)!;
        if (FontDirectory.Length == 0)
        {
            FontDirectory = new[] { "Fonts", "fonts", "Font", "字体" }
                .Select(name => Path.Combine(folder, name))
                .FirstOrDefault(Directory.Exists) ?? folder;
        }

        if (OutputDirectory.Length == 0)
        {
            OutputDirectory = Path.Combine(folder, "output");
        }

        Status = $"{Scripts.Count} script(s) ready.";
    }

    [RelayCommand]
    private void RemoveScript(string path) => Scripts.Remove(path);

    [RelayCommand]
    private void Clear()
    {
        Scripts.Clear();
        Fonts.Clear();
        Notes.Clear();
        Result = null;
        FontDirectory = string.Empty;
        OutputDirectory = string.Empty;
        Status = "Drop .ass scripts (or their folder) to start.";
    }

    private bool CanRun() => !IsBusy && Scripts.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsBusy = true;
        Fonts.Clear();
        Notes.Clear();
        Result = null;
        var options = new FontSubsetOptions
        {
            Scripts = Scripts.ToList(),
            FontDirectories = [FontDirectory],
            OutputDirectory = OutputDirectory,
        };

        var progress = new Progress<string>(message => Status = message);
        try
        {
            FontSubsetReport report = await Task.Run(() => FontSubsetter.Run(options, progress));
            Show(report);
            ToastRequested?.Invoke(this, report.Succeeded
                ? new ToastRequest(ToastKind.Success, $"Subset {report.Fonts.Count} font(s) for {report.ScriptsWritten.Count} script(s).")
                : new ToastRequest(ToastKind.Warning, $"{report.MissingFonts.Count} font(s) are missing; see the report."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = "Failed: " + ex.Message;
            ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Error, "Font subset failed: " + ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(FontSubsetReport report)
    {
        Result = report;
        foreach (SubsetFont font in report.Fonts)
        {
            Fonts.Add(new SubsetFontRow(
                font.NewFamily,
                string.Join(", ", font.ScriptNames),
                $"{Path.GetFileName(font.SourcePath)}{(font.SourceFace > 0 ? $" #{font.SourceFace}" : string.Empty)}",
                font.Characters,
                $"{Size(font.SourceBytes)} → {Size(font.SubsetBytes)}",
                font.SourceBytes > 0 ? font.SubsetBytes / (double)font.SourceBytes : 0));
        }

        foreach (FontRequest missing in report.MissingFonts)
        {
            Notes.Add(new SubsetNote(ToastKind.Error, $"Missing font: {missing} — no file in the font folder provides it."));
        }

        foreach ((FontRequest request, FontMatch match) in report.Synthesized)
        {
            string what = (match.FauxBold, match.FauxItalic) switch
            {
                (true, true) => "bold and italic",
                (true, false) => "bold",
                _ => "italic",
            };
            Notes.Add(new SubsetNote(ToastKind.Warning, $"{request}: no real {what} face; the renderer will synthesise it from {match.Face}."));
        }

        foreach ((string font, IReadOnlyList<int> codepoints) in report.MissingGlyphs)
        {
            string sample = string.Join(" ", codepoints.Take(8).Select(c => $"{char.ConvertFromUtf32(c)} U+{c:X4}"));
            Notes.Add(new SubsetNote(ToastKind.Warning, $"{font} has no glyph for {codepoints.Count} character(s): {sample}{(codepoints.Count > 8 ? " …" : string.Empty)}"));
        }

        foreach (string unused in report.UnusedStyleFonts)
        {
            Notes.Add(new SubsetNote(ToastKind.Info, $"{unused}: named by a style but never drawn; not shipped."));
        }

        foreach ((string path, string error) in report.UnreadableFonts)
        {
            Notes.Add(new SubsetNote(ToastKind.Warning, $"Could not read {Path.GetFileName(path)}: {error}"));
        }

        long before = report.Fonts.Sum(f => f.SourceBytes);
        long after = report.Fonts.Sum(f => f.SubsetBytes);
        Status = $"{report.Fonts.Count} font(s), {Size(before)} → {Size(after)}; scripts in {OutputDirectory}.";
    }

    private bool CanSendToMux() => Result is { Fonts.Count: > 0 } or { ScriptsWritten.Count: > 0 };

    [RelayCommand(CanExecute = nameof(CanSendToMux))]
    private void SendToMux()
    {
        if (Result is { } report)
        {
            SendToMuxRequested?.Invoke(this, (report.ScriptsWritten, report.Fonts.Select(f => f.OutputPath).ToList()));
        }
    }

    internal static string Size(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
}
