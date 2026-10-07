namespace ReBackup.Core.Versions;

/// <summary>What the Versions and retention views find at a plan's target.</summary>
public enum TargetState
{
    /// <summary>The target folder exists and can be listed.</summary>
    Present,

    /// <summary>The target does not exist, but its drive or share does (a plan that has not run yet).</summary>
    NotCreatedYet,

    /// <summary>The target cannot be reached: the drive or share is missing (e.g. an offline NAS), or it fails in another way.</summary>
    Unreachable,
}
