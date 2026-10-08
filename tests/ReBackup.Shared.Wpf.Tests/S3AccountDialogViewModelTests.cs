using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using FluentAssertions;
using ReBackup.Shared.Wpf.S3;
using ReBackup.Shared.Wpf.Tests.Fakes;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.Tests;

public class S3AccountDialogViewModelTests
{
    private static readonly S3Account Stored = new("acc-1", "Main", "AKIA1", "stored-secret");

    private static (S3AccountDialogViewModel Vm, FakeS3Client Fake, List<S3Connection> Tested) Create(
        S3Account? existing = null, Func<string, bool>? isNameTaken = null)
    {
        var (client, fake) = FakeS3Client.Create();
        var tested = new List<S3Connection>();
        var tester = new S3ConnectionTester(connection =>
        {
            tested.Add(connection);
            return client;
        });
        return (new S3AccountDialogViewModel(existing, isNameTaken ?? (_ => false), tester), fake, tested);
    }

    private static void FillValid(S3AccountDialogViewModel vm)
    {
        vm.Name = "Main";
        vm.AccessKeyId = "AKIA1";
        vm.Secret = "secret";
    }

    [Fact]
    public void A_new_account_starts_with_every_required_field_missing()
    {
        var (vm, _, _) = Create();

        vm.IsNew.Should().BeTrue();
        vm.Errors.Should().Equal("s3.error.nameRequired", "s3.error.accessKeyRequired", "s3.error.secretRequired");
        vm.SecretPlaceholderKey.Should().BeNull();
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.TestCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Whitespace_only_fields_count_as_empty()
    {
        var (vm, _, _) = Create();
        vm.Name = " ";
        vm.AccessKeyId = " ";
        vm.Secret = " ";

        vm.Errors.Should().Equal("s3.error.nameRequired", "s3.error.accessKeyRequired", "s3.error.secretRequired");
    }

    [Fact]
    public void A_name_that_another_account_has_is_taken()
    {
        var (vm, _, _) = Create(isNameTaken: name => name == "Other");
        FillValid(vm);

        vm.Name = "Other";

        vm.Errors.Should().Equal("s3.error.nameTaken");
        vm.NameHintKey.Should().Be("s3.error.nameTaken");
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.TestCommand.CanExecute(null).Should().BeTrue("the test does not need the name");
    }

    [Fact]
    public void An_edited_account_may_keep_its_own_name()
    {
        var (vm, _, _) = Create(Stored, isNameTaken: name => name == "Main");

        vm.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Save_trims_the_fields_and_gives_a_new_account_an_id()
    {
        var (vm, _, _) = Create();
        FillValid(vm);
        vm.Name = " Main ";
        vm.AccessKeyId = " AKIA1 ";
        var saved = false;
        vm.Saved += (_, _) => saved = true;

        vm.SaveCommand.Execute(null);

        saved.Should().BeTrue();
        vm.Result!.Id.Should().NotBeNullOrWhiteSpace();
        vm.Result.Should().Be(new S3Account(vm.Result.Id, "Main", "AKIA1", "secret"));
    }

    [Fact]
    public void Editing_keeps_the_stored_secret_when_the_field_stays_empty()
    {
        var (vm, _, _) = Create(Stored);

        vm.IsNew.Should().BeFalse();
        vm.Name.Should().Be("Main");
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
        var (vm, _, _) = Create(Stored);

        vm.Secret = "new-secret";
        vm.SaveCommand.Execute(null);

        vm.Result.Should().Be(Stored with { Secret = "new-secret" });
    }

    [Fact]
    public void Has_secret_input_follows_the_secret_field_and_is_raised()
    {
        var (vm, _, _) = Create(Stored);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.HasSecretInput.Should().BeFalse();
        vm.Secret = "typed";
        vm.HasSecretInput.Should().BeTrue();
        raised.Should().Contain(nameof(vm.HasSecretInput));
    }

    [Fact]
    public void An_account_whose_secret_cannot_be_decrypted_needs_it_again()
    {
        var (vm, _, _) = Create(Stored with { Secret = null });

        vm.SecretPlaceholderKey.Should().Be("s3.secret.reenter");
        vm.Errors.Should().Equal("s3.error.secretRequired");
        vm.TestCommand.CanExecute(null).Should().BeFalse();

        vm.Secret = "again";
        vm.Errors.Should().BeEmpty();
        vm.SaveCommand.Execute(null);
        vm.Result.Should().Be(Stored with { Secret = "again" });
    }

    [Fact]
    public void A_changed_access_key_needs_its_secret()
    {
        var (vm, _, _) = Create(Stored);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.AccessKeyId = "AKIA2";

        vm.Errors.Should().Equal("s3.error.secretRequired");
        vm.SecretPlaceholderKey.Should().BeNull("the stored secret belongs to the old key");
        raised.Should().Contain(nameof(vm.SecretPlaceholderKey));
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.TestCommand.CanExecute(null).Should().BeFalse();

        vm.AccessKeyId = " AKIA1 ";
        vm.Errors.Should().BeEmpty("the old key keeps the stored secret again");

        vm.AccessKeyId = "AKIA2";
        vm.Secret = "secret-2";
        vm.SaveCommand.Execute(null);
        vm.Result.Should().Be(Stored with { AccessKeyId = "AKIA2", Secret = "secret-2" });
    }

    [Fact]
    public async Task Test_with_an_unchanged_secret_uses_the_stored_one()
    {
        var (vm, _, tested) = Create(Stored);

        await vm.TestCommand.ExecuteAsync(null);

        tested.Should().ContainSingle().Which.Should().Match<S3Connection>(c => c.AccessKeyId == "AKIA1" && c.Secret == "stored-secret");
        vm.TestResult.Should().Be(new S3CheckResult(S3Check.Account, S3CheckState.Ok, S3MessageKeys.Ok, null));
        vm.IsTesting.Should().BeFalse();
    }

    [Fact]
    public async Task Test_uses_the_typed_fields()
    {
        var (vm, _, tested) = Create(Stored);
        vm.AccessKeyId = "AKIA2";
        vm.Secret = " typed ";

        await vm.TestCommand.ExecuteAsync(null);

        tested.Should().ContainSingle().Which.Should().Match<S3Connection>(c => c.AccessKeyId == "AKIA2" && c.Secret == "typed");
    }

    [Fact]
    public async Task A_key_that_may_not_list_buckets_is_a_warning()
    {
        var (vm, fake, _) = Create(Stored);
        fake.ListBucketsError = new AmazonS3Exception("denied", null, ErrorType.Sender, "AccessDenied", "req", HttpStatusCode.Forbidden);

        await vm.TestCommand.ExecuteAsync(null);

        vm.TestResult!.State.Should().Be(S3CheckState.Warning);
        vm.TestResult.MessageKey.Should().Be(S3MessageKeys.AccountNoList);
        vm.SaveCommand.CanExecute(null).Should().BeTrue("a warning does not block saving");
    }

    [Fact]
    public async Task A_wrong_key_fails()
    {
        var (vm, fake, _) = Create(Stored);
        fake.ListBucketsError = new AmazonS3Exception("bad", null, ErrorType.Sender, "InvalidAccessKeyId", "req", HttpStatusCode.Forbidden);

        await vm.TestCommand.ExecuteAsync(null);

        vm.TestResult!.State.Should().Be(S3CheckState.Failed);
        vm.TestResult.MessageKey.Should().Be(S3MessageKeys.AccessDenied);
    }

    [Fact]
    public async Task Changing_a_tested_field_clears_the_result()
    {
        var (vm, _, _) = Create(Stored);
        await vm.TestCommand.ExecuteAsync(null);
        vm.TestResult.Should().NotBeNull();

        vm.Secret = "other";

        vm.TestResult.Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_test_says_so()
    {
        var (vm, _, _) = Create(Stored);
        // The fake answers a cancelled token with cancellation; cancel as soon as the test has started.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsTesting) && vm.IsTesting)
            {
                vm.SaveCommand.CanExecute(null).Should().BeFalse();
                vm.CancelTestCommand.Execute(null);
            }
        };

        await vm.TestCommand.ExecuteAsync(null);

        vm.IsTesting.Should().BeFalse();
        vm.TestResult.Should().BeNull();
        vm.TestStatusKey.Should().Be("s3.test.cancelled");
    }
}
