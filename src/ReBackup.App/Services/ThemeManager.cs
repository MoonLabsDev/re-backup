using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using ReBackup.Core.Settings;
using ThemeMode = ReBackup.Core.Settings.ThemeMode;   // not System.Windows.ThemeMode (WPF Fluent)

namespace ReBackup.App.Services;

/// <summary>
/// Switches the app between the dark and the light palette at runtime. The palette is the merged dictionary
/// Theme/Colors.Dark.xaml or Theme/Colors.Light.xaml in <see cref="Application.Resources"/>; it is replaced as a whole
/// (fonts, icons and control styles stay), so everything that uses the colours as DynamicResource follows at once.
/// Code that reads colours itself listens to <see cref="ThemeChanged"/>. UI thread only.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DarkFile = "Colors.Dark.xaml";
    private const string LightFile = "Colors.Light.xaml";

    private static ResourceDictionary? _dark;
    private static ResourceDictionary? _light;
    private static bool _listening;
    private static readonly List<WeakReference<FrameworkElement>> Detached = [];

    /// <summary>The chosen mode. Until the first <see cref="Apply"/> the dark palette of App.xaml is in use.</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    /// <summary>True while the dark palette is applied (also for System when Windows uses dark apps).</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised on the UI thread after the mode or the applied palette changed.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>Applies <paramref name="mode"/> now; System follows the Windows app mode until another mode is chosen.</summary>
    public static void Apply(ThemeMode mode)
    {
        var application = Application.Current ?? throw new InvalidOperationException("No WPF application is running.");
        application.Dispatcher.VerifyAccess();

        var modeChanged = mode != Mode;
        Mode = mode;
        ListenToSystem(mode == ThemeMode.System, application);
        SetPalette(application, ResolveDark(mode), modeChanged);
    }

    /// <summary>
    /// Keeps an element tree that lives outside every window (the tray menu) on the current palette. A palette swap
    /// only reaches the trees of the app's windows; such an element gets the palette in its own resources instead,
    /// replaced on every switch, which re-resolves all its DynamicResource references (templates included).
    /// </summary>
    public static void Follow(FrameworkElement detachedRoot)
    {
        Detached.Add(new WeakReference<FrameworkElement>(detachedRoot));
        if (CurrentPalette() is { } palette)
            GivePalette(detachedRoot, palette);
    }

    /// <summary>Whether <paramref name="mode"/> means the dark palette right now.</summary>
    public static bool ResolveDark(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => !WindowsUsesLightApps(),
    };

    /// <summary>HKCU …\Themes\Personalize\AppsUseLightTheme: 1 = light; missing or unreadable = dark.</summary>
    public static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void SetPalette(Application application, bool dark, bool modeChanged)
    {
        var merged = application.Resources.MergedDictionaries;
        var index = FindPalette(merged);
        if (index >= 0)
            Adopt(merged[index]);   // reuse the instance App.xaml loaded

        var palette = dark ? _dark ??= Load(DarkFile) : _light ??= Load(LightFile);
        var paletteChanged = index < 0 || !ReferenceEquals(merged[index], palette);
        if (paletteChanged)
        {
            if (index < 0)
                merged.Insert(0, palette);
            else
                merged[index] = palette;
        }
        IsDark = dark;

        if (paletteChanged)
        {
            Detached.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in Detached)
            {
                if (reference.TryGetTarget(out var element))
                    GivePalette(element, palette);
            }

            foreach (Window window in application.Windows)
            {
                if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
                    DarkTitleBar.Apply(window, dark);
            }
        }
        if (paletteChanged || modeChanged)
            ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void GivePalette(FrameworkElement element, ResourceDictionary palette)
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(palette);
        element.Resources = resources;   // a new dictionary: every reference to its keys is resolved again
    }

    private static ResourceDictionary? CurrentPalette()
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null)
            return null;
        var index = FindPalette(merged);
        return index < 0 ? null : merged[index];
    }

    private static int FindPalette(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (ReferenceEquals(merged[i], _dark) || ReferenceEquals(merged[i], _light) || FileOf(merged[i]) is not null)
                return i;
        }
        return -1;
    }

    private static void Adopt(ResourceDictionary dictionary)
    {
        switch (FileOf(dictionary))
        {
            case DarkFile:
                _dark ??= dictionary;
                break;
            case LightFile:
                _light ??= dictionary;
                break;
        }
    }

    private static string? FileOf(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        if (source is null)
            return null;
        if (source.EndsWith(DarkFile, StringComparison.OrdinalIgnoreCase))
            return DarkFile;
        if (source.EndsWith(LightFile, StringComparison.OrdinalIgnoreCase))
            return LightFile;
        return null;
    }

    private static ResourceDictionary Load(string file) =>
        new() { Source = new Uri($"pack://application:,,,/ReBackup.App;component/Theme/{file}", UriKind.Absolute) };

    private static void ListenToSystem(bool listen, Application application)
    {
        if (listen == _listening)
            return;
        _listening = listen;
        if (listen)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            application.Exit += OnApplicationExit;
        }
        else
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            application.Exit -= OnApplicationExit;
        }
    }

    private static void OnApplicationExit(object? sender, ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listening = false;
    }

    /// <summary>Raised on a system events thread; the app mode switch arrives as General (and Color on some builds).</summary>
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
            return;
        var application = Application.Current;
        application?.Dispatcher.InvokeAsync(() =>
        {
            if (Mode == ThemeMode.System)
                SetPalette(application, ResolveDark(ThemeMode.System), modeChanged: false);
        });
    }
}
