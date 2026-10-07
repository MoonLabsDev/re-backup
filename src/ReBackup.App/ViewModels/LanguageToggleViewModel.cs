using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Settings;

namespace ReBackup.App.ViewModels;

/// <summary>The language button of the icon rail: shows the current flag; its menu chooses German or English.</summary>
public sealed partial class LanguageToggleViewModel : ObservableObject
{
    private readonly Func<string> _current;
    private readonly Action<string> _choose;

    /// <param name="current">The applied language.</param>
    /// <param name="choose">Applies and saves a language.</param>
    public LanguageToggleViewModel(Func<string> current, Action<string> choose)
    {
        _current = current;
        _choose = choose;
    }

    public string Language => _current();

    public bool IsGerman => Language == AppLanguages.German;

    public bool IsEnglish => Language == AppLanguages.English;

    public string ToolTip => Loc.F("language.toolTip", ("language", Loc.T("language." + Language)));

    [RelayCommand]
    private void Choose(string? language)
    {
        if (AppLanguages.Normalize(language) is { } tag && tag != Language)
            _choose(tag);
    }

    /// <summary>Call after the language changed (from here or from the settings dialog).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
