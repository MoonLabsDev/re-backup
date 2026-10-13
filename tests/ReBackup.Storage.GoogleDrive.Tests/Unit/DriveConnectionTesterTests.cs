using FluentAssertions;
using ReBackup.Storage.GoogleDrive.Connections;
using ReBackup.Storage.GoogleDrive.Tests.Fakes;

namespace ReBackup.Storage.GoogleDrive.Tests.Unit;

public sealed class DriveConnectionTesterTests
{
    private const string Email = "backup@project.iam.gserviceaccount.com";
    private const string SharedDriveId = "sd1";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly InMemoryDriveApi _api = new();

    public DriveConnectionTesterTests()
    {
        _api.AddSharedDrive(SharedDriveId, "Backups");
    }

    private DriveConnectionTester Tester() => new(_ => _api);

    private static DriveConnection Connection(string rootId, string? sharedDriveId) =>
        new(new DriveConnectionInfo("c1", "Docs", "a1", rootId, "Root", sharedDriveId), Email, "{}");

    private static DriveConnection SharedDrive() => Connection(SharedDriveId, SharedDriveId);

    private static DriveConnection MyDriveFolder() => Connection(InMemoryDriveApi.RootId, null);

    private IReadOnlyList<DriveItem> Everything(string folderId) =>
        _api.Children(folderId).SelectMany(c => c.IsFolder ? [c, .. Everything(c.Id)] : new[] { c }).ToList();

    [Fact]
    public async Task A_shared_drive_target_is_ok_and_the_probe_file_is_written_and_removed()
    {
        var result = await Tester().RunAsync(SharedDrive(), "", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.Ok, result.Message);
        _api.Calls("upload").Should().Be(1);
        _api.Calls("delete").Should().Be(1);
        _api.Children(SharedDriveId).Should().BeEmpty();
    }

    [Fact]
    public async Task The_probe_is_written_into_the_path_when_it_exists()
    {
        var backups = _api.AddFolder(SharedDriveId, "backups");
        string? probeParent = null;
        _api.OnCall = (operation, id, _) =>
        {
            if (operation == "upload") probeParent = id;
            return Task.CompletedTask;
        };

        var result = await Tester().RunAsync(SharedDrive(), "backups", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.Ok, result.Message);
        probeParent.Should().Be(backups);
        Everything(SharedDriveId).Should().ContainSingle().Which.Id.Should().Be(backups);
    }

    [Fact]
    public async Task A_target_path_that_does_not_exist_yet_is_ok_and_nothing_is_left_behind()
    {
        var result = await Tester().RunAsync(SharedDrive(), "backups/pc1", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.Ok, result.Message);
        result.Message.Should().Contain("created");
        _api.Calls("upload").Should().Be(1);
        Everything(SharedDriveId).Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_on_a_shared_drive_is_a_valid_target()
    {
        var folder = _api.AddFolder(SharedDriveId, "team");

        var result = await Tester().RunAsync(Connection(folder, SharedDriveId), "", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.Ok, result.Message);
    }

    [Fact]
    public async Task A_target_on_a_my_drive_folder_is_not_a_shared_drive_and_nothing_is_written()
    {
        var result = await Tester().RunAsync(MyDriveFolder(), "", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.NotSharedDrive);
        result.Message.Should().Contain("shared drive");
        _api.Calls("upload").Should().Be(0);
    }

    [Fact]
    public async Task A_target_whose_connection_claims_a_shared_drive_is_judged_by_where_the_root_really_is()
    {
        var result = await Tester().RunAsync(Connection(InMemoryDriveApi.RootId, "sd-old"), "", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.NotSharedDrive);
    }

    [Fact]
    public async Task A_my_drive_folder_is_a_valid_source_and_nothing_is_written()
    {
        _api.AddFolder(InMemoryDriveApi.RootId, "photos");

        var result = await Tester().RunAsync(MyDriveFolder(), "photos", asTarget: false, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.Ok, result.Message);
        _api.Calls("upload").Should().Be(0);
        _api.Calls("delete").Should().Be(0);
    }

    [Fact]
    public async Task A_source_path_that_does_not_exist_is_not_found()
    {
        var result = await Tester().RunAsync(MyDriveFolder(), "photos", asTarget: false, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.NotFound);
        result.Message.Should().Contain("photos");
    }

    [Fact]
    public async Task A_path_that_is_a_file_is_not_found()
    {
        _api.AddFile(SharedDriveId, "photos", [1, 2, 3]);

        var result = await Tester().RunAsync(SharedDrive(), "photos", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.NotFound);
        result.Message.Should().Contain("file");
        _api.Calls("upload").Should().Be(0);
    }

    [Fact]
    public async Task A_root_that_is_gone_or_not_shared_is_not_found_with_the_share_hint()
    {
        var result = await Tester().RunAsync(Connection("unknown", null), "", asTarget: false, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.NotFound);
        result.Message.Should().Contain("share").And.Contain(Email);
    }

    [Fact]
    public async Task A_refused_root_is_access_denied_with_the_share_hint()
    {
        _api.OnCall = (_, _, _) => throw new DriveApiException(403, "insufficientFilePermissions");

        var result = await Tester().RunAsync(SharedDrive(), "", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.AccessDenied);
        result.Message.Should().Contain("share").And.Contain(Email);
    }

    [Fact]
    public async Task An_invalid_key_is_access_denied()
    {
        _api.OnCall = (_, _, _) => throw new DriveApiException(400, "invalid_grant");

        var result = await Tester().RunAsync(SharedDrive(), "", asTarget: false, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.AccessDenied);
    }

    [Fact]
    public async Task A_refused_write_is_access_denied()
    {
        _api.OnCall = (operation, _, _) =>
            operation == "upload" ? throw new DriveApiException(403, "insufficientFilePermissions") : Task.CompletedTask;

        var result = await Tester().RunAsync(SharedDrive(), "", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.AccessDenied);
        result.Message.Should().Contain("write");
    }

    [Fact]
    public async Task A_refused_delete_is_access_denied_and_names_the_role()
    {
        _api.OnCall = (operation, _, _) =>
            operation == "delete" ? throw new DriveApiException(403, "insufficientFilePermissions") : Task.CompletedTask;

        var result = await Tester().RunAsync(SharedDrive(), "", asTarget: true, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.AccessDenied);
        result.Message.Should().Contain("delete").And.Contain("Manager");
    }

    [Fact]
    public async Task A_network_failure_is_an_error()
    {
        _api.OnCall = (_, _, _) => throw new HttpRequestException("no route");

        var result = await new DriveConnectionTester(_ => _api, new ImmediateTime()).RunAsync(SharedDrive(), "", asTarget: false, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.Error);
        result.Message.Should().Contain("not available");
    }

    [Fact]
    public async Task An_ambiguous_path_is_an_error_naming_it()
    {
        _api.AddFolder(SharedDriveId, "photos");
        _api.AddFolder(SharedDriveId, "photos");

        var result = await Tester().RunAsync(SharedDrive(), "photos", asTarget: false, Ct);

        result.Outcome.Should().Be(DriveTestOutcome.Error);
        result.Message.Should().Contain("ambiguous name: photos");
    }

    [Fact]
    public async Task Cancellation_is_thrown()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var act = () => Tester().RunAsync(SharedDrive(), "", asTarget: true, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_invalid_path_is_refused()
    {
        var act = () => Tester().RunAsync(SharedDrive(), "/a", asTarget: true, Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task The_api_is_disposed_after_the_run()
    {
        var disposed = false;
        var api = new DisposingApi(_api, () => disposed = true);

        await new DriveConnectionTester(_ => api).RunAsync(SharedDrive(), "", asTarget: false, Ct);

        disposed.Should().BeTrue();
    }

    /// <summary>A clock whose timers fire at once: retry waits take no time.</summary>
    private sealed class ImmediateTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private sealed class DisposingApi(IDriveApi inner, Action onDispose) : IDriveApi, IDisposable
    {
        public void Dispose() => onDispose();

        public Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, CancellationToken ct, string? name = null) => inner.ListChildrenAsync(folderId, ct, name);
        public Task<DriveItem> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<DriveItem> CreateFolderAsync(string parentId, string name, CancellationToken ct) => inner.CreateFolderAsync(parentId, name, ct);
        public Task<DriveItem> UploadAsync(string parentId, string name, Stream content, DateTime? modifiedUtc, CancellationToken ct) => inner.UploadAsync(parentId, name, content, modifiedUtc, ct);
        public Task<DriveItem> UpdateContentAsync(string id, Stream content, DateTime? modifiedUtc, CancellationToken ct) => inner.UpdateContentAsync(id, content, modifiedUtc, ct);
        public Task DeleteAsync(string id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public Task<Stream> DownloadAsync(string id, CancellationToken ct) => inner.DownloadAsync(id, ct);
        public Task<Stream> ExportAsync(string id, string mimeType, CancellationToken ct) => inner.ExportAsync(id, mimeType, ct);
        public Task<IReadOnlyList<SharedDrive>> ListSharedDrivesAsync(CancellationToken ct) => inner.ListSharedDrivesAsync(ct);
    }
}
