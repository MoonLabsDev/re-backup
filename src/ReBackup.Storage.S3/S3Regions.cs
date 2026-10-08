using System.Text.RegularExpressions;

namespace ReBackup.Storage.S3;

/// <summary>AWS region names, checked the same way by the factory, the connection tester and the connection dialog.</summary>
public static class S3Regions
{
    /// <summary>AWS region names: <c>eu-central-1</c>, <c>us-gov-west-1</c>, <c>ap-southeast-2</c>.</summary>
    private static readonly Regex Pattern = new(@"\A[a-z]{2}(-[a-z]+)+-[0-9]{1,2}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="region"/> looks like an AWS region name (exactly: no blanks, lowercase). A hand-edited connections file
    /// or a typed region may hold anything, and the SDK either rejects that with its own exception or builds a bogus endpoint.
    /// </summary>
    public static bool IsValid(string? region) => region is not null && Pattern.IsMatch(region);
}
