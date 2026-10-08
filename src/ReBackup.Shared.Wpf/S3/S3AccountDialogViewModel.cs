using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.S3;

/// <summary>
/// The logic of <see cref="S3AccountDialog"/>: name, access key, secret, validation, the optional key test and the result.
/// Holds the secret in memory only and never writes it anywhere; testable without a window.
/// </summary>
public sealed partial class S3AccountDialogViewModel : ObservableObject
{
    public const string NameRequiredKey = "s3.error.nameRequired";
    public const string NameTakenKey = "s3.error.nameTaken";
    public const string AccessKeyRequiredKey = "s3.error.accessKeyRequired";
    public const string SecretRequiredKey = "s3.error.secretRequired";
    public const string SecretUnchangedKey = "s3.secret.unchanged";
    public const string SecretReenterKey = "s3.secret.reenter";
    public const string TestCancelledKey = "s3.test.cancelled";
    public const string TestErrorKey = "s3.test.error";

    private readonly S3Account? _existing;
    private readonly Func<string, bool> _isNameTaken;
    private readonly S3ConnectionTester _tester;
    private CancellationTokenSource? _testCts;

    /// <param name="existing">The account to edit; <c>null</c> for a new one.</param>
    /// <param name="isNameTaken">Whether another account already has the name (the unchanged name of <paramref name="existing"/> is always allowed).</param>
    /// <param name="tester">Runs "Test access key".</param>
    public S3AccountDialogViewModel(S3Account? existing, Func<string, bool> isNameTaken, S3ConnectionTester tester)
    {
        ArgumentNullException.ThrowIfNull(isNameTaken);
        ArgumentNullException.ThrowIfNull(tester);
        _existing = existing;
        _isNameTaken = isNameTaken;
        _tester = tester;
        if (existing is not null)
        {
            _name = existing.Name;
            _accessKeyId = existing.AccessKeyId;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Errors), nameof(NameHintKey))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Errors), nameof(SecretPlaceholderKey))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _accessKeyId = "";

    /// <summary>The secret as typed (pushed from the PasswordBox); empty with <see cref="SecretUnchangedKey"/> keeps the stored one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Errors), nameof(HasSecretInput))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _secret = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand), nameof(CancelTestCommand))]
    private bool _isTesting;

    /// <summary>The result of the last test; cleared when a tested field changes.</summary>
    [ObservableProperty]
    private S3CheckResult? _testResult;

    /// <summary>How the last test ended when it has no result: <see cref="TestCancelledKey"/>, <see cref="TestErrorKey"/> or <c>null</c>.</summary>
    [ObservableProperty]
    private string? _testStatusKey;

    /// <summary>Something is typed in the secret field (hides its placeholder; the dialog binds this, never the secret).</summary>
    public bool HasSecretInput => Secret.Length > 0;

    /// <summary>True for a new account (the dialog's title).</summary>
    public bool IsNew => _existing is null;

    /// <summary>The fields can be changed: not while a test runs.</summary>
    public bool IsEditable => !IsTesting;

    /// <summary>
    /// The hint in the empty secret field: <see cref="SecretUnchangedKey"/>, <see cref="SecretReenterKey"/> (stored secret lost) or
    /// <c>null</c> (new account, or the access key was changed: the stored secret belongs to the old one).
    /// </summary>
    public string? SecretPlaceholderKey =>
        _existing is null || !KeepsAccessKey ? null : _existing.NeedsSecret ? SecretReenterKey : SecretUnchangedKey;

    /// <summary>The account as saved; <c>Secret</c> is <c>null</c> when the stored secret stays unchanged. Set by <see cref="SaveCommand"/>.</summary>
    public S3Account? Result { get; private set; }

    /// <summary>Raised after <see cref="SaveCommand"/> set <see cref="Result"/>; the dialog closes.</summary>
    public event EventHandler? Saved;

    /// <summary>The label keys of the current validation errors, in field order.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            var errors = new List<string>();
            var name = Name.Trim();
            if (name.Length == 0) errors.Add(NameRequiredKey);
            else if (!IsOwnName(name) && _isNameTaken(name)) errors.Add(NameTakenKey);
            if (AccessKeyId.Trim().Length == 0) errors.Add(AccessKeyRequiredKey);
            if (SecretRequired && Secret.Trim().Length == 0) errors.Add(SecretRequiredKey);
            return errors;
        }
    }

    /// <summary>The error shown below the name: only "taken" (a missing name just keeps Save disabled).</summary>
    public string? NameHintKey => Errors.Contains(NameTakenKey) ? NameTakenKey : null;

    /// <summary>An empty secret keeps the stored one only for an existing account that has one and still uses its access key.</summary>
    private bool SecretRequired => _existing is null || _existing.NeedsSecret || !KeepsAccessKey;

    private bool KeepsAccessKey => _existing is not null && string.Equals(AccessKeyId.Trim(), _existing.AccessKeyId, StringComparison.Ordinal);

    private bool IsOwnName(string name) => _existing is not null && string.Equals(name, _existing.Name, StringComparison.OrdinalIgnoreCase);

    private bool CanSave() => !IsTesting && Errors.Count == 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var secret = Secret.Trim();
        Result = new S3Account(_existing?.Id ?? Guid.NewGuid().ToString("N"), Name.Trim(), AccessKeyId.Trim(),
            secret.Length > 0 ? secret : null);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The test needs the key and a secret, not the name.</summary>
    private bool CanTest() => !IsTesting && Errors.All(key => key is NameRequiredKey or NameTakenKey);

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        using var cts = new CancellationTokenSource();
        _testCts = cts;
        TestResult = null;
        TestStatusKey = null;
        IsTesting = true;
        try
        {
            TestResult = await _tester.RunAccountAsync(AccountToTest(), cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            TestStatusKey = TestCancelledKey;
        }
        catch (Exception)
        {
            // The tester classifies the SDK's errors itself; whatever is left is reported without its message (it may hold request details).
            TestStatusKey = TestErrorKey;
        }
        finally
        {
            _testCts = null;
            IsTesting = false;
        }
    }

    /// <summary>The fields as an account; an empty secret means the stored one (only reached when <see cref="SecretRequired"/> is false).</summary>
    private S3Account AccountToTest()
    {
        var secret = Secret.Trim();
        return new S3Account(_existing?.Id ?? "", Name.Trim(), AccessKeyId.Trim(),
            secret.Length > 0 ? secret : SecretRequired ? null : _existing?.Secret);
    }

    [RelayCommand(CanExecute = nameof(IsTesting))]
    private void CancelTest() => _testCts?.Cancel();

    partial void OnAccessKeyIdChanged(string value) => ClearResult();

    partial void OnSecretChanged(string value) => ClearResult();

    private void ClearResult()
    {
        if (IsTesting) return;
        TestResult = null;
        TestStatusKey = null;
    }
}
