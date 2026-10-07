using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Reflection;
using System.Windows.Markup;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Settings;

namespace ReBackup.Shared.Wpf.Localization;

/// <summary>
/// One source of label files: the assembly that embeds them and the resource name prefix; the file of a language is
/// <c>ResourcePrefix + language + ".json"</c> (e.g. <c>ReBackup.App.Locales.</c> + <c>de-DE</c>).
/// </summary>
public sealed record LabelSource(Assembly Assembly, string ResourcePrefix);

/// <summary>
/// The labels of the chosen language (label files, embedded in the assemblies the app registers with
/// <see cref="Configure"/>). XAML binds to the indexer through
/// <see cref="LocExtension"/>; <see cref="Apply"/> raises <c>Item[]</c>, so every bound text follows at once, and then
/// <see cref="LanguageChanged"/>, after which view models raise the texts they build in code again. UI thread only.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly Dictionary<string, LabelSet> Sets = new(StringComparer.Ordinal);
    private static IReadOnlyList<LabelSource> _sources = [];
    private static IReadOnlyList<Func<string, Message?>> _recognizers = [];
    private static bool _configured;
    /// <summary>The English labels; an empty set (every text shows its key) until <see cref="Configure"/> or when even they cannot be read.</summary>
    private static LabelSet _englishSet = LabelSet.Parse("{}");
    private static bool _windowHookRegistered;

    private Labels _labels = new(_englishSet, _englishSet, CultureInfo.GetCultureInfo(AppLanguages.English));

    private Loc()
    {
    }

    public static Loc Instance { get; } = new();

    /// <summary>
    /// Registers where the label files are and which recognizers turn stored English texts back into messages. Called
    /// once on startup, before <see cref="Apply"/>. A label of a later source overrides the same key of an earlier one.
    /// </summary>
    /// <param name="sources">The label files, shared ones first, the app's last.</param>
    /// <param name="recognizers">Tried in order by <see cref="Known"/>; a nested text is recognized through all of them.</param>
    public static void Configure(IReadOnlyList<LabelSource> sources, IReadOnlyList<Func<string, Message?>> recognizers)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(recognizers);
        if (_configured)
            throw new InvalidOperationException("Loc is configured once, on startup.");

        _configured = true;
        _sources = sources.ToArray();
        _recognizers = recognizers.ToArray();
        _englishSet = TryLoad(AppLanguages.English) ?? LabelSet.Parse("{}");
        Instance._labels = new Labels(_englishSet, _englishSet, CultureInfo.GetCultureInfo(AppLanguages.English));
    }

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

    /// <summary>
    /// A text Core wrote in English (a run log reason, warning or skip reason) or raised (an exception message): in
    /// the applied language when it is recognized, otherwise as it is (e.g. a message from Windows).
    /// </summary>
    public static string Known(string? text) =>
        string.IsNullOrEmpty(text) ? "" : MessageRecognizers.Recognize(text, _recognizers) is { } message ? F(message) : text;

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
        _labels = new Labels(_englishSet, SetFor(tag), culture);
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

    /// <summary>The labels of <paramref name="language"/>; English when its file is missing or broken.</summary>
    private static LabelSet SetFor(string language)
    {
        if (language == AppLanguages.English)
            return _englishSet;
        if (Sets.TryGetValue(language, out var set))
            return set;
        set = TryLoad(language) ?? _englishSet;
        Sets[language] = set;
        return set;
    }

    /// <summary>
    /// The embedded label files of <paramref name="language"/> from every source, merged (a later source wins on a
    /// key); null when none of them can be read. A missing or broken file is left out, its labels fall back to English.
    /// </summary>
    private static LabelSet? TryLoad(string language)
    {
        LabelSet? merged = null;
        foreach (var source in _sources)
        {
            try
            {
                using var stream = source.Assembly.GetManifestResourceStream(source.ResourcePrefix + language + ".json");
                if (stream is null)
                    continue;
                var set = LabelSet.Parse(stream);
                merged = merged is null ? set : merged.Merge(set);
            }
            catch (Exception ex) when (ex is FormatException or IOException or DecoderFallbackException)
            {
                System.Diagnostics.Debug.WriteLine($"The label file {source.ResourcePrefix}{language}.json cannot be read: {ex.Message}");
            }
        }
        return merged;
    }
}
