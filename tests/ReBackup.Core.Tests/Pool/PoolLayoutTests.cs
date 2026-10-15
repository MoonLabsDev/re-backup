using FluentAssertions;
using ReBackup.Core.Pool;

namespace ReBackup.Core.Tests.Pool;

public class PoolLayoutTests
{
    [Fact]
    public void BlobPath_splits_two_levels()
    {
        PoolLayout.BlobPath("xxh128:abcdef0123456789abcdef0123456789")
            .Should().Be("re-pool/ab/cd/abcdef0123456789abcdef0123456789");
    }

    [Theory]
    [InlineData("xxh64:0123456789abcdef")]
    [InlineData("xxh128:XYZ")]
    [InlineData("")]
    public void BlobPath_rejects_xxh64_and_garbage(string hash)
    {
        var act = () => PoolLayout.BlobPath(hash);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Format_is_lower_hex_32()
    {
        PoolLayout.Format(UInt128.MaxValue).Should().Be("xxh128:" + new string('f', 32));
        PoolLayout.Format(UInt128.One).Should().Be("xxh128:" + new string('0', 31) + "1");
    }

    [Fact]
    public void TryParseHex_returns_the_hex_part_only_for_valid_hashes()
    {
        PoolLayout.TryParseHex("xxh128:abcdef0123456789abcdef0123456789", out var hex).Should().BeTrue();
        hex.Should().Be("abcdef0123456789abcdef0123456789");
        PoolLayout.TryParseHex("xxh128:abc", out _).Should().BeFalse();
        PoolLayout.TryParseHex(null!, out _).Should().BeFalse();
    }
}
