using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

/// <summary>
/// A source may list a file with a size it only learns when the file is read (a Google document is listed with size 0 and
/// exported on read). Bytes that differ from the listed size alone are no change; a different stamp or time is.
/// </summary>
public class BackupRunnerUnknownSizeTests
{
    private const string Version = "2026_09_30-16_05 Projects";
    private static readonly DateTime Mtime = new(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly InMemoryStorage _sourceFiles = new(StorageCapabilities.None);
    private readonly InMemoryStorage _targetFiles = new(StorageCapabilities.None);

    public BackupRunnerUnknownSizeTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _sourceFiles.AddFile("doc.docx", new byte[1234], Mtime);
        _sourceFiles.AddFile("plain.txt", "hello"u8.ToArray(), Mtime);
    }

    private Task<RunLogEntry> Run(IStorage source, string sourceKind)
    {
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = sourceKind == StorageLocation.FileSystemKind
                ? StorageLocation.FileSystem(Path.GetTempPath())
                : new StorageLocation(sourceKind, "", "conn-1"),
            Target = new StorageLocation("mem", "", "conn-2"),
        };
        var storages = new TestStorageFactory(location => location == plan.Source ? source : _targetFiles);
        return new BackupRunner(storages, _time, host: "TESTHOST")
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual), null, CancellationToken.None);
    }

    [Theory]
    [InlineData("fs")]
    [InlineData("gdrive")]
    public async Task A_file_listed_with_size_0_but_read_whole_is_no_change(string sourceKind)
    {
        var source = new ExportLikeStorage(_sourceFiles, "doc.docx");

        var entry = await Run(source, sourceKind);

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        entry.Skipped.Should().BeEmpty();
        entry.FilesCopied.Should().Be(2);
        entry.BytesCopied.Should().Be(1234 + 5);
        _targetFiles.ReadAllBytes($"{Version}/doc.docx").Should().HaveCount(1234);
    }

    [Theory]
    [InlineData("fs")]
    [InlineData("gdrive")]
    public async Task A_file_listed_with_size_0_whose_stamp_changed_during_the_copy_is_reported(string sourceKind)
    {
        var source = new ExportLikeStorage(_sourceFiles, "doc.docx") { StampAfterRead = "changed" };

        var entry = await Run(source, sourceKind);

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().ContainSingle().Which.Should().Be(
            new SkippedEntry("doc.docx", "changed while it was copied; the copy may be inconsistent"));
        entry.FilesCopied.Should().Be(2);
    }

    [Fact]
    public async Task A_file_listed_with_size_0_whose_time_changed_during_the_copy_is_reported()
    {
        var source = new ExportLikeStorage(_sourceFiles, "doc.docx") { TimeAfterRead = Mtime.AddMinutes(1) };

        var entry = await Run(source, "gdrive");

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().ContainSingle().Which.Path.Should().Be("doc.docx");
    }

    [Theory]
    [InlineData("fs")]
    [InlineData("gdrive")]
    public async Task A_file_listed_with_a_known_size_that_reads_shorter_is_reported_whatever_its_stamp(string sourceKind)
    {
        // Listed and stat'ed with 1235 bytes, the same stamp and time before and after, but 1234 bytes read: changed.
        var source = new ExportLikeStorage(_sourceFiles, "doc.docx") { ListedSize = 1235 };

        var entry = await Run(source, sourceKind);

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().ContainSingle().Which.Should().Be(
            new SkippedEntry("doc.docx", "changed while it was copied; the copy may be inconsistent"));
    }

    [Fact]
    public async Task A_file_listed_with_size_0_that_is_stat_with_a_size_afterwards_is_reported()
    {
        var source = new ExportLikeStorage(_sourceFiles, "doc.docx") { SizeAfterRead = 1234 };

        var entry = await Run(source, "gdrive");

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().ContainSingle().Which.Path.Should().Be("doc.docx");
    }

    [Fact]
    public async Task A_file_whose_size_differs_without_stamps_is_reported()
    {
        var source = new ExportLikeStorage(_sourceFiles, "doc.docx") { NoStamps = true };

        var entry = await Run(source, "gdrive");

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().ContainSingle().Which.Path.Should().Be("doc.docx");
    }

    /// <summary>
    /// Lists and stats <paramref name="path"/> with size 0 (or <see cref="ListedSize"/>) and a fixed stamp, as Drive does for a Google document, while reading
    /// gives the whole content. After it was read, a stat may show another stamp or time.
    /// </summary>
    private sealed class ExportLikeStorage(InMemoryStorage inner, string path) : IStorage
    {
        private bool _read;

        public string? StampAfterRead { get; init; }

        public DateTime? TimeAfterRead { get; init; }

        public bool NoStamps { get; init; }

        /// <summary>The size listed and stat'ed instead of 0.</summary>
        public long ListedSize { get; init; }

        /// <summary>The size a stat shows after the file was read.</summary>
        public long? SizeAfterRead { get; init; }

        public StorageCapabilities Capabilities => inner.Capabilities;

        private StorageEntry Shown(StorageEntry entry)
        {
            if (entry.Path != path)
                return NoStamps ? entry with { Stamp = null } : entry;
            var stamp = NoStamps ? null : _read && StampAfterRead is not null ? StampAfterRead : "v7";
            var time = _read && TimeAfterRead is { } t ? t : entry.ModifiedUtc;
            var size = _read && SizeAfterRead is { } after ? after : ListedSize;
            return entry with { Size = size, Stamp = stamp, ModifiedUtc = time };
        }

        public async Task<StorageEntry?> StatAsync(string p, CancellationToken ct) =>
            await inner.StatAsync(p, ct) is { } entry ? Shown(entry) : null;

        public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var entry in inner.ListAsync(folder, recursive, ct))
                yield return Shown(entry);
        }

        public async Task<Stream> OpenReadAsync(string p, CancellationToken ct)
        {
            var stream = await inner.OpenReadAsync(p, ct);
            if (p == path)
                _read = true;
            return stream;
        }

        public Task<StorageWriter> CreateAsync(string p, CreateOptions options, CancellationToken ct) => inner.CreateAsync(p, options, ct);

        public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) => inner.DeleteAsync(paths, ct);

        public Task EnsureDirectoryAsync(string p, CancellationToken ct) => inner.EnsureDirectoryAsync(p, ct);

        public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);
    }
}
