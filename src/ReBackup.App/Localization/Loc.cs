using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using ReBackup.Core.Localization;
using ReBackup.Core.Settings;

namespace ReBackup.App.Localization;

/// <summary>
/// The labels of the chosen language (Locales/*.json, embedded). XAML binds to the indexer through
/// <see cref="LocExtension"/>; <see cref="Apply"/> raises <c>Item[]</c>, so every bound text follows at once, and then
/// <see cref="LanguageChanged"/>, after which view models raise the texts they build in code again. UI thread only.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private const string ResourcePrefix = "ReBackup.App.Locales.";
    private static readonly Dictionary<string, LabelSet> Sets = new(StringComparer.Ordinal);
    private static readonly LabelSet EnglishSet = SetFor(AppLanguages.English);
    private static bool _windowHookRegistered;

    private Labels _labels = new(EnglishSet, EnglishSet, CultureInfo.GetCultureInfo(AppLanguages.English));

    private Loc()
    {
    }

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread after <see cref="Apply"/>.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>The applied language: "en-US" (until the first <see cref="Apply"/>) or "de-DE".</summary>
    public string Language { get; private set; } = AppLanguages.English;

    /// <summary>The culture of the applied language: numbers and dates.</summary>
    public static CultureInfo Culture => Instance._labels.Culture;

    /// <summary>The label <paramref name="key"/>; what <see cref="LocExtension"/> binds to.</summary>
    public string this[string key] => _labels.Get(key);

    /// <summary>The label <paramref name="key"/> as it is.</summary>
    public static string T(string key) => Instance._labels.Get(key);

    /// <summary>The label <paramref name="key"/> with its placeholders filled.</summary>
    public static string F(string key, params (string Name, object? Value)[] args) =>
        Instance._labels.Format(Message.Of(key, args));

    /// <summary>A message (e.g. from a Core validator) in the applied language.</summary>
    public static string F(Message message) => Instance._labels.Format(message);

    /// <summary>Binds a property of an element made in code (e.g. a tray menu item) to a label.</summary>
    public static void Bind(DependencyObject target, DependencyProperty property, string key) =>
        BindingOperations.SetBinding(target, property, BindingFor(key));

    internal static Binding BindingFor(string key) => new($"[{key}]") { Source = Instance, Mode = BindingMode.OneWay };

    /// <summary>
    /// Applies <paramref name="language"/> (an unsupported one as English): labels, the thread cultures, the
    /// <see cref="FrameworkElement.Language"/> of every window; then raises the change.
    /// </summary>
    public void Apply(string language)
    {
        var tag = AppLanguages.Normalize(language) ?? AppLanguages.English;
        var culture = new CultureInfo(tag);
        _labels = new Labels(EnglishSet, SetFor(tag), culture);
        Language = tag;

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (Application.Current is { } application)
        {
            RegisterWindowHook();
            var xmlLanguage = XmlLanguage.GetLanguage(tag);
            foreach (Window window in application.Windows)
                window.Language = xmlLanguage;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Windows opened later (Settings, dialogs) get the applied language when they load.</summary>
    private static void RegisterWindowHook()
    {
        if (_windowHookRegistered)
            return;
        _windowHookRegistered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ((Window)sender).Language = XmlLanguage.GetLanguage(Instance.Language)));
    }

    private static LabelSet SetFor(string language)
    {
        if (Sets.TryGetValue(language, out var set))
            return set;
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream(ResourcePrefix + language + ".json")
            ?? throw new InvalidOperationException($"The label file {language}.json is not embedded in the app.");
        set = LabelSet.Parse(stream);
        Sets[language] = set;
        return set;
    }
}
