using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Pool;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Pool;

public class TargetLockTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 8, 30, 0, TimeSpan.Zero));
    private readonly InMemoryStorage _target = new();

    private Task<TargetLock?> AcquireAsync(string host = "PC-A", string operation = "backup") =>
        TargetLock.TryAcquireAsync(_target, host, operation, _time, Ct);

    [Fact]
    public async Task Acquire_writes_lock_file()
    {
        await using var held = (await AcquireAsync("PC-A", "backup"))!;

        held.Should().NotBeNull();
        _target.Files.Should().BeEquivalentTo([PoolLayout.LockFile]);
        using var json = JsonDocument.Parse(_target.ReadAllBytes(PoolLayout.LockFile));
        json.RootElement.GetProperty("host").GetString().Should().Be("PC-A");
        json.RootElement.GetProperty("pid").GetInt32().Should().Be(Environment.ProcessId);
        json.RootElement.GetProperty("operation").GetString().Should().Be("backup");
        json.RootElement.GetProperty("startedUtc").GetDateTime().Should().Be(_time.GetUtcNow().UtcDateTime);
        Guid.TryParse(json.RootElement.GetProperty("token").GetString(), out _).Should().BeTrue();
        held.Info.Host.Should().Be("PC-A");
        held.Info.Pid.Should().Be(Environment.ProcessId);
        held.Info.Operation.Should().Be("backup");
        held.Info.StartedUtc.Should().Be(_time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Second_acquire_fails_while_held()
    {
        await using var first = await AcquireAsync("PC-A", "backup");

        var second = await AcquireAsync("PC-B", "delete");

        first.Should().NotBeNull();
        second.Should().BeNull();
        (await TargetLock.ReadAsync(_target, Ct))!.Host.Should().Be("PC-A");
    }

    [Fact]
    public async Task Dispose_releases()
    {
        var first = (await AcquireAsync())!;

        await first.DisposeAsync();

        _target.Files.Should().BeEmpty();
        await using var again = await AcquireAsync("PC-B", "verify");
        again.Should().NotBeNull();
    }

    [Fact]
    public async Task Dispose_does_not_delete_a_foreign_lock()
    {
        var first = (await AcquireAsync("PC-A", "backup"))!;
        await TargetLock.ForceReleaseAsync(_target, Ct);
        await using var foreign = await AcquireAsync("PC-B", "delete");

        await first.DisposeAsync();

        foreign.Should().NotBeNull();
        (await TargetLock.ReadAsync(_target, Ct))!.Should().Be(foreign!.Info);
    }

    [Fact]
    public async Task ForceRelease_removes_any_lock()
    {
        await using var held = await AcquireAsync();

        await TargetLock.ForceReleaseAsync(_target, Ct);

        _target.Files.Should().BeEmpty();
        await TargetLock.ForceReleaseAsync(_target, Ct);   // no lock: no error
    }

    [Fact]
    public async Task ReadAsync_returns_info_or_null()
    {
        (await TargetLock.ReadAsync(_target, Ct)).Should().BeNull();

        await using var held = (await AcquireAsync("PC-A", "copy"))!;

        (await TargetLock.ReadAsync(_target, Ct)).Should().Be(held.Info);
    }
}
