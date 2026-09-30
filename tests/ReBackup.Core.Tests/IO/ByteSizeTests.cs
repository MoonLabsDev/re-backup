using FluentAssertions;
using ReBackup.Core.IO;

namespace ReBackup.Core.Tests.IO;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(5368709120L, "5.0 GB")]
    [InlineData(1099511627776L, "1.0 TB")]
    public void Format_uses_base_1024_with_one_decimal(long bytes, string expected)
    {
        ByteSize.Format(bytes).Should().Be(expected);
    }
}
