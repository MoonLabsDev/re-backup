using ReBackup.Storage;

namespace ReBackup.Core.Versions;

/// <summary>Tells "not created yet" from "unreachable" for a plan's target through the storage's own exceptions.</summary>
public static class TargetStates
{
    /// <summary>
    /// Lists the root of <paramref name="target"/>: success is <see cref="TargetState.Present"/>,
    /// <see cref="StorageNotFoundException"/> is <see cref="TargetState.NotCreatedYet"/>, <see cref="StorageUnavailableException"/>
    /// is <see cref="TargetState.Unreachable"/>. Any other <see cref="StorageException"/> (e.g. access denied) is
    /// <see cref="TargetState.Unreachable"/> too: nothing may be deleted or indexed on a target that cannot be read.
    /// Never throws for these cases; only cancellation surfaces. An unset location is for the caller to handle first.
    /// </summary>
    public static async Task<TargetState> ProbeAsync(IStorage target, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in target.ListAsync("", recursive: false, ct).ConfigureAwait(false))
                break;
            return TargetState.Present;
        }
        catch (StorageNotFoundException)
        {
            return TargetState.NotCreatedYet;
        }
        catch (StorageException)
        {
            return TargetState.Unreachable;
        }
    }

    /// <summary>
    /// Opens <paramref name="location"/> and probes it. An unset path, a path that is no valid root (e.g. relative) and a
    /// kind this build does not know are <see cref="TargetState.Unreachable"/> with no storage; otherwise the storage is
    /// returned for listing (null unless <see cref="TargetState.Present"/>).
    /// </summary>
    public static async Task<(TargetState State, IStorage? Storage)> OpenAndProbeAsync(IStorageFactory storages,
        StorageLocation location, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(location.Path))
            return (TargetState.Unreachable, null);
        IStorage storage;
        try
        {
            storage = storages.Open(location);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return (TargetState.Unreachable, null);
        }
        var state = await ProbeAsync(storage, ct).ConfigureAwait(false);
        return (state, state == TargetState.Present ? storage : null);
    }
}
