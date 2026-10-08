using FluentAssertions;

namespace ReBackup.Storage.S3.Tests.Unit;

public class S3RegionsTests
{
    [Theory]
    [InlineData("us-east-1")]
    [InlineData("eu-central-1")]
    [InlineData("us-gov-west-1")]
    [InlineData("ap-southeast-2")]
    [InlineData("cn-northwest-1")]
    [InlineData("ap-southeast-12")]
    public void Region_names_are_valid(string region) => S3Regions.IsValid(region).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("eu-central")]
    [InlineData("eu-central-1 ")]
    [InlineData(" eu-central-1")]
    [InlineData("eu-central-1\n")]
    [InlineData("EU-CENTRAL-1")]
    [InlineData("eu-central-123")]
    [InlineData("e-central-1")]
    [InlineData("eu--1")]
    [InlineData("not a region!")]
    public void Anything_else_is_invalid(string? region) => S3Regions.IsValid(region).Should().BeFalse();
}
