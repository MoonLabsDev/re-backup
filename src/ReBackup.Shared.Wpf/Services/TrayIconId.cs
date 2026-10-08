using System.Security.Cryptography;
using System.Text;

namespace ReBackup.Shared.Wpf.Services;

/// <summary>
/// The id of an app's tray icon. Windows binds a tray icon id to the path of the exe that first used it, so an id that
/// only depends on the app (the tray library's default) makes a renamed or moved exe — every new versioned release —
/// fail to create its icon. The id therefore covers the app and the exe path.
/// </summary>
public static class TrayIconId
{
    /// <summary>The id for <paramref name="appId"/> running from <paramref name="exePath"/> (compared without regard to case).</summary>
    public static Guid For(string appId, string exePath)
    {
        var text = appId + "|" + exePath.ToUpperInvariant();
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return new Guid(hash);
    }

    /// <summary>The id for <paramref name="appId"/> running from this process's exe.</summary>
    public static Guid ForCurrentProcess(string appId) => For(appId, Environment.ProcessPath ?? "");
}
