using ReBackup.Shared.IO;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Plans;

/// <summary>The bucket an S3 connection opens, as plan validation compares them (Core never sees the connection itself).</summary>
public sealed record ConnectionBucket(string Bucket, string Region);

public static class PlanValidator
{
    /// <summary>The kind of a location on an S3 connection (the App's <c>S3StorageFactory.Kind</c>).</summary>
    public const string S3Kind = "s3";

    /// <summary>What is wrong with the plan (keys under <c>core.plan</c>); empty when it can be saved.</summary>
    /// <param name="connections">
    /// Looks an S3 connection's bucket up by connection id: <c>null</c> when there is no such connection; an
    /// <see cref="IOException"/> (e.g. <see cref="StorageIOException"/>) when the connections cannot be read. Without it,
    /// every S3 connection counts as missing.
    /// </param>
    public static IReadOnlyList<Message> Validate(BackupPlan plan, IEnumerable<BackupPlan> allPlans,
        Func<string, ConnectionBucket?>? connections = null)
    {
        var errors = new List<Message>();
        ValidateName(plan, allPlans, errors);

        ValidateLocations(plan, connections, errors);

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
    /// A folder and an S3 location never overlap. Other kinds are not supported. Nothing is opened.
    /// </summary>
    private static void ValidateLocations(BackupPlan plan, Func<string, ConnectionBucket?>? connections, List<Message> errors)
    {
        var lookup = new BucketLookup(connections, errors);
        var source = ValidateLocation(plan.Source, "source", lookup, errors);
        if (source is { IsFolder: true } && !Directory.Exists(plan.Source.Path))
            errors.Add(Message.Of("core.plan.sourceMissing"));
        var target = ValidateLocation(plan.Target, "target", lookup, errors);
        if (source is null || target is null)
            return;

        bool targetInSource, sourceInTarget;
        if (source.IsFolder && target.IsFolder)
        {
            targetInSource = PathUtil.IsSameOrInside(plan.Target.Path, plan.Source.Path);
            sourceInTarget = PathUtil.IsSameOrInside(plan.Source.Path, plan.Target.Path);
        }
        else if (source.Bucket is { } sourceBucket && target.Bucket is { } targetBucket && SameBucket(sourceBucket, targetBucket))
        {
            targetInSource = IsSameOrInside(plan.Target.Path, plan.Source.Path);
            sourceInTarget = IsSameOrInside(plan.Source.Path, plan.Target.Path);
        }
        else
        {
            return;
        }

        if (targetInSource)
            errors.Add(Message.Of("core.plan.targetInsideSource"));
        else if (sourceInTarget)
            errors.Add(Message.Of("core.plan.sourceInsideTarget"));
    }

    /// <summary>A location that passed its own checks: a folder, or the bucket of an S3 location.</summary>
    private sealed record CheckedLocation(bool IsFolder, ConnectionBucket? Bucket);

    /// <param name="side"><c>"source"</c> or <c>"target"</c>, the middle part of the message keys.</param>
    private static CheckedLocation? ValidateLocation(StorageLocation location, string side, BucketLookup lookup, List<Message> errors)
    {
        if (location.IsFileSystem)
            return ValidatePath(location.Path, $"core.plan.{side}Required", $"core.plan.{side}NotAbsolute", errors)
                ? new CheckedLocation(true, null)
                : null;
        if (!string.Equals(location.Kind, S3Kind, StringComparison.Ordinal))
        {
            errors.Add(Message.Of("core.plan.unsupportedLocation"));
            return null;
        }

        ConnectionBucket? bucket = null;
        var ok = true;
        if (string.IsNullOrWhiteSpace(location.ConnectionId))
        {
            errors.Add(Message.Of($"core.plan.{side}ConnectionRequired"));
            ok = false;
        }
        else
        {
            switch (lookup.Find(location.ConnectionId, out bucket))
            {
                case false:
                    errors.Add(Message.Of($"core.plan.{side}ConnectionMissing"));
                    ok = false;
                    break;
                case null:
                    ok = false;   // the connections cannot be read; the lookup reported it once
                    break;
            }
        }

        try
        {
            StoragePath.Validate(location.Path);
        }
        catch (ArgumentException)
        {
            errors.Add(Message.Of($"core.plan.{side}PrefixInvalid"));
            ok = false;
        }
        return ok ? new CheckedLocation(false, bucket) : null;
    }

    /// <summary>Whether two connections open the same bucket: S3 bucket names are global, so the region does not tell buckets apart.</summary>
    private static bool SameBucket(ConnectionBucket a, ConnectionBucket b) =>
        string.Equals(a.Bucket, b.Bucket, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the storage path <paramref name="path"/> is <paramref name="container"/> or lies below it (<c>""</c> holds everything).</summary>
    private static bool IsSameOrInside(string path, string container) =>
        container.Length == 0 ||
        string.Equals(path, container, StringComparison.Ordinal) ||
        path.StartsWith(container + "/", StringComparison.Ordinal);

    /// <summary>The connection lookup of one validation: connections that cannot be read are reported once.</summary>
    private sealed class BucketLookup(Func<string, ConnectionBucket?>? connections, List<Message> errors)
    {
        private bool _failed;

        /// <summary>True with the bucket; false when the connection does not exist; null when the connections cannot be read.</summary>
        public bool? Find(string id, out ConnectionBucket? bucket)
        {
            bucket = null;
            if (connections is null)
                return false;
            if (_failed)
                return null;
            try
            {
                bucket = connections(id);
                return bucket is not null;
            }
            catch (IOException ex)
            {
                _failed = true;
                errors.Add(Message.Of("core.plan.connectionsUnreadable", ("error", ex.Message)));
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
