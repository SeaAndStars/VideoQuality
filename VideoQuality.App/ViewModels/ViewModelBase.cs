using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoQuality.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Re-reads every bound property, so texts computed from the language catalog follow a language change.
    /// Pages with texts stored at construction override it to rebuild them.
    /// </summary>
    public virtual void RefreshTexts() => OnPropertyChanged(string.Empty);
}
