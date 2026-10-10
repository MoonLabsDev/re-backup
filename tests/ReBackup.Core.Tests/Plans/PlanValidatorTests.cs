using System.Globalization;
using FluentAssertions;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Tests.Plans;

public class PlanValidatorTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private BackupPlan ValidPlan() => new()
    {
        Name = "Projects",
        Source = StorageLocation.FileSystem(_tmp.CreateDir("src")),
        Target = StorageLocation.FileSystem(_tmp.PathOf("dst")),
    };

    /// <summary>
    /// A message in English: its own text from Core, the problems nested in it (retention rule, trigger) from the
    /// shared library.
    /// </summary>
    private static string English(Message message) =>
        LabelFormat.Format(CoreTexts.Templates.TryGetValue(message.Key, out var template)
                ? template
                : SharedTexts.Templates[message.Key],
            message.Args, CultureInfo.InvariantCulture, English);

    /// <summary>The errors as Core renders them in English (the texts did not change, only their form did).</summary>
    private static IReadOnlyList<string> Validate(BackupPlan plan, params BackupPlan[] others) =>
        PlanValidator.Validate(plan, others.Append(plan)).Select(English).ToList();

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
        plan.Source = StorageLocation.FileSystem("");
        plan.Target = StorageLocation.FileSystem("");

        Validate(plan).Should().Contain(["Source folder is required.", "Target folder is required."]);
    }

    [Fact]
    public void Relative_paths_are_reported()
    {
        var plan = ValidPlan();
        plan.Source = StorageLocation.FileSystem("relative");
        plan.Target = StorageLocation.FileSystem(@"also\relative");

        Validate(plan).Should().Contain(["Source must be an absolute path.", "Target must be an absolute path."]);
    }

    [Fact]
    public void Nonexistent_source_is_reported()
    {
        var plan = ValidPlan();
        plan.Source = StorageLocation.FileSystem(_tmp.PathOf("missing"));

        Validate(plan).Should().Contain("Source folder does not exist.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unknown_kind_is_rejected_by_validator(bool onSource)
    {
        var plan = ValidPlan();
        // S3 has rules of its own (below); a kind nothing knows stays unsupported.
        var other = new StorageLocation("ftp", "bucket/prefix", "conn");
        if (onSource)
            plan.Source = other;
        else
            plan.Target = other;

        Validate(plan).Should().Equal("This kind of location is not supported yet.");
    }

    // ---- S3 locations ----

    private static readonly Dictionary<string, ConnectionBucket> Buckets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c1"] = new ConnectionBucket("photos", "eu-central-1"),
        ["c1-copy"] = new ConnectionBucket("photos", "eu-central-1"),   // another connection on the same bucket
        ["c2"] = new ConnectionBucket("archive", "eu-central-1"),
        ["c3"] = new ConnectionBucket("photos", "us-east-1"),   // the same bucket, configured with another region
    };

    private static ConnectionBucket? Lookup(string id) => Buckets.GetValueOrDefault(id);

    private static IReadOnlyList<string> ValidateS3(BackupPlan plan, Func<string, ConnectionBucket?>? lookup = null) =>
        PlanValidator.Validate(plan, [plan], lookup ?? Lookup).Select(English).ToList();

    private static StorageLocation S3(string prefix, string? connectionId) => new("s3", prefix, connectionId);

    [Theory]
    [InlineData("")]
    [InlineData("backups")]
    [InlineData("backups/moonlabs")]
    public void S3_source_and_target_with_an_existing_connection_and_a_valid_prefix_are_valid(string prefix)
    {
        var plan = ValidPlan();
        plan.Target = S3(prefix, "c1");
        ValidateS3(plan).Should().BeEmpty();

        plan = ValidPlan();
        plan.Source = S3(prefix, "c2");
        ValidateS3(plan).Should().BeEmpty("an S3 source is not looked at (no network in validation)");
    }

    [Fact]
    public void S3_location_without_a_connection_is_reported()
    {
        var plan = ValidPlan();
        plan.Source = S3("a", null);
        plan.Target = S3("b", "  ");

        ValidateS3(plan).Should().Equal("Choose an S3 connection for the source.", "Choose an S3 connection for the target.");
    }

    [Fact]
    public void S3_location_whose_connection_no_longer_exists_is_reported()
    {
        var plan = ValidPlan();
        plan.Source = S3("a", "gone");
        plan.Target = S3("b", "gone-too");

        ValidateS3(plan).Should().Equal("The source's S3 connection no longer exists.", "The target's S3 connection no longer exists.");
    }

    [Fact]
    public void Without_a_lookup_every_S3_connection_is_missing()
    {
        var plan = ValidPlan();
        plan.Target = S3("b", "c1");

        PlanValidator.Validate(plan, [plan]).Select(English).Should().Equal("The target's S3 connection no longer exists.");
    }

    [Theory]
    [InlineData("/backups")]
    [InlineData("backups/")]
    [InlineData("a//b")]
    [InlineData(@"a\b")]
    [InlineData("a/../b")]
    [InlineData(".")]
    public void Invalid_S3_prefix_is_reported(string prefix)
    {
        var plan = ValidPlan();
        plan.Source = S3(prefix, "c2");
        plan.Target = S3(prefix, "c1");

        ValidateS3(plan).Should().Equal(
            "The source prefix is not valid: separate folder names with a single /, with no / at the start or end and no \\, . or .. parts.",
            "The target prefix is not valid: separate folder names with a single /, with no / at the start or end and no \\, . or .. parts.");
    }

    [Fact]
    public void Unreadable_connections_are_reported_once_with_the_reason()
    {
        var plan = ValidPlan();
        plan.Source = S3("a", "c1");
        plan.Target = S3("b", "c2");

        ValidateS3(plan, _ => throw new StorageIOException("connections.json", "connections.json could not be read: corrupt"))
            .Should().Equal("The S3 connections could not be read: connections.json could not be read: corrupt");
    }

    // Review Focus 4: a plan whose S3 target lies inside its S3 source never validates, so it never runs.
    [Theory]
    [InlineData("c1", "", "c1", "backups")]
    [InlineData("c1", "data", "c1", "data")]
    [InlineData("c1", "data", "c1", "data/backups")]
    [InlineData("c1", "data", "c1-copy", "data/backups/x")]
    [InlineData("c1", "data", "C1", "data/b")]
    [InlineData("c1", "data", "c3", "data/backups")]  // bucket names are global: the region does not make it another bucket
    public void S3_target_inside_its_S3_source_on_the_same_bucket_is_reported(string sourceId, string sourcePrefix,
        string targetId, string targetPrefix)
    {
        var plan = ValidPlan();
        plan.Source = S3(sourcePrefix, sourceId);
        plan.Target = S3(targetPrefix, targetId);

        ValidateS3(plan).Should().Equal("Target must not be inside the source.");
    }

    [Theory]
    [InlineData("c1", "data/photos", "c1", "data")]
    [InlineData("c1", "data/photos", "c1-copy", "")]
    public void S3_source_inside_its_S3_target_on_the_same_bucket_is_reported(string sourceId, string sourcePrefix,
        string targetId, string targetPrefix)
    {
        var plan = ValidPlan();
        plan.Source = S3(sourcePrefix, sourceId);
        plan.Target = S3(targetPrefix, targetId);

        ValidateS3(plan).Should().Equal("Source must not be inside the target.");
    }

    [Theory]
    [InlineData("c1", "data", "c2", "data")]          // another bucket
    [InlineData("c1", "data", "c1", "database")]      // a sibling whose name starts alike
    [InlineData("c1", "data/a", "c1", "data/b")]
    [InlineData("c1", "Data", "c1", "data/backups")]  // S3 keys are case-sensitive
    [InlineData("c1", "data", "c1", "Data")]
    public void S3_source_and_target_apart_are_valid(string sourceId, string sourcePrefix, string targetId, string targetPrefix)
    {
        var plan = ValidPlan();
        plan.Source = S3(sourcePrefix, sourceId);
        plan.Target = S3(targetPrefix, targetId);

        ValidateS3(plan).Should().BeEmpty();
    }

    [Fact]
    public void Folder_and_S3_never_overlap()
    {
        var plan = ValidPlan();
        plan.Target = S3("", "c1");
        ValidateS3(plan).Should().BeEmpty();

        plan = ValidPlan();
        plan.Source = S3("", "c1");
        plan.Target = StorageLocation.FileSystem(_tmp.PathOf("dst"));
        ValidateS3(plan).Should().BeEmpty();
    }

    [Fact]
    public void Target_inside_source_is_reported()
    {
        var plan = ValidPlan();
        plan.Target = StorageLocation.FileSystem(Path.Combine(plan.Source.Path, "backups"));

        Validate(plan).Should().Contain("Target must not be inside the source.");
    }

    [Fact]
    public void Source_inside_target_is_reported()
    {
        var plan = ValidPlan();
        plan.Target = StorageLocation.FileSystem(_tmp.Root);

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
            Message.Of("core.plan.retentionRule", ("index", 1), ("problem", Message.Of("shared.retention.keep", ("max", 9999)))),
            Message.Of("core.plan.trigger", ("index", 1), ("problem", Message.Of("shared.trigger.notWeekday", ("day", "Mo")))));
    }
}
