using ReBackup.Shared.IO;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Plans;

/// <summary>The bucket an S3 connection opens, as plan validation compares them (Core never sees the connection itself).</summary>
public sealed record ConnectionBucket(string Bucket, string Region);

/// <summary>
/// The root a Google Drive connection opens, as plan validation compares them: the Drive id of the root (a shared drive or a
/// folder) and the shared drive it lives in (<c>null</c> for My Drive).
/// </summary>
public sealed record DriveRoot(string RootId, string? SharedDriveId);

public static class PlanValidator
{
    /// <summary>The kind of a location on an S3 connection (the App's <c>S3StorageFactory.Kind</c>).</summary>
    public const string S3Kind = "s3";

    /// <summary>The kind of a location on a Google Drive connection (the App's <c>DriveStorageFactory.Kind</c>).</summary>
    public const string DriveKind = "gdrive";

    /// <summary>What is wrong with the plan (keys under <c>core.plan</c>); empty when it can be saved.</summary>
    /// <param name="connections">
    /// Looks an S3 connection's bucket up by connection id: <c>null</c> when there is no such connection; an
    /// <see cref="IOException"/> (e.g. <see cref="StorageIOException"/>) when the connections cannot be read. Without it,
    /// every S3 connection counts as missing.
    /// </param>
    /// <param name="driveConnections">
    /// Looks a Google Drive connection's root up by connection id, like <paramref name="connections"/>. Without it, every Drive
    /// connection counts as missing.
    /// </param>
    public static IReadOnlyList<Message> Validate(BackupPlan plan, IEnumerable<BackupPlan> allPlans,
        Func<string, ConnectionBucket?>? connections = null, Func<string, DriveRoot?>? driveConnections = null)
    {
        var errors = new List<Message>();
        ValidateName(plan, allPlans, errors);

        ValidateLocations(plan, connections, driveConnections, errors);

        for (var i = 0; i < plan.Retention.Count; i++)
        {
            if (RetentionRules.Validate(plan.Retention[i]) is { } problem)
                errors.Add(Message.Of("core.plan.retentionRule", ("index", i + 1), ("problem", problem)));
        }

        for (var i = 0; i < plan.Triggers.Count; i++)
        {
            if (ScheduleTriggers.Validate(plan.Triggers[i]) is { } problem)
                errors.Add(Message.Of("core.plan.trigger", ("index", i + 1), ("problem", problem)));
        }

        return errors;
    }

    /// <summary>
    /// Folders: absolute paths, an existing source, neither inside the other. S3: an existing connection and a valid prefix
    /// (<c>""</c> = the whole bucket); on the same bucket and region neither prefix may be equal to or inside the other.
    /// Google Drive: an existing connection and a valid path (<c>""</c> = the root); a target must be on a shared drive; on the
    /// same root neither path may be equal to or inside the other. Locations of different kinds never overlap. Other kinds are
    /// not supported. Nothing is opened.
    /// </summary>
    private static void ValidateLocations(BackupPlan plan, Func<string, ConnectionBucket?>? connections,
        Func<string, DriveRoot?>? driveConnections, List<Message> errors)
    {
        var lookup = new ConnectionLookup<ConnectionBucket>(connections, "core.plan.connectionsUnreadable", errors);
        var driveLookup = new ConnectionLookup<DriveRoot>(driveConnections, "core.plan.driveConnectionsUnreadable", errors);
        var source = ValidateLocation(plan.Source, "source", lookup, driveLookup, errors);
        if (source is { IsFolder: true } && !Directory.Exists(plan.Source.Path))
            errors.Add(Message.Of("core.plan.sourceMissing"));
        var target = ValidateLocation(plan.Target, "target", lookup, driveLookup, errors);
        if (target?.Drive is { SharedDriveId: null })
        {
            // A service account has no storage of its own: it can only write to a shared drive.
            errors.Add(Message.Of("core.plan.targetNotSharedDrive"));
            target = null;
        }
        if (source is null || target is null ||
            Nesting(plan.Source, source, plan.Target, target) is not (var targetInSource, var sourceInTarget))
            return;

        if (targetInSource)
            errors.Add(Message.Of("core.plan.targetInsideSource"));
        else if (sourceInTarget)
            errors.Add(Message.Of("core.plan.sourceInsideTarget"));
    }

    /// <summary>
    /// Whether two locations are the same or one lies inside the other, by the rules <see cref="Validate"/> applies to a
    /// plan's source and target. Nothing is opened. A location that is not complete (no absolute folder path, no
    /// connection) overlaps nothing. S3 and Drive locations are compared by the bucket or root the lookups give; without a
    /// lookup, or when it does not know the connection or cannot be read, two locations on the same connection are still
    /// compared by their paths.
    /// </summary>
    public static bool Overlaps(StorageLocation a, StorageLocation b, Func<string, ConnectionBucket?>? connections = null,
        Func<string, DriveRoot?>? driveConnections = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Resolve(a, connections, driveConnections) is { } first &&
               Resolve(b, connections, driveConnections) is { } second &&
               Nesting(a, first, b, second) is (var bInA, var aInB) && (bInA || aInB);
    }

    /// <summary>
    /// Whether <paramref name="targetLocation"/> is <paramref name="sourceLocation"/> or lies inside it, and the other way
    /// round; null when the two cannot overlap (different kinds, buckets or roots).
    /// </summary>
    private static (bool TargetInSource, bool SourceInTarget)? Nesting(StorageLocation sourceLocation, CheckedLocation source,
        StorageLocation targetLocation, CheckedLocation target)
    {
        if (source.IsFolder && target.IsFolder)
            return (PathUtil.IsSameOrInside(targetLocation.Path, sourceLocation.Path),
                PathUtil.IsSameOrInside(sourceLocation.Path, targetLocation.Path));
        if ((source.Bucket is { } sourceBucket && target.Bucket is { } targetBucket && SameBucket(sourceBucket, targetBucket)) ||
            (source.Drive is { } sourceRoot && target.Drive is { } targetRoot && SameRoot(sourceRoot, targetRoot)))
            return (IsSameOrInside(targetLocation.Path, sourceLocation.Path),
                IsSameOrInside(sourceLocation.Path, targetLocation.Path));
        if (source.Drive is { } sourceDrive && target.Drive is { } targetDrive &&
            sourceDrive.SharedDriveId is { } sharedDrive &&
            string.Equals(sharedDrive, targetDrive.SharedDriveId, StringComparison.Ordinal))
        {
            // Different roots on one shared drive: without asking Drive, only a side that is the whole shared drive is
            // known to hold the other (a folder root's place on the drive is not known here).
            return (IsWholeSharedDrive(sourceDrive, sourceLocation.Path), IsWholeSharedDrive(targetDrive, targetLocation.Path));
        }
        return null;
    }

    /// <summary>
    /// The location as <see cref="Overlaps"/> compares it; null when it is not complete. A connection the lookup does not
    /// give stands for a bucket or root of its own, known only by the connection's id.
    /// </summary>
    private static CheckedLocation? Resolve(StorageLocation location, Func<string, ConnectionBucket?>? connections,
        Func<string, DriveRoot?>? driveConnections)
    {
        if (location.IsFileSystem)
            return !string.IsNullOrWhiteSpace(location.Path) && Path.IsPathFullyQualified(location.Path)
                ? new CheckedLocation(true, null)
                : null;
        if (string.IsNullOrWhiteSpace(location.ConnectionId))
            return null;
        var unknown = "\0connection:" + location.ConnectionId;   // no real bucket name or Drive id looks like this
        if (string.Equals(location.Kind, S3Kind, StringComparison.Ordinal))
            return new CheckedLocation(false, TryLookUp(connections, location.ConnectionId) ?? new ConnectionBucket(unknown, ""));
        if (string.Equals(location.Kind, DriveKind, StringComparison.Ordinal))
            return new CheckedLocation(false, null, TryLookUp(driveConnections, location.ConnectionId) ?? new DriveRoot(unknown, null));
        return null;
    }

    /// <summary>The lookup's answer; null without a lookup or when the connections cannot be read.</summary>
    private static T? TryLookUp<T>(Func<string, T?>? lookup, string id) where T : class
    {
        try
        {
            return lookup?.Invoke(id);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>A location that passed its own checks: a folder, the bucket of an S3 location or the root of a Drive location.</summary>
    private sealed record CheckedLocation(bool IsFolder, ConnectionBucket? Bucket, DriveRoot? Drive = null);

    /// <param name="side"><c>"source"</c> or <c>"target"</c>, the middle part of the message keys.</param>
    private static CheckedLocation? ValidateLocation(StorageLocation location, string side, ConnectionLookup<ConnectionBucket> lookup,
        ConnectionLookup<DriveRoot> driveLookup, List<Message> errors)
    {
        if (location.IsFileSystem)
            return ValidatePath(location.Path, $"core.plan.{side}Required", $"core.plan.{side}NotAbsolute", errors)
                ? new CheckedLocation(true, null)
                : null;
        if (string.Equals(location.Kind, S3Kind, StringComparison.Ordinal))
        {
            var ok = ValidateConnection(location, lookup, $"core.plan.{side}Connection", out var bucket, errors);
            ok &= ValidateStoragePath(location.Path, $"core.plan.{side}PrefixInvalid", errors);
            return ok ? new CheckedLocation(false, bucket) : null;
        }
        if (string.Equals(location.Kind, DriveKind, StringComparison.Ordinal))
        {
            var ok = ValidateConnection(location, driveLookup, $"core.plan.{side}DriveConnection", out var root, errors);
            ok &= ValidateStoragePath(location.Path, $"core.plan.{side}DrivePathInvalid", errors);
            return ok ? new CheckedLocation(false, null, root) : null;
        }

        errors.Add(Message.Of("core.plan.unsupportedLocation"));
        return null;
    }

    /// <summary>The connection is named and exists; <paramref name="keyPrefix"/> + <c>Required</c> / <c>Missing</c> say what is wrong.</summary>
    private static bool ValidateConnection<T>(StorageLocation location, ConnectionLookup<T> lookup, string keyPrefix, out T? found,
        List<Message> errors) where T : class
    {
        found = null;
        if (string.IsNullOrWhiteSpace(location.ConnectionId))
        {
            errors.Add(Message.Of(keyPrefix + "Required"));
            return false;
        }
        switch (lookup.Find(location.ConnectionId, out found))
        {
            case false:
                errors.Add(Message.Of(keyPrefix + "Missing"));
                return false;
            case null:
                return false;   // the connections cannot be read; the lookup reported it once
            default:
                return true;
        }
    }

    private static bool ValidateStoragePath(string path, string invalidKey, List<Message> errors)
    {
        try
        {
            StoragePath.Validate(path);
            return true;
        }
        catch (ArgumentException)
        {
            errors.Add(Message.Of(invalidKey));
            return false;
        }
    }

    /// <summary>Whether the location is a whole shared drive: its root is the shared drive itself and its path is empty.</summary>
    private static bool IsWholeSharedDrive(DriveRoot root, string path) =>
        path.Length == 0 && string.Equals(root.RootId, root.SharedDriveId, StringComparison.Ordinal);

    /// <summary>Whether two Drive connections open the same root (the same folder or shared drive).</summary>
    private static bool SameRoot(DriveRoot a, DriveRoot b) =>
        string.Equals(a.RootId, b.RootId, StringComparison.Ordinal) &&
        string.Equals(a.SharedDriveId, b.SharedDriveId, StringComparison.Ordinal);

    /// <summary>Whether two connections open the same bucket: S3 bucket names are global, so the region does not tell buckets apart.</summary>
    private static bool SameBucket(ConnectionBucket a, ConnectionBucket b) =>
        string.Equals(a.Bucket, b.Bucket, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the storage path <paramref name="path"/> is <paramref name="container"/> or lies below it (<c>""</c> holds everything).</summary>
    private static bool IsSameOrInside(string path, string container) =>
        container.Length == 0 ||
        string.Equals(path, container, StringComparison.Ordinal) ||
        path.StartsWith(container + "/", StringComparison.Ordinal);

    /// <summary>A connection lookup of one validation: connections that cannot be read are reported once.</summary>
    private sealed class ConnectionLookup<T>(Func<string, T?>? connections, string unreadableKey, List<Message> errors) where T : class
    {
        private bool _failed;

        /// <summary>True with the value; false when the connection does not exist; null when the connections cannot be read.</summary>
        public bool? Find(string id, out T? value)
        {
            value = null;
            if (connections is null)
                return false;
            if (_failed)
                return null;
            try
            {
                value = connections(id);
                return value is not null;
            }
            catch (IOException ex)
            {
                _failed = true;
                errors.Add(Message.Of(unreadableKey, ("error", ex.Message)));
                return null;
            }
        }
    }

    private static void ValidateName(BackupPlan plan, IEnumerable<BackupPlan> allPlans, List<Message> errors)
    {
        var problems = NameErrors(plan.Name);
        errors.AddRange(problems);
        if (problems.Count == 0 &&
            allPlans.Any(p => p.Id != plan.Id && string.Equals(p.Name, plan.Name, StringComparison.OrdinalIgnoreCase)))
            errors.Add(Message.Of("core.plan.nameTaken", ("name", plan.Name)));
    }

    /// <summary>What is wrong with a plan name on its own (uniqueness is not checked). Empty when it can be used.</summary>
    public static IReadOnlyList<Message> NameErrors(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return [Message.Of("core.plan.nameRequired")];

        var errors = new List<Message>();
        if (name != name.Trim())
            errors.Add(Message.Of("core.plan.nameSpaces"));
        if (name.EndsWith('.'))
            errors.Add(Message.Of("core.plan.nameDot"));
        if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            errors.Add(Message.Of("core.plan.namePartial"));
        if (name.EndsWith(".deleting", StringComparison.OrdinalIgnoreCase))
            errors.Add(Message.Of("core.plan.nameDeleting"));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add(Message.Of("core.plan.nameChars"));
        return errors;
    }

    private static bool ValidatePath(string path, string requiredKey, string absoluteKey, List<Message> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(Message.Of(requiredKey));
            return false;
        }
        if (!Path.IsPathFullyQualified(path))
        {
            errors.Add(Message.Of(absoluteKey));
            return false;
        }
        return true;
    }
}
