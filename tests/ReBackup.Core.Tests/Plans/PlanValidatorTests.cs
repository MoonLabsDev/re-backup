using FluentAssertions;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Core.Schedule;
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

    /// <summary>The errors as Core renders them in English (the texts did not change, only their form did).</summary>
    private static IReadOnlyList<string> Validate(BackupPlan plan, params BackupPlan[] others) =>
        PlanValidator.Validate(plan, others.Append(plan)).Select(message => CoreTexts.English(message)).ToList();

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
    [InlineData("Docs.partial", "Name must not end with \".partial\".")]
    [InlineData("Docs.PARTIAL", "Name must not end with \".partial\".")]
    [InlineData("Docs.deleting", "Name must not end with \".deleting\".")]
    [InlineData("Docs.DELETING", "Name must not end with \".deleting\".")]
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

    [Fact]
    public void Invalid_retention_rules_are_reported_with_their_position()
    {
        var plan = ValidPlan();
        plan.Retention =
        [
            new RetentionRule { Period = RetentionPeriod.Daily, Keep = 7 },
            new RetentionRule { Period = RetentionPeriod.Weekly, Anchor = "Someday", Keep = 4 },
            new RetentionRule { Period = RetentionPeriod.Daily, Keep = 0 },
        ];

        Validate(plan).Should().Equal(
            "Retention rule 2: the anchor must be a weekday, for example Sunday.",
            "Retention rule 3: keep must be a number from 1 to 9999.");
    }

    [Fact]
    public void Invalid_triggers_are_reported_with_their_position()
    {
        var plan = ValidPlan();
        plan.Triggers =
        [
            new ScheduleTrigger { Type = TriggerType.Daily, Time = "02:00" },
            new ScheduleTrigger { Type = TriggerType.Weekly, Time = "18:00" },
        ];

        Validate(plan).Should().Equal("Trigger 2: choose at least one weekday.");
    }

    [Fact]
    public void NameErrors_checks_the_name_alone()
    {
        static IEnumerable<string> English(string? name) => PlanValidator.NameErrors(name).Select(m => CoreTexts.English(m));

        English("Projects").Should().BeEmpty();
        English("Projects.").Should().Equal("Name must not end with a dot.");
        English(" x ").Should().Equal("Name must not start or end with spaces.");
        English(null).Should().Equal("Name is required.");
    }


    [Fact]
    public void Errors_are_keys_with_arguments()
    {
        var plan = ValidPlan();
        var other = ValidPlan();
        other.Name = "PROJECTS";
        plan.Retention = [new RetentionRule { Period = RetentionPeriod.Daily, Keep = 0 }];
        plan.Triggers = [new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Mo"], Time = "18:00" }];

        PlanValidator.Validate(plan, [plan, other]).Should().Equal(
            Message.Of("core.plan.nameTaken", ("name", "Projects")),
            Message.Of("core.plan.retentionRule", ("index", 1), ("problem", Message.Of("core.retention.keep", ("max", 9999)))),
            Message.Of("core.plan.trigger", ("index", 1), ("problem", Message.Of("core.trigger.notWeekday", ("day", "Mo")))));
    }
}
