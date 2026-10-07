using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

public class VersionMarkersTests
{
    private const string Version = "2026_09_01-02_00 Projects";
    private static readonly DateTime Started = new(2026, 9, 1, 0, 0, 5, DateTimeKind.Utc);
    private static readonly MarkerInfo Marker = new(1, "p1", "Projects", Started, "HOST");
    private readonly InMemoryStorage _storage = new();

    private string Text(string path) => Encoding.UTF8.GetString(_storage.ReadAllBytes(path));

    [Fact]
    public async Task A_pending_marker_is_written_with_the_agreed_fields()
    {
        await VersionMarkers.WritePendingAsync(_storage, Version, Marker, CancellationToken.None);

        var json = JsonNode.Parse(Text($"{Version}/re-pending.json"))!.AsObject();
        json.Select(p => p.Key).Should().BeEquivalentTo("formatVersion", "planId", "planName", "startedUtc", "host");
        json["formatVersion"]!.GetValue<int>().Should().Be(1);
        json["planId"]!.GetValue<string>().Should().Be("p1");
        json["planName"]!.GetValue<string>().Should().Be("Projects");
        json["host"]!.GetValue<string>().Should().Be("HOST");
        (await VersionMarkers.TryReadAsync(_storage, $"{Version}/re-pending.json", CancellationToken.None)).Should().Be(Marker);
    }

    [Fact]
    public async Task A_pending_marker_is_exclusive()
    {
        await VersionMarkers.WritePendingAsync(_storage, Version, Marker, CancellationToken.None);

        var act = () => VersionMarkers.WritePendingAsync(_storage, Version, Marker with { PlanId = "p2" }, CancellationToken.None);

        await act.Should().ThrowAsync<StorageConflictException>();
        (await VersionMarkers.TryReadAsync(_storage, $"{Version}/re-pending.json", CancellationToken.None))!.PlanId.Should().Be("p1");
    }

    [Fact]
    public async Task A_deleting_marker_overwrites_an_earlier_one()
    {
        await VersionMarkers.WriteDeletingAsync(_storage, Version, Marker, CancellationToken.None);
        await VersionMarkers.WriteDeletingAsync(_storage, Version, Marker with { Host = "OTHER" }, CancellationToken.None);

        (await VersionMarkers.TryReadAsync(_storage, $"{Version}/re-deleting.json", CancellationToken.None))!.Host.Should().Be("OTHER");
    }

    [Theory]
    [InlineData(null)]                                    // missing
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"formatVersion\": 1 }")]               // no plan id
    [InlineData("{ \"planId\": 5 }")]
    [InlineData("{ \"formatVersion\": 2, \"planId\": \"p1\", \"planName\": \"Projects\" }")]   // a newer format
    public async Task A_missing_or_unreadable_marker_reads_as_null(string? content)
    {
        if (content is not null)
            _storage.AddFile($"{Version}/re-deleting.json", Encoding.UTF8.GetBytes(content));

        (await VersionMarkers.TryReadAsync(_storage, $"{Version}/re-deleting.json", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task An_unavailable_storage_is_not_an_unreadable_marker()
    {
        var offline = new WrappedStorage(_storage) { Unavailable = true };

        var act = () => VersionMarkers.TryReadAsync(offline, $"{Version}/re-deleting.json", CancellationToken.None);

        await act.Should().ThrowAsync<StorageUnavailableException>();
    }
}
