using ReBackup.Shared.Wpf.Services;

namespace ReBackup.Shared.Wpf.Tests.Fakes;

/// <summary>A Run key in memory: the tests never touch the real registry.</summary>
public sealed class InMemoryRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetValue(string name, string value) => Values[name] = value;

    public void DeleteValue(string name) => Values.Remove(name);
}
