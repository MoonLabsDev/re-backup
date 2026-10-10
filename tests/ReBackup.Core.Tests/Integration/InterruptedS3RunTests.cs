using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.S3.Tests.Integration;
using static ReBackup.Core.Tests.Integration.S3Integration;

namespace ReBackup.Core.Tests.Integration;

/// <summary>
/// A folder→S3 run (LocalStack) that stops halfway and cannot remove what it wrote (every deletion fails): its version stays with
/// its pending marker, and the next run removes it completely, leaving no marker, no object and no open multipart upload.
/// </summary>
[Collection(S3ServerCollection.Name)]
public sealed class InterruptedS3RunTests(S3ServerFixture server) : IDisposable
{
    private const string PlanId = "p-int";
    private const string Interrupted = "2026_10_07-22_00 Projects";
    private const string Next = "2026_10_07-22_01 Projects";

    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 22, 0, 0, TimeSpan.Zero));
    private readonly string _prefix = NewPrefix("int");
    private string _source = "";

    public void Dispose() => _tmp.Dispose();

    private BackupPlan Arrange(bool withBigFile)
    {
        Skip.IfNot(server.Available, server.SkipReason);
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _source = _tmp.CreateDir("source");
        for (var i = 1; i <= 12; i++)
            _tmp.WriteFile(Path.Combine("source", $"dir{i % 3}", $"f{i:00}.txt"), $"file {i}");
        if (withBigFile)
            File.WriteAllBytes(Path.Combine(_source, "big.bin"), RandomData(20 * MiB, seed: 4));
        return new BackupPlan { Id = PlanId, Name = "Projects", Source = StorageLocation.FileSystem(_source), Target = Location(_prefix) };
    }

    /// <summary>Runs <paramref name="plan"/> with the S3 target wrapped so that by <paramref name="wrapTarget"/> (which makes every deletion fail).</summary>
    private Task<RunLogEntry> RunWithoutDeletes(BackupPlan plan, Func<IStorage, FaultyStorage> wrapTarget, CancellationToken ct = default) =>
        new BackupRunner(Storages(server, (location, storage) => location == plan.Target ? wrapTarget(storage) : storage), _time)
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual), cancellationToken: ct);

    private async Task Leftover_is_there_and_the_next_run_removes_it(BackupPlan plan, RunLogEntry interrupted)
    {
        interrupted.Version.Should().BeNull();
        interrupted.Warnings.Should().ContainSingle(w => w.Contains(Interrupted), "the run could not remove its version");
        var left = await KeysAsync(server, _prefix);
        left.Should().Contain($"{Interrupted}/re-pending.json").And.OnlyContain(k => k.StartsWith(Interrupted + "/", StringComparison.Ordinal));
        left.Should().NotContain($"{Interrupted}/re-manifest.json");
        (await OpenUploadsAsync(server, _prefix)).Should().BeEmpty("an upload that was stopped is aborted");

        _time.Advance(TimeSpan.FromMinutes(1));
        var next = await new BackupRunner(Storages(server), _time).RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));

        next.Status.Should().Be(RunStatus.Completed, next.Reason);
        next.Version.Should().Be(Next);
        next.Warnings.Should().BeEmpty();
        var keys = await KeysAsync(server, _prefix);
        keys.Should().OnlyContain(k => k.StartsWith(Next + "/", StringComparison.Ordinal), "the half-written version is gone completely");
        keys.Should().NotContain(k => k.EndsWith("/re-pending.json", StringComparison.Ordinal) || k.EndsWith("/re-deleting.json", StringComparison.Ordinal));
        keys.Should().Contain($"{Next}/re-manifest.json");
        keys.Count(k => k.EndsWith(".txt", StringComparison.Ordinal)).Should().Be(12);
        (await OpenUploadsAsync(server, _prefix)).Should().BeEmpty();
        (await VersionCatalog.ListAsync(server.CreateStorage(_prefix), PlanId, plan.Name)).Select(v => v.Name).Should().Equal(Next);
    }

    [SkippableFact]
    public async Task Run_canceled_while_the_multipart_upload_completes_is_cleaned_up_by_the_next_run()
    {
        var plan = Arrange(withBigFile: true);
        using var cts = new CancellationTokenSource();

        var interrupted = await RunWithoutDeletes(plan, storage => new FaultyStorage(storage)
        {
            FailDelete = _ => true,
            Before = (operation, path) =>
            {
                if (operation == "commit" && path.EndsWith("/big.bin", StringComparison.Ordinal)) cts.Cancel();
            },
        }, cts.Token);

        interrupted.Status.Should().Be(RunStatus.Canceled, interrupted.Reason);
        (await KeysAsync(server, _prefix)).Should().NotContain($"{Interrupted}/big.bin");
        await Leftover_is_there_and_the_next_run_removes_it(plan, interrupted);
    }

    [SkippableFact]
    public async Task Run_stopped_by_a_storage_failure_is_cleaned_up_by_the_next_run()
    {
        var plan = Arrange(withBigFile: false);

        var interrupted = await RunWithoutDeletes(plan, storage => new FaultyStorage(storage)
        {
            FailDelete = _ => true,
            FailCommit = path => path.EndsWith("/f07.txt", StringComparison.Ordinal),
        });

        interrupted.Status.Should().Be(RunStatus.Error);
        await Leftover_is_there_and_the_next_run_removes_it(plan, interrupted);
    }
}
