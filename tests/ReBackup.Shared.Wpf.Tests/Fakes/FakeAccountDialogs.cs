using ReBackup.Shared.Wpf.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.Tests.Fakes;

/// <summary>Records what <see cref="S3AccountsViewModel"/> asks and answers with preset results.</summary>
public sealed class FakeAccountDialogs : IS3AccountDialogs
{
    /// <summary>What the account dialog returns: gets the edited account (null = new) and the name check.</summary>
    public Func<S3Account?, Func<string, bool>, S3Account?> OnEdit { get; set; } = (_, _) => null;

    public bool ConfirmAnswer { get; set; } = true;

    public List<S3Account?> Edited { get; } = [];

    public List<Func<string, bool>> NameChecks { get; } = [];

    public List<(string TitleKey, string Message)> Confirms { get; } = [];

    public List<string> Errors { get; } = [];

    public S3Account? EditAccount(S3Account? existing, Func<string, bool> isNameTaken)
    {
        Edited.Add(existing);
        NameChecks.Add(isNameTaken);
        return OnEdit(existing, isNameTaken);
    }

    public bool Confirm(string titleKey, string message)
    {
        Confirms.Add((titleKey, message));
        return ConfirmAnswer;
    }

    public void ShowError(string message) => Errors.Add(message);
}
