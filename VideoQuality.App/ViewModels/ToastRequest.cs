namespace VideoQuality.App.ViewModels;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A transient message for the view to show, optionally with one action (e.g. Undo).</summary>
public sealed record ToastRequest(ToastKind Kind, string Message, string? ActionLabel = null, Action? Action = null);
