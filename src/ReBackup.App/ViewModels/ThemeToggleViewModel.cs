using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Settings;

namespace ReBackup.App.ViewModels;

/// <summary>The theme button of the icon rail: shows the current mode and toggles Dark ↔ Light.</summary>
public sealed partial class ThemeToggleViewModel : ObservableObject
{
    private readonly Func<ThemeMode> _current;
    private readonly Action<ThemeMode> _choose;

    /// <param name="current">The mode in use.</param>
    /// <param name="choose">Applies and saves a mode.</param>
    public ThemeToggleViewModel(Func<ThemeMode> current, Action<ThemeMode> choose)
    {
        _current = current;
        _choose = choose;
    }

    public ThemeMode Mode => _current();

    public string ToolTip => Loc.F("shell.rail.themeToolTip", ("theme", Loc.T("enum.theme." + Mode)));

    public string AutomationName => Loc.F("shell.rail.themeName", ("theme", Loc.T("enum.theme." + Mode)));

    public static ThemeMode Next(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => ThemeMode.Light,
        _ => ThemeMode.Dark,
    };

    [RelayCommand]
    private void Cycle() => _choose(Next(Mode));

    /// <summary>Call after the mode changed (from here or from the settings dialog).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(AutomationName));
    }
}
