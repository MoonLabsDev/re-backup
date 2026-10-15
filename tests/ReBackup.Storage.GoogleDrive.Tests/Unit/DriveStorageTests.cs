using System.Text;
using FluentAssertions;
using ReBackup.Storage.GoogleDrive.Tests.Fakes;

namespace ReBackup.Storage.GoogleDrive.Tests.Unit;

public sealed class DriveStorageTests
{
    private const string Root = InMemoryDriveApi.RootId;
    private static readonly DateTime Time = new(2025, 5, 4, 3, 2, 1, DateTimeKind.Utc);

    private readonly InMemoryDriveApi _api = new();
    private readonly ManualTimeProvider _time = new();

    private DriveStorage Storage(string rootId = Root) => new(_api, rootId, _time);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<string> ReadAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, default);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<List<StorageEntry>> ListAsync(IStorage storage, string folder, bool recursive = false)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync(folder, recursive, default)) entries.Add(entry);
        return entries;
    }

    private static async Task WriteAsync(IStorage storage, string path, string text, CreateOptions? options = null)
    {
        await using var writer = await storage.CreateAsync(path, options ?? new CreateOptions(), default);
        await writer.WriteAsync(Bytes(text));
        await writer.CommitAsync(default);
    }

    /// <summary>Moves the clock until the task is done, so retry delays pass.</summary>
    private async Task<T> DriveAsync<T>(Task<T> task)
    {
        for (var i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(1);
        }
        return await task;
    }

    // ---- paths and the cache ----

    [Fact]
    public async Task Resolves_nested_paths()
    {
        var a = _api.AddFolder(Root, "a");
        var b = _api.AddFolder(a, "b");
        _api.AddFile(b, "c.txt", Bytes("hello"), Time);
        var storage = Storage();

        var entry = await storage.StatAsync("a/b/c.txt", default);

        entry.Should().Be(new StorageEntry("a/b/c.txt", false, 5, Time, false, _api.Children(b)[0].Md5));
        (await ReadAsync(storage, "a/b/c.txt")).Should().Be("hello");
        (await storage.StatAsync("a/b", default))!.IsDirectory.Should().BeTrue();
        (await ListAsync(storage, "", recursive: true)).Select(e => (e.Path, e.IsDirectory)).Should().BeEquivalentTo(
            [("a", true), ("a/b", true), ("a/b/c.txt", false)]);
        (await ListAsync(storage, "a")).Select(e => e.Path).Should().Equal("a/b");
    }

    [Fact]
    public async Task Resolved_folders_are_cached()
    {
        var a = _api.AddFolder(Root, "a");
        var b = _api.AddFolder(a, "b");
        _api.AddFile(b, "c.txt", Bytes("x"));
        var storage = Storage();
        await storage.StatAsync("a/b/c.txt", default);
        var before = _api.Calls("list");

        await storage.StatAsync("a/b/c.txt", default);

        (_api.Calls("list") - before).Should().Be(1, "only the file is looked up in the cached folder");
    }

    [Fact]
    public async Task A_cached_folder_that_is_gone_is_resolved_again()
    {
        var a = _api.AddFolder(Root, "a");
        var b = _api.AddFolder(a, "b");
        _api.AddFile(b, "f.txt", Bytes("old"));
        var storage = Storage();
        (await ReadAsync(storage, "a/b/f.txt")).Should().Be("old");

        // Someone replaces the folder: same path, new ID.
        _api.Remove(b);
        var newB = _api.AddFolder(a, "b");
        _api.AddFile(newB, "f.txt", Bytes("new"));

        (await ReadAsync(storage, "a/b/f.txt")).Should().Be("new");
        (await storage.StatAsync("a/b/f.txt", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Missing_paths()
    {
        var file = _api.AddFile(Root, "f.txt", Bytes("x"));
        var storage = Storage();

        (await storage.StatAsync("nope", default)).Should().BeNull();
        (await storage.StatAsync("nope/deeper", default)).Should().BeNull();
        (await storage.StatAsync("f.txt/below", default)).Should().BeNull();
        await storage.Invoking(s => s.OpenReadAsync("nope", default)).Should().ThrowAsync<StorageNotFoundException>();
        await storage.Invoking(s => ListAsync(s, "nope")).Should().ThrowAsync<StorageNotFoundException>();
        await storage.Invoking(s => ListAsync(s, "f.txt")).Should().ThrowAsync<StorageNotFoundException>();
        _api.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task The_root()
    {
        var storage = Storage();

        (await storage.StatAsync("", default)).Should().Match<StorageEntry>(e => e.IsDirectory && e.Path == "");
        (await ListAsync(storage, "")).Should().BeEmpty();
        storage.Capabilities.Should().Be(StorageCapabilities.SetModifiedTime | StorageCapabilities.EmptyDirectories);
        (await storage.GetFreeSpaceAsync(default)).Should().BeNull();
        await storage.Invoking(s => s.OpenReadAsync("", default)).Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task A_missing_root()
    {
        var storage = Storage("gone");

        (await storage.StatAsync("", default)).Should().BeNull();
        await storage.Invoking(s => ListAsync(s, "")).Should().ThrowAsync<StorageNotFoundException>();
        await storage.Invoking(s => s.StatAsync("a", default)).Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Invalid_paths_throw_argument_exceptions()
    {
        var storage = Storage();

        await storage.Invoking(s => s.StatAsync("/a", default)).Should().ThrowAsync<ArgumentException>();
        await storage.Invoking(s => s.OpenReadAsync("a//b", default)).Should().ThrowAsync<ArgumentException>();
        await storage.Invoking(s => s.CreateAsync("a/..", new CreateOptions(), default)).Should().ThrowAsync<ArgumentException>();
        await storage.Invoking(s => s.CreateAsync("", new CreateOptions(), default)).Should().ThrowAsync<ArgumentException>();
        await storage.Invoking(s => s.DeleteAsync(["a\\b"], default)).Should().ThrowAsync<ArgumentException>();
        await storage.Invoking(s => s.EnsureDirectoryAsync("a/", default)).Should().ThrowAsync<ArgumentException>();
        var enumerator = storage.ListAsync("x/", false, default).GetAsyncEnumerator();
        await enumerator.Invoking(e => e.MoveNextAsync().AsTask()).Should().ThrowAsync<ArgumentException>();
    }

    // ---- names ----

    [Fact]
    public async Task Same_name_siblings_are_ambiguous()
    {
        _api.AddFile(Root, "dup.txt", Bytes("one"));
        _api.AddFile(Root, "dup.txt", Bytes("two"));
        var d1 = _api.AddFolder(Root, "d");
        _api.AddFolder(Root, "d");
        _api.AddFile(d1, "f.txt", Bytes("x"));
        var storage = Storage();

        (await ListAsync(storage, "")).Select(e => e.Path).Should().BeEquivalentTo(["dup.txt", "d"]);
        (await ListAsync(storage, "", recursive: true)).Select(e => e.Path).Should().BeEquivalentTo(["dup.txt", "d", "d/f.txt"]);

        (await storage.Invoking(s => s.StatAsync("dup.txt", default)).Should().ThrowAsync<StorageIOException>())
            .Which.Message.Should().Contain("ambiguous name: dup.txt");
        (await storage.Invoking(s => s.OpenReadAsync("dup.txt", default)).Should().ThrowAsync<StorageIOException>())
            .Which.Message.Should().Contain("ambiguous name: dup.txt");
        (await storage.Invoking(s => s.CreateAsync("dup.txt", new CreateOptions(Overwrite: true), default)).Should().ThrowAsync<StorageIOException>())
            .Which.Message.Should().Contain("ambiguous name: dup.txt");
        (await storage.Invoking(s => s.StatAsync("d/f.txt", default)).Should().ThrowAsync<StorageIOException>())
            .Which.Message.Should().Contain("ambiguous name: d");
        (await storage.Invoking(s => WriteAsync(s, "d/new.txt", "x", new CreateOptions(Overwrite: true))).Should().ThrowAsync<StorageIOException>())
            .Which.Message.Should().Contain("ambiguous name: d");
        (await storage.Invoking(s => s.EnsureDirectoryAsync("d/sub", default)).Should().ThrowAsync<StorageIOException>())
            .Which.Message.Should().Contain("ambiguous name: d");
        _api.Children(Root).Should().HaveCount(4, "nothing was written");
    }

    [Fact]
    public async Task Names_that_are_no_storage_path_segment_are_left_out()
    {
        _api.AddFile(Root, "a/b", Bytes("slash"));
        _api.AddFile(Root, "..", Bytes("dots"));
        _api.AddFile(Root, ".", Bytes("dot"));
        _api.AddFile(Root, "back\\slash", Bytes("back"));
        _api.AddFile(Root, "", Bytes("empty"));
        _api.AddFile(Root, "ok.txt", Bytes("ok"));
        var storage = Storage();

        (await ListAsync(storage, "", recursive: true)).Select(e => e.Path).Should().Equal("ok.txt");
        (await storage.StatAsync("a/b", default)).Should().BeNull();
    }

    // ---- Google files ----

    [Fact]
    public async Task Google_files_are_listed_with_their_export_name_and_read_as_exports()
    {
        var doc = _api.AddGoogleFile(Root, "Report", DriveMimeTypes.Document, Bytes("docx bytes"));
        _api.AddGoogleFile(Root, "Budget", DriveMimeTypes.Spreadsheet, Bytes("xlsx"));
        _api.AddGoogleFile(Root, "Deck", DriveMimeTypes.Presentation, Bytes("pptx"));
        _api.AddGoogleFile(Root, "Sketch", DriveMimeTypes.Drawing, Bytes("pdf"));
        _api.AddGoogleFile(Root, "Link", "application/vnd.google-apps.shortcut");
        _api.AddGoogleFile(Root, "Survey", "application/vnd.google-apps.form");
        _api.AddGoogleFile(Root, "Site", "application/vnd.google-apps.site");
        var storage = Storage();

        var entries = await ListAsync(storage, "");

        entries.Select(e => e.Path).Should().BeEquivalentTo(["Report.docx", "Budget.xlsx", "Deck.pptx", "Sketch.pdf"]);
        var report = entries.Single(e => e.Path == "Report.docx");
        report.Should().Be(new StorageEntry("Report.docx", false, 0, _api.Item(doc).ModifiedUtc, false,
            _api.Item(doc).Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        (await storage.StatAsync("Report.docx", default)).Should().Be(report);
        (await storage.StatAsync("Report", default)).Should().BeNull();
        (await storage.StatAsync("Link", default)).Should().BeNull();
        (await ReadAsync(storage, "Report.docx")).Should().Be("docx bytes");
        (await ReadAsync(storage, "Sketch.pdf")).Should().Be("pdf");
    }

    [Fact]
    public async Task An_export_over_the_limit_is_not_readable()
    {
        _api.AddGoogleFile(Root, "Huge", DriveMimeTypes.Document, new byte[InMemoryDriveApi.ExportLimit + 1]);
        var storage = Storage();

        var thrown = await storage.Invoking(s => s.OpenReadAsync("Huge.docx", default)).Should().ThrowAsync<StorageIOException>();

        thrown.Which.NotReadable.Should().BeTrue();
        thrown.Which.Archived.Should().BeFalse();
        thrown.Which.Path.Should().Be("Huge.docx");
        thrown.Which.Message.Should().Contain("Huge.docx").And.Contain("10 MB");
    }

    [Fact]
    public async Task A_real_file_with_the_export_name_hides_the_Google_file()
    {
        _api.AddGoogleFile(Root, "Report", DriveMimeTypes.Document, Bytes("export"));
        _api.AddFile(Root, "Report.docx", Bytes("real file"));
        var storage = Storage();

        var entries = await ListAsync(storage, "");

        entries.Should().ContainSingle().Which.Size.Should().Be(9);
        (await storage.StatAsync("Report.docx", default))!.Size.Should().Be(9);
        (await ReadAsync(storage, "Report.docx")).Should().Be("real file");
    }

    // ---- writes ----

    [Fact]
    public async Task A_new_file_is_visible_after_the_commit_with_its_modified_time()
    {
        var storage = Storage();

        await using (var writer = await storage.CreateAsync("x/y/new.txt", new CreateOptions(ModifiedUtc: Time), default))
        {
            await writer.WriteAsync(Bytes("content"));
            (await storage.StatAsync("x/y/new.txt", default)).Should().BeNull();
            await writer.CommitAsync(default);
            await writer.Invoking(w => w.CommitAsync(default)).Should().ThrowAsync<InvalidOperationException>();
        }

        var entry = await storage.StatAsync("x/y/new.txt", default);
        entry!.ModifiedUtc.Should().Be(Time);
        entry.Size.Should().Be(7);
        entry.Stamp.Should().NotBeNullOrEmpty();
        (await ReadAsync(storage, "x/y/new.txt")).Should().Be("content");
    }

    [Fact]
    public async Task A_writer_disposed_without_commit_leaves_nothing()
    {
        var storage = Storage();

        await using (var writer = await storage.CreateAsync("a.txt", new CreateOptions(), default))
            await writer.WriteAsync(Bytes("content"));

        _api.Children(Root).Should().BeEmpty();
        _api.Calls("upload").Should().Be(0);
    }

    [Fact]
    public async Task Exclusive_create_of_an_existing_path_conflicts()
    {
        _api.AddFile(Root, "a.txt", Bytes("theirs"));
        _api.AddGoogleFile(Root, "Doc", DriveMimeTypes.Document);
        _api.AddFolder(Root, "dir");
        var storage = Storage();

        await storage.Invoking(s => s.CreateAsync("a.txt", new CreateOptions(), default)).Should().ThrowAsync<StorageConflictException>();
        await storage.Invoking(s => s.CreateAsync("Doc.docx", new CreateOptions(), default)).Should().ThrowAsync<StorageConflictException>();
        await storage.Invoking(s => s.CreateAsync("dir", new CreateOptions(Overwrite: true), default)).Should().ThrowAsync<StorageConflictException>();
    }

    [Fact]
    public async Task Exclusive_create_conflicts_when_file_exists()
    {
        var storage = Storage();
        await WriteAsync(storage, "re-lock.json", "first", new CreateOptions(Durable: true));

        await storage.Invoking(s => WriteAsync(s, "re-lock.json", "second", new CreateOptions(Overwrite: false, Durable: true)))
            .Should().ThrowAsync<StorageConflictException>();

        (await ReadAsync(storage, "re-lock.json")).Should().Be("first");
    }

    [Fact]
    public async Task Exclusive_commit_conflicts_when_the_path_was_taken_meanwhile()
    {
        var storage = Storage();
        await using var writer = await storage.CreateAsync("a.txt", new CreateOptions(), default);
        await writer.WriteAsync(Bytes("ours"));
        _api.AddFile(Root, "a.txt", Bytes("theirs"));

        await writer.Invoking(w => w.CommitAsync(default)).Should().ThrowAsync<StorageConflictException>();

        _api.Children(Root).Should().ContainSingle().Which.Size.Should().Be(6);
    }

    [Fact]
    public async Task Exclusive_create_race_deletes_our_file()
    {
        var storage = Storage();
        string? theirs = null;
        var uploaded = false;
        _api.OnCall = (operation, _, _) =>
        {
            if (operation == "upload") uploaded = true;
            else if (operation == "list" && uploaded && theirs is null) theirs = _api.AddFile(Root, "a.txt", Bytes("theirs"));
            return Task.CompletedTask;
        };

        await storage.Invoking(s => WriteAsync(s, "a.txt", "ours")).Should().ThrowAsync<StorageConflictException>();

        _api.Children(Root).Select(c => c.Id).Should().Equal(theirs);
        _api.Calls("delete").Should().Be(1);
    }

    [Fact]
    public async Task Overwrite_keeps_the_id()
    {
        var id = _api.AddFile(Root, "a.txt", Bytes("old"));
        var storage = Storage();

        await WriteAsync(storage, "a.txt", "new content", new CreateOptions(Overwrite: true, ModifiedUtc: Time));

        _api.Children(Root).Should().ContainSingle().Which.Id.Should().Be(id);
        Encoding.UTF8.GetString(_api.Content(id)).Should().Be("new content");
        _api.Item(id).ModifiedUtc.Should().Be(Time);
        _api.Calls("upload").Should().Be(0);
    }

    [Fact]
    public async Task Overwrite_of_a_missing_file_creates_it()
    {
        var storage = Storage();

        await WriteAsync(storage, "d/a.txt", "x", new CreateOptions(Overwrite: true));

        (await ReadAsync(storage, "d/a.txt")).Should().Be("x");
    }

    [Fact]
    public async Task Expected_stamp()
    {
        var id = _api.AddFile(Root, "a.txt", Bytes("v1"));
        var storage = Storage();
        var stamp = (await storage.StatAsync("a.txt", default))!.Stamp;

        await storage.Invoking(s => s.CreateAsync("a.txt", new CreateOptions(ExpectedStamp: stamp), default))
            .Should().ThrowAsync<ArgumentException>();

        // Someone changes the file between our read and our commit.
        await using (var writer = await storage.CreateAsync("a.txt", new CreateOptions(Overwrite: true, ExpectedStamp: stamp), default))
        {
            await writer.WriteAsync(Bytes("ours"));
            await _api.UpdateContentAsync(id, new MemoryStream(Bytes("theirs")), null, default);
            await writer.Invoking(w => w.CommitAsync(default)).Should().ThrowAsync<StorageConflictException>();
        }
        Encoding.UTF8.GetString(_api.Content(id)).Should().Be("theirs");

        var current = (await storage.StatAsync("a.txt", default))!.Stamp;
        await WriteAsync(storage, "a.txt", "ours", new CreateOptions(Overwrite: true, ExpectedStamp: current));
        Encoding.UTF8.GetString(_api.Content(id)).Should().Be("ours");
        (await storage.StatAsync("a.txt", default))!.Stamp.Should().NotBe(current);

        await storage.Invoking(s => WriteAsync(s, "missing.txt", "x", new CreateOptions(Overwrite: true, ExpectedStamp: current)))
            .Should().ThrowAsync<StorageConflictException>();
        (await storage.StatAsync("missing.txt", default)).Should().BeNull();
    }

    // ---- folders and delete ----

    [Fact]
    public async Task EnsureDirectory_creates_missing_folders_once()
    {
        var storage = Storage();

        await storage.EnsureDirectoryAsync("a/b/c", default);
        await storage.EnsureDirectoryAsync("a/b/c", default);
        await storage.EnsureDirectoryAsync("", default);

        (await ListAsync(storage, "", recursive: true)).Select(e => (e.Path, e.IsDirectory)).Should().BeEquivalentTo(
            [("a", true), ("a/b", true), ("a/b/c", true)]);
        _api.Calls("createFolder").Should().Be(3);
    }

    [Fact]
    public async Task Parallel_writes_create_each_folder_once()
    {
        _api.OnCall = async (_, _, _) => await Task.Delay(1);
        var storage = Storage();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => WriteAsync(storage, $"v/sub/f{i}.txt", "x"))));

        _api.Calls("createFolder").Should().Be(2);
        (await ListAsync(storage, "v/sub")).Should().HaveCount(8);
    }

    [Fact]
    public async Task EnsureDirectory_through_a_file_conflicts()
    {
        _api.AddFile(Root, "f", Bytes("x"));

        await Storage().Invoking(s => s.EnsureDirectoryAsync("f/sub", default)).Should().ThrowAsync<StorageConflictException>();
    }

    [Fact]
    public async Task Delete_rules()
    {
        var full = _api.AddFolder(Root, "full");
        _api.AddFile(full, "inside.txt", Bytes("x"));
        var empty = _api.AddFolder(Root, "empty");
        var file = _api.AddFile(Root, "file.txt", Bytes("x"));
        var doc = _api.AddGoogleFile(Root, "Doc", DriveMimeTypes.Document);
        var hidden = _api.AddFolder(Root, "hidden");
        _api.AddGoogleFile(hidden, "Shortcut", "application/vnd.google-apps.shortcut");
        var storage = Storage();

        await storage.DeleteAsync(["file.txt", "empty", "missing", "missing/deeper", "Doc.docx"], default);
        _api.Exists(file).Should().BeFalse();
        _api.Exists(empty).Should().BeFalse();
        _api.Exists(doc).Should().BeFalse();

        await storage.Invoking(s => s.DeleteAsync(["full"], default)).Should().ThrowAsync<StorageConflictException>();
        await storage.Invoking(s => s.DeleteAsync(["hidden"], default)).Should().ThrowAsync<StorageConflictException>();
        await storage.Invoking(s => s.DeleteAsync(["full/inside.txt", ""], default)).Should().ThrowAsync<StorageConflictException>();
        _api.Exists(full).Should().BeTrue();
        _api.Children(full).Should().ContainSingle("the root check comes before any delete");

        await storage.DeleteAsync(["full/inside.txt", "full"], default);
        _api.Exists(full).Should().BeFalse();
        (await storage.StatAsync("full", default)).Should().BeNull();
    }

    [Fact]
    public async Task A_deleted_folder_can_be_created_again()
    {
        var storage = Storage();
        await storage.EnsureDirectoryAsync("a/b", default);
        await storage.DeleteAsync(["a/b"], default);

        await WriteAsync(storage, "a/b/f.txt", "x");

        (await ReadAsync(storage, "a/b/f.txt")).Should().Be("x");
    }

    // ---- errors and retries ----

    [Theory]
    [InlineData(401, "authError", typeof(StorageAccessDeniedException))]
    [InlineData(0, "invalid_grant", typeof(StorageAccessDeniedException))]
    [InlineData(403, "insufficientPermissions", typeof(StorageAccessDeniedException))]
    [InlineData(403, "insufficientFilePermissions", typeof(StorageAccessDeniedException))]
    [InlineData(404, "notFound", typeof(StorageNotFoundException))]
    [InlineData(429, "rateLimitExceeded", typeof(StorageUnavailableException))]
    [InlineData(403, "rateLimitExceeded", typeof(StorageUnavailableException))]
    [InlineData(403, "userRateLimitExceeded", typeof(StorageUnavailableException))]
    [InlineData(500, "backendError", typeof(StorageUnavailableException))]
    [InlineData(503, "", typeof(StorageUnavailableException))]
    [InlineData(403, "storageQuotaExceeded", typeof(StorageIOException))]
    [InlineData(403, "teamDriveFileLimitExceeded", typeof(StorageIOException))]
    [InlineData(403, "exportSizeLimitExceeded", typeof(StorageIOException))]
    [InlineData(400, "badRequest", typeof(StorageIOException))]
    public void Error_mapping(int status, string reason, Type expected)
    {
        var mapped = DriveErrors.Map(new DriveApiException(status, reason), "p", default);

        mapped.Should().BeOfType(expected);
        ((StorageException)mapped).Path.Should().Be("p");
        mapped.InnerException.Should().BeOfType<DriveApiException>();
        if (mapped is StorageIOException io) io.NotReadable.Should().Be(reason == "exportSizeLimitExceeded");
    }

    [Fact]
    public void Network_failures_are_unavailable_and_the_callers_cancellation_stays()
    {
        DriveErrors.Map(new HttpRequestException("dns"), "p", default).Should().BeOfType<StorageUnavailableException>();
        DriveErrors.Map(new IOException("reset"), "p", default).Should().BeOfType<StorageUnavailableException>();
        DriveErrors.Map(new TaskCanceledException("timeout"), "p", default).Should().BeOfType<StorageUnavailableException>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new OperationCanceledException(cts.Token);
        DriveErrors.Map(cancelled, "p", cts.Token).Should().BeSameAs(cancelled);
        DriveErrors.Map(new InvalidOperationException("?"), "p", default).Should().BeOfType<StorageIOException>();
    }

    [Fact]
    public async Task Errors_surface_as_storage_exceptions()
    {
        _api.AddFile(Root, "a.txt", Bytes("x"));
        var storage = Storage();

        _api.OnCall = (_, _, _) => throw new DriveApiException(403, "insufficientPermissions");
        await storage.Invoking(s => s.StatAsync("a.txt", default)).Should().ThrowAsync<StorageAccessDeniedException>();
        await storage.Invoking(s => ListAsync(s, "")).Should().ThrowAsync<StorageAccessDeniedException>();

        _api.OnCall = (operation, _, _) => operation == "upload" ? throw new DriveApiException(403, "storageQuotaExceeded") : Task.CompletedTask;
        (await storage.Invoking(s => WriteAsync(s, "b.txt", "x")).Should().ThrowAsync<StorageIOException>())
            .Which.Path.Should().Be("b.txt");
    }

    [Theory]
    [InlineData(429, "rateLimitExceeded")]
    [InlineData(403, "rateLimitExceeded")]
    [InlineData(403, "userRateLimitExceeded")]
    [InlineData(500, "backendError")]
    [InlineData(503, "")]
    public async Task Transient_errors_are_retried(int status, string reason)
    {
        _api.AddFile(Root, "a.txt", Bytes("x"));
        var failures = 2;
        _api.OnCall = (operation, _, _) =>
            operation == "list" && failures-- > 0 ? throw new DriveApiException(status, reason) : Task.CompletedTask;

        var entry = await DriveAsync(Storage().StatAsync("a.txt", default));

        entry.Should().NotBeNull();
        _api.Calls("list").Should().Be(3);
    }

    [Fact]
    public async Task Rate_limits_are_retried_and_cancellation_stays_prompt()
    {
        var storage = Storage();
        var failures = 3;
        _api.OnCall = (operation, _, _) =>
            operation == "get" && failures-- > 0 ? throw new DriveApiException(429, "rateLimitExceeded") : Task.CompletedTask;

        // Retried with exponential backoff (plus up to a second of jitter) until it succeeds.
        (await DriveAsync(storage.StatAsync("", default))).Should().NotBeNull();
        _api.Calls("get").Should().Be(4);
        var delays = _time.DueTimes;
        delays.Should().HaveCount(3);
        for (var i = 0; i < 3; i++)
            delays[i].Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1 << i)).And.BeLessThan(TimeSpan.FromSeconds((1 << i) + 1));

        // Six attempts at most, then unavailable.
        failures = int.MaxValue;
        await storage.Invoking(s => DriveAsync(s.StatAsync("", default))).Should().ThrowAsync<StorageUnavailableException>();
        _api.Calls("get").Should().Be(4 + DriveRetry.MaxAttempts);
        DriveRetry.MaxAttempts.Should().Be(6);

        // Cancelled while waiting for the next attempt: ends at once, without the clock moving.
        using var cts = new CancellationTokenSource();
        var pending = storage.StatAsync("", cts.Token);
        for (var i = 0; i < 500 && _time.PendingTimers == 0; i++) await Task.Delay(10);
        _time.PendingTimers.Should().Be(1);
        await cts.CancelAsync();
        await pending.Invoking(p => p.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<OperationCanceledException>();
        _time.PendingTimers.Should().Be(0);
    }

    private Task WriteDrivenAsync(IStorage storage, string path, string text, CreateOptions? options = null) =>
        DriveAsync(Task.Run(async () =>
        {
            await WriteAsync(storage, path, text, options);
            return true;
        }));

    [Fact]
    public async Task Uploads_are_retried_on_rate_limits()
    {
        var storage = Storage();
        var failures = 2;
        _api.OnCall = (operation, _, _) =>
            operation == "upload" && failures-- > 0 ? throw new DriveApiException(429, "rateLimitExceeded") : Task.CompletedTask;

        await WriteDrivenAsync(storage, "a.txt", "content");

        (await ReadAsync(storage, "a.txt")).Should().Be("content");
        _api.Calls("upload").Should().Be(3);
        _api.Children(Root).Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_server_error_before_the_upload_is_retried(bool overwrite)
    {
        var storage = Storage();
        var failures = 1;
        _api.OnCall = (operation, _, _) =>
            operation == "upload" && failures-- > 0 ? throw new DriveApiException(503, "") : Task.CompletedTask;

        await WriteDrivenAsync(storage, "a.txt", "content", new CreateOptions(Overwrite: overwrite));

        _api.Calls("upload").Should().Be(2);
        _api.Children(Root).Should().ContainSingle();
        (await ReadAsync(storage, "a.txt")).Should().Be("content");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_server_error_after_the_upload_landed_keeps_the_one_file(bool overwrite)
    {
        var storage = Storage();
        var failures = 1;
        _api.AfterCall = (operation, _, _) =>
            operation == "upload" && failures-- > 0 ? throw new DriveApiException(503, "") : Task.CompletedTask;

        await WriteDrivenAsync(storage, "a.txt", "content", new CreateOptions(Overwrite: overwrite));

        _api.Calls("upload").Should().Be(1);
        _api.Children(Root).Should().ContainSingle();
        (await ReadAsync(storage, "a.txt")).Should().Be("content");
    }

    [Fact]
    public async Task A_server_error_after_the_upload_with_other_content_there_is_not_taken_as_ours()
    {
        var storage = Storage();
        _api.AfterCall = (operation, id, _) =>
        {
            if (operation != "upload") return Task.CompletedTask;
            _api.Remove(id);
            _api.AddFile(Root, "a.txt", Bytes("theirs"));
            throw new DriveApiException(503, "");
        };

        await storage.Invoking(s => WriteDrivenAsync(s, "a.txt", "ours")).Should().ThrowAsync<StorageUnavailableException>();

        _api.Calls("upload").Should().Be(1);
        _api.Children(Root).Should().ContainSingle().Which.Size.Should().Be(6);
    }

    [Fact]
    public async Task A_server_error_after_a_folder_was_created_keeps_the_one_folder()
    {
        var storage = Storage();
        var failures = 1;
        _api.AfterCall = (operation, _, _) =>
            operation == "createFolder" && failures-- > 0 ? throw new DriveApiException(503, "") : Task.CompletedTask;

        await WriteDrivenAsync(storage, "d/f.txt", "x");

        _api.Calls("createFolder").Should().Be(1);
        _api.Children(Root).Should().ContainSingle().Which.IsFolder.Should().BeTrue();
        (await ReadAsync(storage, "d/f.txt")).Should().Be("x");
    }

    [Fact]
    public async Task A_server_error_before_a_folder_create_is_retried()
    {
        var storage = Storage();
        var failures = 1;
        _api.OnCall = (operation, _, _) =>
            operation == "createFolder" && failures-- > 0 ? throw new DriveApiException(500, "backendError") : Task.CompletedTask;

        await WriteDrivenAsync(storage, "d/f.txt", "x");

        _api.Calls("createFolder").Should().Be(2);
        _api.Children(Root).Should().ContainSingle();
    }

    [Fact]
    public async Task Server_errors_on_every_upload_attempt_end_as_unavailable()
    {
        var storage = Storage();
        _api.OnCall = (operation, _, _) => operation == "upload" ? throw new DriveApiException(503, "") : Task.CompletedTask;

        await storage.Invoking(s => WriteDrivenAsync(s, "a.txt", "content")).Should().ThrowAsync<StorageUnavailableException>();

        _api.Calls("upload").Should().Be(DriveRetry.MaxAttempts);
        _api.Children(Root).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_while_waiting_to_repeat_an_upload_is_prompt()
    {
        var storage = Storage();
        _api.OnCall = (operation, _, _) => operation == "upload" ? throw new DriveApiException(503, "") : Task.CompletedTask;
        using var cts = new CancellationTokenSource();
        var writer = await storage.CreateAsync("a.txt", new CreateOptions(), default);
        await writer.WriteAsync(Bytes("x"));

        var commit = writer.CommitAsync(cts.Token);
        for (var i = 0; i < 500 && _time.PendingTimers == 0; i++) await Task.Delay(10);
        _time.PendingTimers.Should().Be(1);
        await cts.CancelAsync();

        await commit.Invoking(c => c.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<OperationCanceledException>();
        await writer.DisposeAsync();
    }
}
