using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Config;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Tests.Plans;

public class RetrySkippedSettingsTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void A_plan_without_a_retry_section_retries_three_times_every_five_minutes()
    {
        var plan = JsonSerializer.Deserialize<BackupPlan>("""{"name":"P"}""", JsonDefaults.Options)!;

        plan.RetrySkipped.Enabled.Should().BeTrue();
        plan.RetrySkipped.Attempts.Should().Be(3);
        plan.RetrySkipped.IntervalMinutes.Should().Be(5);
    }

    [Fact]
    public void A_null_retry_section_falls_back_to_the_defaults()
    {
        var plan = JsonSerializer.Deserialize<BackupPlan>("""{"name":"P","retrySkipped":null}""", JsonDefaults.Options)!;

        plan.RetrySkipped.Should().NotBeNull();
        plan.RetrySkipped.Attempts.Should().Be(3);
    }

    [Fact]
    public void Retry_settings_survive_a_save_and_load()
    {
        var plan = new BackupPlan { Name = "P" };
        plan.RetrySkipped.Enabled = false;
        plan.RetrySkipped.Attempts = 7;
        plan.RetrySkipped.IntervalMinutes = 45;

        var copy = plan.Clone();

        copy.RetrySkipped.Enabled.Should().BeFalse();
        copy.RetrySkipped.Attempts.Should().Be(7);
        copy.RetrySkipped.IntervalMinutes.Should().Be(45);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(21, 5)]
    [InlineData(3, 0)]
    [InlineData(3, 721)]
    public void Retry_settings_outside_their_range_are_invalid(int attempts, int minutes)
    {
        var plan = ValidPlan();
        plan.RetrySkipped.Attempts = attempts;
        plan.RetrySkipped.IntervalMinutes = minutes;

        PlanValidator.Validate(plan, [plan]).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(0, 5, "core.plan.retryAttempts")]
    [InlineData(3, 721, "core.plan.retryInterval")]
    public void An_invalid_retry_setting_is_reported_under_its_own_key(int attempts, int minutes, string key)
    {
        var plan = ValidPlan();
        plan.RetrySkipped.Attempts = attempts;
        plan.RetrySkipped.IntervalMinutes = minutes;

        PlanValidator.Validate(plan, [plan]).Select(e => e.Key).Should().ContainSingle().Which.Should().Be(key);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 720)]
    public void Retry_settings_at_the_edges_of_their_range_are_valid(int attempts, int minutes)
    {
        var plan = ValidPlan();
        plan.RetrySkipped.Attempts = attempts;
        plan.RetrySkipped.IntervalMinutes = minutes;

        PlanValidator.Validate(plan, [plan]).Should().BeEmpty();
    }

    [Fact]
    public void Retry_files_live_in_the_retries_folder()
    {
        var paths = new ConfigPaths(@"C:\r");

        paths.RetriesDirectory.Should().Be(@"C:\r\retries");
        paths.RetryFileFor("abc").Should().Be(@"C:\r\retries\abc.json");
    }

    private BackupPlan ValidPlan() => new()
    {
        Name = "Projects",
        Source = StorageLocation.FileSystem(_tmp.CreateDir("src")),
        Target = StorageLocation.FileSystem(_tmp.PathOf("dst")),
    };
}
