namespace ReBackup.App.Localization;

/// <summary>
/// A text built in the current language each time it is read. View models keep these instead of finished strings,
/// so after a language switch raising the property again is enough.
/// </summary>
public sealed class LocText
{
    private readonly Func<string> _build;

    public LocText(Func<string> build) => _build = build;

    public static LocText Empty { get; } = new(() => "");

    /// <summary>The label <paramref name="key"/> with its arguments.</summary>
    public static LocText Of(string key, params (string Name, object? Value)[] args) => new(() => Loc.F(key, args));

    /// <summary>A text that is the same in every language (a path, a message from Windows).</summary>
    public static LocText Raw(string text) => new(() => text);

    /// <summary>A text Core produced in English, shown through <see cref="Loc.Known"/>.</summary>
    public static LocText Known(string text) => new(() => Loc.Known(text));

    public override string ToString() => _build();
}
