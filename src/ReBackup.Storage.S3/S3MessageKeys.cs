namespace ReBackup.Storage.S3;

/// <summary>Locale keys of the texts the UI shows for <see cref="S3CheckResult"/>s. The texts live in the UI's locale files.</summary>
public static class S3MessageKeys
{
    public const string Ok = "s3.check.ok";
    public const string RegionMismatch = "s3.check.regionMismatch";
    public const string RegionInvalid = "s3.check.regionInvalid";
    public const string AccessDenied = "s3.check.accessDenied";
    public const string NotFound = "s3.check.notFound";
    public const string Unavailable = "s3.check.unavailable";
    public const string Failed = "s3.check.failed";
    public const string LifecycleMissing = "s3.check.lifecycleMissing";
    public const string VersioningEnabled = "s3.check.versioningEnabled";
    public const string NotCheckable = "s3.check.notCheckable";
    public const string Skipped = "s3.check.skipped";
}
