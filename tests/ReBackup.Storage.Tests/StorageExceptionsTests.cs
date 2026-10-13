using FluentAssertions;
using ReBackup.Storage;

namespace ReBackup.Storage.Tests;

public class StorageExceptionsTests
{
    [Fact]
    public void An_archived_file_is_not_readable() =>
        new StorageIOException("a", "archived") { Archived = true }.NotReadable.Should().BeTrue();

    [Fact]
    public void Not_readable_can_be_set_without_archived()
    {
        var ex = new StorageIOException("a", "too large") { NotReadable = true };

        ex.NotReadable.Should().BeTrue();
        ex.Archived.Should().BeFalse();
    }

    [Fact]
    public void A_plain_io_error_is_readable() =>
        new StorageIOException("a").NotReadable.Should().BeFalse();
}
