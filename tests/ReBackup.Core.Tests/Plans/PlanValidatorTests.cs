using FluentAssertions;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Plans;

public class PlanValidatorTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private BackupPlan ValidPlan() => new()
    {
        Name = "Projects",
        Source = _tmp.CreateDir("src"),
        Target = _tmp.PathOf("dst"),
    };

    private static IReadOnlyList<string> Validate(BackupPlan plan, params BackupPlan[] others) =>
        PlanValidator.Validate(plan, others.Append(plan));

    [Fact]
    public void Valid_plan_has_no_errors()
    {
        Validate(ValidPlan()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "Name is required.")]
    [InlineData("   ", "Name is required.")]
    [InlineData(" Projects", "Name must not start or end with spaces.")]
    [InlineData("Projects.", "Name must not end with a dot.")]
    [InlineData("a/b", "Name contains characters that are not allowed in folder names.")]
    [InlineData("a:b", "Name contains characters that are not allowed in folder names.")]
    public void Invalid_names_are_reported(string name, string expected)
    {
        var plan = ValidPlan();
        plan.Name = name;

        Validate(plan).Should().Contain(expected);
    }

    [Fact]
    public void Duplicate_name_in_another_plan_is_reported_case_insensitively()
    {
        var plan = ValidPlan();
        var other = ValidPlan();
        other.Name = "PROJECTS";

        Validate(plan, other).Should().Contain("Another plan is already named \"Projects\".");
    }

    [Fact]
    public void Same_plan_is_not_a_duplicate_of_itself()
    {
        var plan = ValidPlan();
        var sameIdCopy = plan.Clone();

        PlanValidator.Validate(plan, [plan, sameIdCopy]).Should().BeEmpty();
    }

    [Fact]
    public void Missing_source_and_target_are_reported()
    {
        var plan = ValidPlan();
        plan.Source = "";
        plan.Target = "";

        Validate(plan).Should().Contain(["Source folder is required.", "Target folder is required."]);
    }

    [Fact]
    public void Relative_paths_are_reported()
    {
        var plan = ValidPlan();
        plan.Source = "relative";
        plan.Target = @"also\relative";

        Validate(plan).Should().Contain(["Source must be an absolute path.", "Target must be an absolute path."]);
    }

    [Fact]
    public void Nonexistent_source_is_reported()
    {
        var plan = ValidPlan();
        plan.Source = _tmp.PathOf("missing");

        Validate(plan).Should().Contain("Source folder does not exist.");
    }

    [Fact]
    public void Target_inside_source_is_reported()
    {
        var plan = ValidPlan();
        plan.Target = Path.Combine(plan.Source, "backups");

        Validate(plan).Should().Contain("Target must not be inside the source.");
    }

    [Fact]
    public void Source_inside_target_is_reported()
    {
        var plan = ValidPlan();
        plan.Target = _tmp.Root;

        Validate(plan).Should().Contain("Source must not be inside the target.");
    }
}
