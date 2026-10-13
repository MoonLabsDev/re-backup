using ReBackup.Storage.GoogleDrive.Connections;

namespace ReBackup.Storage.GoogleDrive;

/// <summary>How a <see cref="DriveConnectionTester"/> run ended.</summary>
public enum DriveTestOutcome
{
    /// <summary>The location can be used (as a target: written and deleted).</summary>
    Ok,

    /// <summary>The service account may not open, write or delete there, or its key is not valid.</summary>
    AccessDenied,

    /// <summary>The root or the path does not exist (or is not shared with the service account), or the path is a file.</summary>
    NotFound,

    /// <summary>A target whose root is not on a shared drive (a service account has no storage of its own).</summary>
    NotSharedDrive,

    /// <summary>Anything else: Drive not reachable, an ambiguous name, the shared drive full, …</summary>
    Error,
}

/// <summary>The outcome of a test and a text for the user (English; it names the service account's e-mail where sharing helps).</summary>
public sealed record DriveTestResult(DriveTestOutcome Outcome, string Message);

/// <summary>
/// Probes a Drive connection and a path below its root: the root is opened, the path resolved and listed; for a target the root
/// must be on a shared drive, and a probe file is written into the path (or, while the path does not exist yet, into its deepest
/// existing folder) and deleted again.
/// </summary>
public sealed class DriveConnectionTester
{
    private const string ProbePrefix = ".rebackup-connection-test-";

    private readonly Func<DriveConnection, IDriveApi> _apiFactory;
    private readonly TimeProvider _time;

    /// <param name="apiFactory">Builds the API client for a run (tests); the tester disposes it when it is <see cref="IDisposable"/>. Default: a <see cref="GoogleDriveApi"/>.</param>
    /// <param name="time">The clock retry waits run on.</param>
    public DriveConnectionTester(Func<DriveConnection, IDriveApi>? apiFactory = null, TimeProvider? time = null)
    {
        _apiFactory = apiFactory ?? (connection => new GoogleDriveApi(connection));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Tests <paramref name="path"/> (a storage path below the connection's root, <c>""</c> = the root) as a source or, with
    /// <paramref name="asTarget"/>, as a target. Throws <see cref="ArgumentException"/> for a path that is no valid storage path and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<DriveTestResult> RunAsync(DriveConnection connection, string path, bool asTarget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        StoragePath.Validate(path);
        ct.ThrowIfCancellationRequested();

        var api = _apiFactory(connection);
        try
        {
            return await RunAsync(api, connection, path, asTarget, ct).ConfigureAwait(false);
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }
    }

    private async Task<DriveTestResult> RunAsync(IDriveApi api, DriveConnection connection, string path, bool asTarget, CancellationToken ct)
    {
        var rootName = connection.Info.RootName;
        var email = connection.ClientEmail;

        DriveItem root;
        try
        {
            root = await new DriveRetry(_time).RunAsync(c => api.GetAsync(connection.Info.RootId, c), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(Map(ex, "", ct), Step.Root, connection, "");
        }
        if (!root.IsFolder)
            return new DriveTestResult(DriveTestOutcome.NotFound, $"'{rootName}' is a file, not a folder or shared drive.");
        if (asTarget && root.DriveId is null)
            return new DriveTestResult(DriveTestOutcome.NotSharedDrive,
                $"'{rootName}' is not on a shared drive. Backups can only be written to a shared drive: a service account has no storage of its own. " +
                $"Choose a shared drive that {email} is a member of.");

        var storage = new DriveStorage(api, connection.Info.RootId, _time);

        // The folder to list and probe: the path itself, or for a target that does not exist yet its deepest existing folder.
        var folder = path;
        var missing = false;
        try
        {
            while (folder.Length > 0)
            {
                var entry = await storage.StatAsync(folder, ct).ConfigureAwait(false);
                if (entry is { IsDirectory: false })
                    return new DriveTestResult(DriveTestOutcome.NotFound, $"'{folder}' in '{rootName}' is a file, not a folder.");
                if (entry is not null) break;
                if (!asTarget)
                    return new DriveTestResult(DriveTestOutcome.NotFound,
                        $"'{path}' was not found in '{rootName}'. Check the path, or share the folder with {email}.");
                missing = true;
                folder = StoragePath.Parent(folder);
            }

            await using var listing = storage.ListAsync(folder, false, ct).GetAsyncEnumerator(ct);
            await listing.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Failure(Map(ex, path, ct), Step.Path, connection, path);
        }

        if (!asTarget)
            return new DriveTestResult(DriveTestOutcome.Ok, $"'{rootName}' can be read.");

        var probe = StoragePath.Combine(folder, ProbePrefix + Guid.NewGuid().ToString("N"));
        var written = false;
        try
        {
            var writer = await storage.CreateAsync(probe, new CreateOptions(), ct).ConfigureAwait(false);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteAsync("ReBackup connection test"u8.ToArray(), ct).ConfigureAwait(false);
                await writer.CommitAsync(ct).ConfigureAwait(false);
            }
            written = true;
            await storage.DeleteAsync([probe], ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A write that failed or was cancelled may still have landed: remove it, whatever the token says.
            if (!written || ct.IsCancellationRequested) await TryDeleteAsync(storage, probe).ConfigureAwait(false);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            return Failure(Map(ex, probe, ct), written ? Step.Delete : Step.Write, connection, path);
        }

        return new DriveTestResult(DriveTestOutcome.Ok, missing
            ? $"'{rootName}' can be written to; '{path}' does not exist yet and will be created by the first backup."
            : $"'{rootName}' can be written to.");
    }

    private enum Step { Root, Path, Write, Delete }

    private static Exception Map(Exception ex, string path, CancellationToken ct)
    {
        var mapped = DriveErrors.Map(ex, path, ct);
        if (mapped is OperationCanceledException && ct.IsCancellationRequested) throw mapped;
        return mapped;
    }

    private static DriveTestResult Failure(Exception error, Step step, DriveConnection connection, string path)
    {
        var rootName = connection.Info.RootName;
        var email = connection.ClientEmail;
        var place = path.Length == 0 ? $"'{rootName}'" : $"'{path}' in '{rootName}'";
        return error switch
        {
            StorageAccessDeniedException => new DriveTestResult(DriveTestOutcome.AccessDenied, step switch
            {
                Step.Write => $"The service account {email} may not write to {place}. Give it the Manager role on the shared drive.",
                Step.Delete => $"The service account {email} can write to {place} but may not delete files. ReBackup deletes old versions permanently, " +
                               "which on a shared drive needs the Manager role.",
                _ => $"The service account {email} may not open {place}, or its key is not valid. Share the folder or shared drive with {email}.",
            }),
            StorageNotFoundException => new DriveTestResult(DriveTestOutcome.NotFound, step == Step.Root
                ? $"'{rootName}' was not found. It may have been deleted, or it is not shared with the service account: share it with {email}."
                : $"{place} was not found. Check the path, or share the folder with {email}."),
            _ => new DriveTestResult(DriveTestOutcome.Error, error.Message),
        };
    }

    /// <summary>Best-effort cleanup: a short timeout of its own, errors swallowed.</summary>
    private static async Task TryDeleteAsync(DriveStorage storage, string probe)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await storage.DeleteAsync([probe], timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Nothing more can be done; the leftover is a small file with a name that says what it is.
        }
    }
}
