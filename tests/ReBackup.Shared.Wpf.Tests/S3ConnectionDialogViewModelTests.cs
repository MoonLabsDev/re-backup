using FluentAssertions;
using ReBackup.Shared.Wpf.S3;
using ReBackup.Shared.Wpf.Tests.Fakes;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.Tests;

public class S3ConnectionDialogViewModelTests
{
    private static readonly S3Connection Stored = new("id-1", "Backups", "eu-central-1", "my-bucket", "AKIA1", "stored-secret");

    private static S3ConnectionDialogViewModel Create(S3Connection? existing = null, Func<string, bool>? isNameTaken = null) =>
        new(existing, isNameTaken ?? (_ => false), new S3ConnectionTester(_ => FakeS3Client.Create().Client));

    private static (S3ConnectionDialogViewModel Vm, FakeS3Client Fake, List<S3Connection> Tested) CreateWithFake(S3Connection? existing = null)
    {
        var (client, fake) = FakeS3Client.Create();
        var tested = new List<S3Connection>();
        var tester = new S3ConnectionTester(connection =>
        {
            tested.Add(connection);
            return client;
        });
        return (new S3ConnectionDialogViewModel(existing, _ => false, tester), fake, tested);
    }

    private static void FillValid(S3ConnectionDialogViewModel vm)
    {
        vm.Name = "Backups";
        vm.Region = "eu-central-1";
        vm.Bucket = "my-bucket";
        vm.AccessKeyId = "AKIA1";
        vm.Secret = "secret";
    }

    [Fact]
    public void A_new_connection_starts_with_every_required_field_missing()
    {
        var vm = Create();

        vm.Errors.Should().Equal("s3.error.nameRequired", "s3.error.regionRequired", "s3.error.bucketInvalid",
            "s3.error.accessKeyRequired", "s3.error.secretRequired");
        vm.SecretPlaceholderKey.Should().BeNull();
        vm.CheckWrite.Should().BeTrue();
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Filled_fields_are_valid()
    {
        var vm = Create();
        FillValid(vm);

        vm.Errors.Should().BeEmpty();
        vm.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Theory]
    [InlineData("My_Bucket", false)]
    [InlineData("ab", false)]
    [InlineData("-bucket", false)]
    [InlineData("bucket-", false)]
    [InlineData("my-bucket.2026", true)]
    [InlineData("abc", true)]
    public void The_bucket_follows_the_S3_naming_rules(string bucket, bool valid)
    {
        var vm = Create();
        FillValid(vm);

        vm.Bucket = bucket;

        if (valid) vm.Errors.Should().BeEmpty();
        else vm.Errors.Should().Equal("s3.error.bucketInvalid");
    }

    [Fact]
    public void A_bucket_of_64_characters_is_too_long()
    {
        var vm = Create();
        FillValid(vm);

        vm.Bucket = new string('a', 63);
        vm.Errors.Should().BeEmpty();
        vm.Bucket = new string('a', 64);
        vm.Errors.Should().Equal("s3.error.bucketInvalid");
    }

    [Fact]
    public void A_name_that_another_connection_has_is_taken()
    {
        var vm = Create(isNameTaken: name => name == "Other");
        FillValid(vm);

        vm.Name = "Other";

        vm.Errors.Should().Equal("s3.error.nameTaken");
        vm.NameHintKey.Should().Be("s3.error.nameTaken");
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void An_edited_connection_may_keep_its_own_name()
    {
        var vm = Create(Stored, isNameTaken: name => name == "Backups");

        vm.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Whitespace_only_fields_count_as_empty()
    {
        var vm = Create();
        FillValid(vm);

        vm.Name = "  ";
        vm.AccessKeyId = " ";
        vm.Secret = " ";
        vm.Region = " ";

        vm.Errors.Should().Equal("s3.error.nameRequired", "s3.error.regionRequired", "s3.error.accessKeyRequired",
            "s3.error.secretRequired");
    }

    [Fact]
    public void Editing_a_connection_with_a_secret_keeps_the_secret_when_the_field_stays_empty()
    {
        var vm = Create(Stored);

        vm.Name.Should().Be("Backups");
        vm.Region.Should().Be("eu-central-1");
        vm.Bucket.Should().Be("my-bucket");
        vm.AccessKeyId.Should().Be("AKIA1");
        vm.Secret.Should().BeEmpty();
        vm.SecretPlaceholderKey.Should().Be("s3.secret.unchanged");
        vm.Errors.Should().BeEmpty();

        vm.SaveCommand.Execute(null);

        vm.Result.Should().Be(Stored with { Secret = null });
    }

    [Fact]
    public void A_new_secret_replaces_the_stored_one()
    {
        var vm = Create(Stored);

        vm.Secret = "new-secret";
        vm.SaveCommand.Execute(null);

        vm.Result.Should().Be(Stored with { Secret = "new-secret" });
    }

    [Fact]
    public void A_connection_whose_secret_cannot_be_decrypted_needs_it_again()
    {
        var vm = Create(Stored with { Secret = null });

        vm.SecretPlaceholderKey.Should().Be("s3.secret.reenter");
        vm.Errors.Should().Equal("s3.error.secretRequired");

        vm.Secret = "again";
        vm.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Save_trims_the_fields_and_gives_a_new_connection_an_id()
    {
        var vm = Create();
        FillValid(vm);
        vm.Name = " Backups ";
        vm.Bucket = "my-bucket ";
        var saved = false;
        vm.Saved += (_, _) => saved = true;

        vm.SaveCommand.Execute(null);

        saved.Should().BeTrue();
        vm.Result!.Id.Should().NotBeNullOrWhiteSpace();
        vm.Result.Should().Be(new S3Connection(vm.Result.Id, "Backups", "eu-central-1", "my-bucket", "AKIA1", "secret"));
    }

    [Fact]
    public void Use_region_sets_the_region()
    {
        var vm = Create(Stored);

        vm.UseRegionCommand.Execute("eu-west-1");

        vm.Region.Should().Be("eu-west-1");
    }

    [Fact]
    public void The_region_list_holds_the_common_regions_once()
    {
        AwsRegions.All.Should().Contain(["us-east-1", "eu-central-1", "eu-west-1", "ap-southeast-2"]);
        AwsRegions.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Test_fills_the_results()
    {
        var (vm, fake, _) = CreateWithFake();
        FillValid(vm);
        fake.BucketRegion = "eu-west-1";

        await vm.TestCommand.ExecuteAsync(null);

        vm.IsTesting.Should().BeFalse();
        vm.Results.Select(r => (r.Check, r.State)).Should().Equal(
            (S3Check.Bucket, S3CheckState.Warning), (S3Check.List, S3CheckState.Ok),
            (S3Check.WriteDelete, S3CheckState.Ok), (S3Check.Lifecycle, S3CheckState.Ok));
        vm.Results[0].MessageKey.Should().Be(S3MessageKeys.RegionMismatch);
        vm.Results[0].Detail.Should().Be("eu-west-1");
    }

    [Fact]
    public async Task Without_check_write_the_write_check_is_skipped()
    {
        var (vm, _, _) = CreateWithFake();
        FillValid(vm);
        vm.CheckWrite = false;

        await vm.TestCommand.ExecuteAsync(null);

        vm.Results.Single(r => r.Check == S3Check.WriteDelete).State.Should().Be(S3CheckState.Skipped);
    }

    [Fact]
    public async Task An_unchanged_secret_is_tested_with_the_stored_one()
    {
        var (vm, _, tested) = CreateWithFake(Stored);

        await vm.TestCommand.ExecuteAsync(null);

        tested.Should().ContainSingle().Which.Secret.Should().Be("stored-secret");
        vm.Results.Should().HaveCount(4);
    }

    [Fact]
    public void Test_needs_the_bucket_fields_but_not_the_name()
    {
        var (vm, _, _) = CreateWithFake();
        FillValid(vm);

        vm.Name = "";
        vm.TestCommand.CanExecute(null).Should().BeTrue();
        vm.Secret = "";
        vm.TestCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_stops_the_test_and_save_waits_for_it()
    {
        var (vm, fake, _) = CreateWithFake();
        FillValid(vm);
        fake.Block = true;

        var running = vm.TestCommand.ExecuteAsync(null);
        await fake.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        vm.IsTesting.Should().BeTrue();
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.CancelTestCommand.CanExecute(null).Should().BeTrue();

        vm.CancelTestCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        vm.IsTesting.Should().BeFalse();
        vm.Results.Should().BeEmpty();
        vm.TestStatusKey.Should().Be("s3.test.cancelled");
        vm.SaveCommand.CanExecute(null).Should().BeTrue();
        vm.CancelTestCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Changing_a_tested_field_clears_the_old_results()
    {
        var (vm, _, _) = CreateWithFake();
        FillValid(vm);
        await vm.TestCommand.ExecuteAsync(null);

        vm.Bucket = "other-bucket";

        vm.Results.Should().BeEmpty();
    }
}
