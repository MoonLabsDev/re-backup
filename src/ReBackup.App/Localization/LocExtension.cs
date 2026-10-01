using System.Windows.Markup;

namespace ReBackup.App.Localization;

/// <summary>
/// <c>{l:Loc shell.tab.plan}</c>: the label in the current language; follows a language switch at once. A binding to
/// <see cref="Loc.Instance"/>, so it also works in setters, templates and on objects outside the tree (columns).
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Loc.BindingFor(Key).ProvideValue(serviceProvider);
}
