using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Shared.Schedule;

namespace ReBackup.Core.Tests.Backup;

public class BackupRunnerIndexTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly BackupPlan _plan;

    public BackupRunnerIndexTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _tmp.WriteFile(@"source\a.txt", "alpha");
        _tmp.WriteFile(@"source\sub\b.bin", "bravo-bravo");
        _plan = new BackupPlan { Id = "p1", Name = "Projects", Source = _tmp.PathOf("source"), Target = _tmp.PathOf("target") };
    }

    public void Dispose() => _tmp.Dispose();

    private Task<RunLogEntry> Run(IVersionIndexSink sink) =>
        new BackupRunner(new PhysicalTargetVolume(), _time, indexSink: sink)
            .RunAsync(new BackupRequest(_plan, [], RunTrigger.Manual));

    [Fact]
    public async Task A_finished_run_adds_its_version_to_the_index()
    {
        var indexes = new VersionIndexSet(_tmp.PathOf("indexes"));

        var entry = await Run(indexes);

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Warnings.Should().BeEmpty();
        var version = indexes.For("p1").Versions().Should().ContainSingle().Subject;
        version.Name.Should().Be("2026_09_30-16_05 Projects");
        version.LocalTime.Should().Be(new DateTime(2026, 9, 30, 16, 5, 0));
        version.Ownership.Should().Be(VersionOwnership.Owned);
        version.Source.Should().Be(_plan.Source);
        version.FileCount.Should().Be(2);
        version.TotalBytes.Should().Be(16);
        indexes.For("p1").Sync(VersionCatalog.List(_plan.Target, "p1", "Projects")).Unchanged
            .Should().Be(1, "the index knows the manifest on disk, so the next sync does not read it again");
    }

    [Fact]
    public async Task An_index_failure_is_a_warning_and_the_backup_stays_completed()
    {
        var entry = await Run(new FailingSink());

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Version.Should().Be("2026_09_30-16_05 Projects");
        entry.Warnings.Should().ContainSingle().Which.Should().Be("The version index could not be updated: disk on fire");
    }

    [Fact]
    public async Task A_run_that_does_not_finish_adds_nothing()
    {
        var sink = new RecordingSink();
        _plan.Source = _tmp.PathOf("missing");

        var entry = await Run(sink);

        entry.Status.Should().Be(RunStatus.Error);
        sink.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_run_finishes_while_the_plans_index_is_busy_and_its_version_is_added_afterwards()
    {
        var worker = new VersionIndexWorker(new VersionIndexSet(_tmp.PathOf("indexes")));
        using var release = new ManualResetEventSlim();
        var busy = worker.RunAsync("p1", _ => release.Wait());   // e.g. a long sync over the network

        // A sink that blocked the run would fail here instead of hanging the suite.
        var entry = await Run(worker).WaitAsync(TimeSpan.FromSeconds(10));

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Warnings.Should().BeEmpty();
        busy.IsCompleted.Should().BeFalse("the run did not wait for the index");
        release.Set();
        await worker.WhenIdle("p1").WaitAsync(TimeSpan.FromSeconds(10));
        worker.Indexes.For("p1").Versions().Should().ContainSingle().Which.Name.Should().Be("2026_09_30-16_05 Projects");
    }

    private sealed class FailingSink : IVersionIndexSink
    {
        public void Add(string planId, VersionInfo version, BackupManifest manifest) =>
            throw new InvalidOperationException("disk on fire");
    }

    private sealed class RecordingSink : IVersionIndexSink
    {
        public int Calls { get; private set; }
        public void Add(string planId, VersionInfo version, BackupManifest manifest) => Calls++;
    }
}
