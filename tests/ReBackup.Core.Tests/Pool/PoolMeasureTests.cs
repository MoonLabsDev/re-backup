using System.Text;
using FluentAssertions;
using ReBackup.Core.Pool;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Pool;

/// <summary>Measuring what an incremental plan's pool takes on the target (<see cref="PoolUsage.MeasurePoolAsync"/>).</summary>
public class PoolMeasureTests
{
    private readonly InMemoryStorage _target = new();

    private void Blob(string content)
    {
        var hash = PoolLayout.Format(System.IO.Hashing.XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(content)));
        _target.AddFile(PoolLayout.BlobPath(hash), Encoding.UTF8.GetBytes(content));
    }

    [Fact]
    public async Task Missing_pool_measures_zero()
    {
        (await PoolUsage.MeasurePoolAsync(_target, null, CancellationToken.None)).Should().Be((0, 0L));
    }

    [Fact]
    public async Task Blobs_are_counted_with_their_sizes_and_other_files_ignored()
    {
        Blob("alpha");
        Blob("twelve bytes");
        _target.AddFile($"{PoolLayout.PoolFolder}/ab/cd/notes.txt", Encoding.UTF8.GetBytes("not a blob"));
        _target.AddFile($"{PoolLayout.PoolFolder}/ab/cd/0123456789abcdef0123456789abcde.partial", Encoding.UTF8.GetBytes("x"));
        _target.AddFile("elsewhere/0123456789abcdef0123456789abcdef", Encoding.UTF8.GetBytes("outside the pool"));

        (await PoolUsage.MeasurePoolAsync(_target, null, CancellationToken.None)).Should().Be((2, 5L + 12));
    }

    [Fact]
    public async Task Progress_reports_the_files_counted()
    {
        for (var i = 0; i < 2500; i++)
            Blob("blob " + i);
        var reports = new List<int>();

        await PoolUsage.MeasurePoolAsync(_target, new SyncProgress(reports.Add), CancellationToken.None);

        reports.Should().Equal(1000, 2000, 2500);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        Blob("alpha");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> measure = () => PoolUsage.MeasurePoolAsync(_target, null, cts.Token);

        await measure.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    [Fact]
    public async Task Folders_are_measured_by_their_files_and_a_missing_one_counts_nothing()
    {
        for (var i = 0; i < 20; i++)
            _target.AddFile($"v{i}/sub/f{i}.bin", new byte[i + 1]);
        _target.AddFile("elsewhere/x.bin", new byte[100]);
        var folders = Enumerable.Range(0, 20).Select(i => $"v{i}").Append("gone").ToList();
        var reports = new List<int>();

        var measured = await PoolUsage.MeasureFoldersAsync(_target, folders, new SyncProgress(reports.Add), CancellationToken.None);

        measured.Should().Be((20, Enumerable.Range(1, 20).Sum(n => (long)n)));
        reports.Should().Equal(20);
    }
}
