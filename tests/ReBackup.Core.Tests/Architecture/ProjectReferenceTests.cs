using System.Xml.Linq;
using FluentAssertions;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Architecture;

/// <summary>The libraries stay free of what they must not depend on; read from the project files.</summary>
public class ProjectReferenceTests
{
    [Theory]
    [InlineData("ReBackup.Shared", new string[0])]
    // Core sees storage only through IStorage / IStorageFactory; the App composes the S3 factory.
    [InlineData("ReBackup.Core", new[] { "ReBackup.Shared", "ReBackup.Storage" })]
    [InlineData("ReBackup.Shared.Wpf", new[] { "ReBackup.Shared", "ReBackup.Storage.S3" })]
    [InlineData("ReBackup.Storage", new string[0])]
    [InlineData("ReBackup.Storage.S3", new[] { "ReBackup.Storage", "ReBackup.Shared" })]
    [InlineData("ReBackup.Storage.GoogleDrive", new[] { "ReBackup.Storage", "ReBackup.Shared" })]
    public void Library_references_only_what_is_allowed(string project, string[] allowed)
    {
        ProjectReferencesOf(project).Should().BeSubsetOf(allowed);
    }

    // The Google libraries stay in the Drive provider: Core never sees it, and ReS3 (Shared.Wpf) must not pull it in.
    [Theory]
    [InlineData("ReBackup.Core")]
    [InlineData("ReBackup.Shared.Wpf")]
    [InlineData("ReBackup.Shared")]
    [InlineData("ReBackup.Storage")]
    [InlineData("ReBackup.Storage.S3")]
    public void Library_does_not_reference_the_Drive_provider(string project) =>
        ProjectReferencesOf(project).Should().NotContain("ReBackup.Storage.GoogleDrive");

    [Theory]
    [InlineData("ReBackup.Shared")]
    public void Library_has_no_package_references(string project) =>
        PackageReferencesOf(project).Should().BeEmpty();

    [Theory]
    // DPAPI (SecretProtector) lives in ReBackup.Storage and is shared by the providers.
    [InlineData("ReBackup.Storage", new[] { "System.Security.Cryptography.ProtectedData" })]
    [InlineData("ReBackup.Storage.S3", new[] { "AWSSDK.S3" })]
    [InlineData("ReBackup.Storage.GoogleDrive", new[] { "Google.Apis.Drive.v3", "Google.Apis.Auth" })]
    [InlineData("ReBackup.Shared.Wpf", new[] { "CommunityToolkit.Mvvm" })]
    public void Library_packages_are_exactly(string project, string[] expected) =>
        PackageReferencesOf(project).Should().BeEquivalentTo(expected);

    private static XDocument Load(string project)
    {
        var path = Path.Combine(RepoPaths.Root, "src", project, project + ".csproj");
        File.Exists(path).Should().BeTrue($"{path} must exist");
        return XDocument.Load(path);
    }

    private static IEnumerable<string> ProjectReferencesOf(string project) =>
        Load(project).Descendants("ProjectReference")
            .Select(item => Path.GetFileNameWithoutExtension(((string?)item.Attribute("Include") ?? "").Replace('\\', '/')))
            .ToList();

    private static IEnumerable<string> PackageReferencesOf(string project) =>
        Load(project).Descendants("PackageReference").Select(item => (string?)item.Attribute("Include") ?? "").ToList();
}
