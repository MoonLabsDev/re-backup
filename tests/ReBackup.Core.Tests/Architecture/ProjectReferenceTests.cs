using System.Xml.Linq;
using FluentAssertions;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Architecture;

/// <summary>The libraries stay free of what they must not depend on; read from the project files.</summary>
public class ProjectReferenceTests
{
    [Theory]
    [InlineData("ReBackup.Shared", new string[0])]
    [InlineData("ReBackup.Shared.Wpf", new[] { "ReBackup.Shared" })]
    [InlineData("ReBackup.Storage", new string[0], Skip = "Task 3")]
    public void Library_references_only_what_is_allowed(string project, string[] allowed)
    {
        ProjectReferencesOf(project).Should().BeSubsetOf(allowed);
    }

    [Theory]
    [InlineData("ReBackup.Shared")]
    [InlineData("ReBackup.Storage", Skip = "Task 3")]
    public void Library_has_no_package_references(string project) =>
        PackageReferencesOf(project).Should().BeEmpty();

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
