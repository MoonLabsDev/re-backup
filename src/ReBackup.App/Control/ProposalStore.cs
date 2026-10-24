using System.Diagnostics.CodeAnalysis;
using ReBackup.Control;

namespace ReBackup.App.Control;

/// <summary>
/// The pending write proposals of the control service, in memory: a proposal tool adds one and hands its id to Claude,
/// <c>apply_change</c> takes it. A proposal can be taken once, within <see cref="Lifetime"/>.
/// </summary>
public sealed class ProposalStore(TimeProvider? time = null)
{
    /// <summary>How long a proposal can be applied after it was made.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Expired proposals are still told apart from unknown ids for this long, then forgotten.</summary>
    private static readonly TimeSpan Remembered = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (object Proposal, DateTimeOffset Made)> _proposals = new(StringComparer.Ordinal);

    /// <summary>Keeps <paramref name="proposal"/> and returns its new id.</summary>
    public string Add(object proposal)
    {
        var now = _time.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            foreach (var old in _proposals.Where(p => now - p.Value.Made > Remembered).Select(p => p.Key).ToList())
                _proposals.Remove(old);
            _proposals[id] = (proposal, now);
        }
        return id;
    }

    /// <summary>
    /// Takes the proposal (it cannot be taken again). False with <paramref name="errorKey"/>
    /// <c>proposal_unknown</c> (never made, already taken or long forgotten) or <c>proposal_expired</c> (older than
    /// <see cref="Lifetime"/>; it is dropped).
    /// </summary>
    public bool TryTake(string id, [MaybeNullWhen(false)] out object proposal, out string? errorKey)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_proposals.Remove(id, out var entry))
            {
                (proposal, errorKey) = (null, ControlErrors.ProposalUnknown);
                return false;
            }
            if (now - entry.Made > Lifetime)
            {
                (proposal, errorKey) = (null, ControlErrors.ProposalExpired);
                return false;
            }
            (proposal, errorKey) = (entry.Proposal, null);
            return true;
        }
    }
}
