using FluentAssertions;
using ReBackup.Shared.Wpf.S3;
using ReBackup.Shared.Wpf.Tests.Fakes;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.Tests;

public class S3ConnectionDialogViewModelTests
{
    private static readonly S3Account Main = new("acc-1", "Main", "AKIA1", "stored-secret");
    private static readonly S3Account Spare = new("acc-2", "Spare", "AKIA2", "spare-secret");
    private static readonly S3ConnectionInfo Stored = new("id-1", "Backups", "eu-central-1", "my-bucket", "acc-1");

    private static S3ConnectionDialogViewModel Create(S3ConnectionInfo? existing = null, Func<string, bool>? isNameTaken = null,
        IReadOnlyList<S3Account>? accounts = null, Func<S3Account?>? createAccount = null) =>
        new(existing, isNameTaken ?? (_ => false), new S3ConnectionTester(_ => FakeS3Client.Create().Client),
            () => accounts ?? [Main, Spare], createAccount ?? (() => null));

    private static (S3ConnectionDialogViewModel Vm, FakeS3Client Fake, List<S3Connection> Tested) CreateWithFake(
        S3ConnectionInfo? existing = null, IReadOnlyList<S3Account>? accounts = null)
    {
        var (client, fake) = FakeS3Client.Create();
        var tested = new List<S3Connection>();
        var tester = new S3ConnectionTester(connection =>
        {
            tested.Add(connection);
            return client;
        });
        return (new S3ConnectionDialogViewModel(existing, _ => false, tester, () => accounts ?? [Main, Spare], () => null), fake, tested);
    }

    private static void FillValid(S3ConnectionDialogViewModel vm)
    {
        vm.Name = "Backups";
        vm.Region = "eu-central-1";
        vm.Bucket = "my-bucket";
        vm.SelectedAccount = vm.Accounts.Single(a => a.Id == Main.Id);
    }

    [Fact]
    public void A_new_connection_starts_with_every_required_field_missing()
    {
        var vm = Create();

        vm.Errors.Should().Equal("s3.error.nameRequired", "s3.error.regionRequired", "s3.error.bucketInvalid",
            "s3.error.accountRequired");
        vm.Accounts.Should().Equal(Main, Spare);
        vm.SelectedAccount.Should().BeNull();
        vm.CheckWrite.Should().BeTrue();
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void A_new_connection_with_one_account_preselects_it()
    {
        var vm = Create(accounts: [Main]);

        vm.SelectedAccount.Should().Be(Main);
        vm.Errors.Should().NotContain("s3.error.accountRequired");
        vm.AccountHintKey.Should().BeNull();

        vm.Name = "Backups";
        vm.Region = "eu-central-1";
        vm.Bucket = "my-bucket";

        vm.SaveCommand.CanExecute(null).Should().BeTrue();
        vm.SaveCommand.Execute(null);
        vm.Result!.AccountId.Should().Be("acc-1");
    }

    [Fact]
    public void A_new_connection_with_two_accounts_preselects_none()
    {
        var vm = Create(accounts: [Main, Spare]);

        vm.SelectedAccount.Should().BeNull();
        vm.Errors.Should().Contain("s3.error.accountRequired");
    }

    [Fact]
    public void An_edited_connection_whose_account_is_missing_does_not_take_the_only_account()
    {
        var vm = Create(Stored with { AccountId = "gone" }, accounts: [Spare]);

        vm.SelectedAccount.Should().BeNull("the user must choose consciously");
        vm.AccountHintKey.Should().Be("s3.error.accountRequired");
    }

    [Fact]
    public void Filled_fields_are_valid()
    {
        var vm = Create();
        FillValid(vm);

        vm.Errors.Should().BeEmpty();
        vm.AccountHintKey.Should().BeNull();
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
        vm.Region = " ";

        vm.Errors.Should().Equal("s3.error.nameRequired", "s3.error.regionRequired");
    }

    [Theory]
    [InlineData("eu-central")]
    [InlineData("EU-CENTRAL-1")]
    [InlineData("frankfurt")]
    public void A_typed_region_that_is_no_region_name_blocks_save_and_test(string region)
    {
        var vm = Create();
        FillValid(vm);
        vm.RegionHintKey.Should().BeNull();

        vm.Region = region;

        vm.Errors.Should().Equal("s3.error.regionInvalid");
        vm.RegionHintKey.Should().Be("s3.error.regionInvalid");
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.TestCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void A_region_with_surrounding_blanks_is_trimmed_and_valid()
    {
        var vm = Create();
        FillValid(vm);

        vm.Region = " eu-west-1 ";

        vm.Errors.Should().BeEmpty();
        vm.SaveCommand.Execute(null);
        vm.Result!.Region.Should().Be("eu-west-1");
    }

    [Fact]
    public void The_region_hint_is_raised_with_the_region()
    {
        var vm = Create();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Region = "x";

        raised.Should().Contain(nameof(vm.RegionHintKey));
    }

    [Fact]
    public void Every_offered_region_is_valid() =>
        AwsRegions.All.Should().OnlyContain(region => S3Regions.IsValid(region));

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
        vm.Result.Should().Be(new S3ConnectionInfo(vm.Result.Id, "Backups", "eu-central-1", "my-bucket", "acc-1"));
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
            (S3Check.Bucket, S3CheckState.Warning), (S3Check.Versioning, S3CheckState.Ok), (S3Check.List, S3CheckState.Ok),
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
    public void Test_needs_the_bucket_fields_but_not_the_name()
    {
        var (vm, _, _) = CreateWithFake();
        FillValid(vm);

        vm.Name = "";
        vm.TestCommand.CanExecute(null).Should().BeTrue();
        vm.SelectedAccount = null;
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

    [Fact]
    public void Editing_selects_the_connection_account()
    {
        var vm = Create(Stored with { AccountId = "ACC-1" });

        vm.Name.Should().Be("Backups");
        vm.Region.Should().Be("eu-central-1");
        vm.Bucket.Should().Be("my-bucket");
        vm.SelectedAccount.Should().BeSameAs(vm.Accounts[0]);
        vm.Errors.Should().BeEmpty();

        vm.SaveCommand.Execute(null);

        vm.Result.Should().Be(Stored, "the account id is the one of the chosen account");
    }

    [Fact]
    public void Choosing_another_account_saves_its_id()
    {
        var vm = Create(Stored);

        vm.SelectedAccount = vm.Accounts[1];
        vm.SaveCommand.Execute(null);

        vm.Result.Should().Be(Stored with { AccountId = "acc-2" });
    }

    [Fact]
    public void Missing_account_requires_choosing_one()
    {
        // The connection's account was deleted elsewhere (or the list is from another file state).
        var vm = Create(Stored with { AccountId = "gone" });

        vm.SelectedAccount.Should().BeNull();
        vm.Errors.Should().Equal("s3.error.accountRequired");
        vm.AccountHintKey.Should().Be("s3.error.accountRequired");
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.TestCommand.CanExecute(null).Should().BeFalse();

        vm.SelectedAccount = vm.Accounts[1];

        vm.Errors.Should().BeEmpty();
        vm.AccountHintKey.Should().BeNull();
    }

    [Fact]
    public void Account_ids_and_connection_ids_are_separate()
    {
        // After a migration an account can have the id of a connection: only the connection's AccountId selects it.
        var accounts = new[] { new S3Account("id-1", "Same id", "AKIA9", "s"), Main };

        var vm = Create(Stored, accounts: accounts);

        vm.SelectedAccount!.Name.Should().Be("Main");
    }

    [Fact]
    public void New_account_selects_the_created_account()
    {
        var created = new S3Account("acc-3", "Third", "AKIA3", "s3");
        var accounts = new List<S3Account> { Main, Spare };
        var vm = new S3ConnectionDialogViewModel(null, _ => false, new S3ConnectionTester(_ => FakeS3Client.Create().Client),
            () => accounts.ToList(), () =>
            {
                accounts.Add(created);
                return created;
            });
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.NewAccountCommand.Execute(null);

        vm.Accounts.Select(a => a.Id).Should().Equal("acc-1", "acc-2", "acc-3");
        vm.SelectedAccount.Should().Be(created);
        raised.Should().Contain(nameof(vm.SelectedAccount));
        vm.Errors.Should().NotContain("s3.error.accountRequired");
    }

    [Fact]
    public void A_cancelled_new_account_keeps_the_selection()
    {
        var vm = Create(Stored);

        vm.NewAccountCommand.Execute(null);

        vm.SelectedAccount.Should().Be(Main);
        vm.Accounts.Should().HaveCount(2);
    }

    [Fact]
    public void An_account_that_needs_its_secret_disables_the_test()
    {
        var locked = Main with { Secret = null };
        var vm = Create(Stored, accounts: [locked]);

        vm.SelectedAccount.Should().Be(locked);
        vm.AccountHintKey.Should().Be("s3.account.needsSecret");
        vm.TestCommand.CanExecute(null).Should().BeFalse();
        vm.Errors.Should().BeEmpty("the connection itself can still be saved");
        vm.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Test_resolves_the_chosen_account()
    {
        var (vm, _, tested) = CreateWithFake(Stored);
        vm.SelectedAccount = vm.Accounts[1];
        vm.Bucket = "other-bucket";

        await vm.TestCommand.ExecuteAsync(null);

        tested.Should().ContainSingle().Which.Should().Be(
            new S3Connection("id-1", "Backups", "eu-central-1", "other-bucket", "AKIA2", "spare-secret"));
        vm.Results.Should().HaveCount(5);
    }

    [Fact]
    public async Task Choosing_another_account_clears_the_old_results()
    {
        var (vm, _, _) = CreateWithFake(Stored);
        await vm.TestCommand.ExecuteAsync(null);
        vm.Results.Should().NotBeEmpty();

        vm.SelectedAccount = vm.Accounts[1];

        vm.Results.Should().BeEmpty();
    }
}
