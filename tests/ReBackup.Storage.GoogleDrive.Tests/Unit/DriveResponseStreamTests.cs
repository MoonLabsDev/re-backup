using FluentAssertions;

namespace ReBackup.Storage.GoogleDrive.Tests.Unit;

/// <summary>A download body that stops arriving must not hang a copy: every read has an idle timeout of its own.</summary>
public sealed class DriveResponseStreamTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task A_read_that_gets_no_data_within_the_idle_timeout_times_out()
    {
        var owner = new Owner();
        await using var stream = new DriveResponseStream(new StalledStream(), owner, Short);

        var act = async () => await stream.ReadAsync(new byte[16]);

        await act.Should().ThrowAsync<TimeoutException>().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_timed_out_read_is_unavailable_for_the_storage()
    {
        await using var stream = new DriveResponseStream(new StalledStream(), null, Short);
        Exception? failure = null;
        try
        {
            _ = await stream.ReadAsync(new byte[16]);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        DriveErrors.Map(failure!, "a.bin", CancellationToken.None).Should().BeOfType<StorageUnavailableException>();
    }

    [Fact]
    public void A_synchronous_read_times_out_too()
    {
        using var stream = new DriveResponseStream(new StalledStream(), null, Short);

        var act = () => stream.Read(new byte[16], 0, 16);

        act.Should().Throw<TimeoutException>();
    }

    [Fact]
    public async Task Cancellation_by_the_caller_stays_cancellation()
    {
        await using var stream = new DriveResponseStream(new StalledStream(), null, TimeSpan.FromMinutes(5));
        using var cancel = new CancellationTokenSource(Short);

        var act = async () => await stream.ReadAsync(new byte[16], cancel.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>().WaitAsync(TimeSpan.FromSeconds(10)))
            .Which.Should().NotBeOfType<TimeoutException>();
    }

    [Fact]
    public async Task Slow_data_that_keeps_arriving_is_read_whole()
    {
        // Each read waits less than the idle timeout; together they take longer than it.
        await using var stream = new DriveResponseStream(new TrickleStream(chunks: 6, delay: TimeSpan.FromMilliseconds(60)), null,
            TimeSpan.FromMilliseconds(250));
        using var copy = new MemoryStream();

        await stream.CopyToAsync(copy);

        copy.Length.Should().Be(6);
    }

    [Fact]
    public async Task Disposing_disposes_the_body_and_its_response()
    {
        var owner = new Owner();
        var inner = new MemoryStream([1, 2, 3]);
        var stream = new DriveResponseStream(inner, owner, Short);

        await stream.DisposeAsync();

        owner.Disposed.Should().BeTrue();
        inner.CanRead.Should().BeFalse();
    }

    private sealed class Owner : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A body whose next bytes never come; a read ends only when its token is cancelled.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that gives one byte per read after a delay.</summary>
    private sealed class TrickleStream(int chunks, TimeSpan delay) : Stream
    {
        private int _left = chunks;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left == 0) return 0;
            await Task.Delay(delay, cancellationToken);
            _left--;
            buffer.Span[0] = 1;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
