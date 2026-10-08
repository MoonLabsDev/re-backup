using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.S3;

/// <summary>
/// The logic of <see cref="S3ConnectionDialog"/>: name, region, bucket, the account whose key opens the bucket, validation,
/// the optional connection test and the result. The secret stays in the chosen <see cref="S3Account"/> (memory only);
/// testable without a window.
/// </summary>
public sealed partial class S3ConnectionDialogViewModel : ObservableObject
{
    public const string NameRequiredKey = "s3.error.nameRequired";
    public const string NameTakenKey = "s3.error.nameTaken";
    public const string RegionRequiredKey = "s3.error.regionRequired";
    public const string RegionInvalidKey = "s3.error.regionInvalid";
    public const string BucketInvalidKey = "s3.error.bucketInvalid";
    public const string AccountRequiredKey = "s3.error.accountRequired";
    public const string AccountNeedsSecretKey = "s3.account.needsSecret";
    public const string TestCancelledKey = "s3.test.cancelled";
    public const string TestErrorKey = "s3.test.error";

    /// <summary>S3 bucket naming: 3–63 characters, lowercase letters, digits, dots and hyphens, starting and ending with a letter or digit.</summary>
    private static readonly Regex BucketPattern = new("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant);

    private readonly S3ConnectionInfo? _existing;
    private readonly Func<string, bool> _isNameTaken;
    private readonly S3ConnectionTester _tester;
    private readonly Func<IReadOnlyList<S3Account>> _accounts;
    private readonly Func<S3Account?> _createAccount;
    private CancellationTokenSource? _testCts;

    /// <param name="existing">The connection to edit; <c>null</c> for a new one.</param>
    /// <param name="isNameTaken">Whether another of the app's connections already has the name (the unchanged name of <paramref name="existing"/> is always allowed).</param>
    /// <param name="tester">Runs "Test connection".</param>
    /// <param name="accounts">The stored accounts; read again after "New account…".</param>
    /// <param name="createAccount">"New account…": opens the account dialog and stores the account; the stored account, or <c>null</c> when cancelled.</param>
    public S3ConnectionDialogViewModel(S3ConnectionInfo? existing, Func<string, bool> isNameTaken, S3ConnectionTester tester,
        Func<IReadOnlyList<S3Account>> accounts, Func<S3Account?> createAccount)
    {
        ArgumentNullException.ThrowIfNull(isNameTaken);
        ArgumentNullException.ThrowIfNull(tester);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(createAccount);
        _existing = existing;
        _isNameTaken = isNameTaken;
        _tester = tester;
        _accounts = accounts;
        _createAccount = createAccount;
        foreach (var account in accounts())
            Accounts.Add(account);
        if (existing is not null)
        {
            _name = existing.Name;
            _region = existing.Region;
            _bucket = existing.Bucket;
            // Looked up among the accounts only (a connection id may equal an account id after a migration); a missing account selects nothing.
            _selectedAccount = Accounts.FirstOrDefault(a => SameId(a.Id, existing.AccountId));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Errors), nameof(NameHintKey))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Errors), nameof(RegionHintKey))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _region = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Errors), nameof(BucketHintKey))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _bucket = "";

    /// <summary>The account whose key opens the bucket; <c>null</c> until one is chosen, or when the stored one no longer exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Errors), nameof(AccountHintKey))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private S3Account? _selectedAccount;

    /// <summary>Whether the test writes and deletes a test object.</summary>
    [ObservableProperty]
    private bool _checkWrite = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand), nameof(CancelTestCommand), nameof(NewAccountCommand))]
    private bool _isTesting;

    /// <summary>How the last test ended when it has no results: <see cref="TestCancelledKey"/>, <see cref="TestErrorKey"/> or <c>null</c>.</summary>
    [ObservableProperty]
    private string? _testStatusKey;

    /// <summary>The accounts to choose from, in file order.</summary>
    public ObservableCollection<S3Account> Accounts { get; } = [];

    /// <summary>True for a new connection (the dialog's title).</summary>
    public bool IsNew => _existing is null;

    /// <summary>The fields can be changed: not while a test runs.</summary>
    public bool IsEditable => !IsTesting;

    /// <summary>The results of the last test, one per check; cleared when a tested field changes.</summary>
    public ObservableCollection<S3CheckResult> Results { get; } = [];

    /// <summary>The connection as saved, with the chosen account's id. Set by <see cref="SaveCommand"/>.</summary>
    public S3ConnectionInfo? Result { get; private set; }

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
            var region = Region.Trim();
            if (region.Length == 0) errors.Add(RegionRequiredKey);
            else if (!S3Regions.IsValid(region)) errors.Add(RegionInvalidKey);
            if (!BucketPattern.IsMatch(Bucket.Trim())) errors.Add(BucketInvalidKey);
            if (SelectedAccount is null) errors.Add(AccountRequiredKey);
            return errors;
        }
    }

    /// <summary>The error shown below the name: only "taken" (a missing name just keeps Save disabled).</summary>
    public string? NameHintKey => Errors.Contains(NameTakenKey) ? NameTakenKey : null;

    /// <summary>The error shown below the region: only "invalid" (a missing region just keeps Save disabled).</summary>
    public string? RegionHintKey => Errors.Contains(RegionInvalidKey) ? RegionInvalidKey : null;

    /// <summary>The error shown below the bucket: only once something is typed.</summary>
    public string? BucketHintKey => Bucket.Trim().Length > 0 && Errors.Contains(BucketInvalidKey) ? BucketInvalidKey : null;

    /// <summary>
    /// The hint below the account: <see cref="AccountRequiredKey"/> when an edited connection's account no longer exists,
    /// <see cref="AccountNeedsSecretKey"/> when the chosen account's secret has to be entered again (Test is disabled), else <c>null</c>.
    /// </summary>
    public string? AccountHintKey => SelectedAccount switch
    {
        null => _existing is not null ? AccountRequiredKey : null,
        { NeedsSecret: true } => AccountNeedsSecretKey,
        _ => null,
    };

    private bool IsOwnName(string name) => _existing is not null && string.Equals(name, _existing.Name, StringComparison.OrdinalIgnoreCase);

    private bool CanSave() => !IsTesting && Errors.Count == 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Result = new S3ConnectionInfo(_existing?.Id ?? Guid.NewGuid().ToString("N"), Name.Trim(), Region.Trim(), Bucket.Trim(),
            SelectedAccount!.Id);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The test needs everything but the name, and an account with a usable secret.</summary>
    private bool CanTest() =>
        !IsTesting && SelectedAccount is { NeedsSecret: false } && Errors.All(key => key is NameRequiredKey or NameTakenKey);

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        using var cts = new CancellationTokenSource();
        _testCts = cts;
        Results.Clear();
        TestStatusKey = null;
        IsTesting = true;
        try
        {
            var results = await _tester.RunAsync(ConnectionToTest(SelectedAccount!), CheckWrite, cts.Token);
            foreach (var result in results)
                Results.Add(result);
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

    /// <summary>The fields resolved with the chosen account; <see cref="CanTest"/> ensures it has its secret.</summary>
    private S3Connection ConnectionToTest(S3Account account) =>
        S3ConnectionResolver.Resolve(new S3ConnectionInfo(_existing?.Id ?? "", Name.Trim(), Region.Trim(), Bucket.Trim(), account.Id), account)!;

    [RelayCommand(CanExecute = nameof(IsTesting))]
    private void CancelTest() => _testCts?.Cancel();

    /// <summary>"New account…": the account list is read again and the created account chosen.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void NewAccount()
    {
        if (_createAccount() is not { } created) return;
        Accounts.Clear();
        foreach (var account in _accounts())
            Accounts.Add(account);
        var match = Accounts.FirstOrDefault(a => SameId(a.Id, created.Id));
        if (match is null)
        {
            match = created;
            Accounts.Add(created);
        }
        SelectedAccount = match;
    }

    /// <summary>Applies the region a region warning names.</summary>
    [RelayCommand]
    private void UseRegion(string? region)
    {
        if (!string.IsNullOrWhiteSpace(region))
            Region = region.Trim();
    }

    partial void OnRegionChanged(string value) => ClearResults();

    partial void OnBucketChanged(string value) => ClearResults();

    partial void OnSelectedAccountChanged(S3Account? value) => ClearResults();

    private void ClearResults()
    {
        if (IsTesting) return;
        Results.Clear();
        TestStatusKey = null;
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
