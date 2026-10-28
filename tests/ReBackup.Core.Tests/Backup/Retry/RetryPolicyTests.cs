using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Backup.Retry;

namespace ReBackup.Core.Tests.Backup.Retry;

public class RetryPolicyTests
{
    [Theory, InlineData("core.file.locked", true), InlineData("core.skip.changed", true),
     InlineData("core.file.accessDenied", true), InlineData("core.file.cannotOpenFile", true),
     InlineData("core.file.noLongerExists", false), InlineData("core.skip.reservedName", false),
     InlineData("core.skip.ignoreFileUnreadable", false), InlineData("core.file.cannotOpenFolder", false),
     InlineData("core.file.archived", false), InlineData(null, false)]
    public void Only_skips_that_may_pass_later_are_retried(string? key, bool retried)
    {
        RetryPolicy.IsRetryable(new SkippedEntry("a.txt", "any reason", key)).Should().Be(retried);
    }
}
