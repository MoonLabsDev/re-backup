using System.Security.Cryptography;
using System.Text;

namespace ReBackup.Storage.S3.Connections;

/// <summary>Protects secrets with Windows DPAPI, bound to the current Windows account.</summary>
public static class SecretProtector
{
    private const string NotWindows = "Secrets are protected with Windows DPAPI.";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ReBackup.S3.v1");

    /// <summary>Encrypts <paramref name="secret"/> and returns the DPAPI blob as Base64.</summary>
    public static string Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(NotWindows);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a blob made by <see cref="Protect"/>; <c>null</c> when it is <c>null</c>, not valid Base64 or cannot
    /// be decrypted by this account.
    /// </summary>
    public static string? TryUnprotect(string? protectedSecret)
    {
        if (protectedSecret is null) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(NotWindows);
        try
        {
            var blob = Convert.FromBase64String(protectedSecret);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
