using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.App.ViewModels;

/// <summary>
/// Settings → "S3 connections…": the saved connections with Add and Edit (the shared connection dialog) and Delete,
/// which is refused while a plan uses the connection. Every change is stored at once; a file that cannot be read is
/// shown with its reason and never overwritten.
/// </summary>
public sealed partial class S3ConnectionsViewModel : ObservableObject
{
    private readonly S3Connections _connections;
    private readonly IDialogService _dialogs;
    private readonly Func<string, IReadOnlyList<string>> _plansUsing;

    /// <param name="plansUsing">The names of the plans (saved or edited) that use a connection, by its id.</param>
    public S3ConnectionsViewModel(S3Connections connections, IDialogService dialogs, Func<string, IReadOnlyList<string>> plansUsing)
    {
        _connections = connections;
        _dialogs = dialogs;
        _plansUsing = plansUsing;
        connections.Reload();
        connections.All.CollectionChanged += OnListChanged;
    }

    /// <summary>The saved connections in file order.</summary>
    public ObservableCollection<S3ConnectionInfo> Connections => _connections.All;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    private S3ConnectionInfo? _selected;

    /// <summary>Why the file cannot be read; null when it was read.</summary>
    public string? LoadError =>
        _connections.LoadError is { } error ? Loc.F("s3connections.loadFailed", ("error", error)) : null;

    /// <summary>There are no connections (and the file was read).</summary>
    public bool IsEmpty => _connections.LoadError is null && Connections.Count == 0;

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LoadError));
        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The window closed: it no longer follows the list.</summary>
    public void Detach() => _connections.All.CollectionChanged -= OnListChanged;

    private bool CanAdd() => _connections.LoadError is null;

    private bool CanChange() => CanAdd() && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add() => SaveFromDialog(null);

    [RelayCommand(CanExecute = nameof(CanChange))]
    private void Edit()
    {
        if (Selected is { } connection)
            SaveFromDialog(connection);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private void Delete()
    {
        if (Selected is not { } connection)
            return;
        var users = _plansUsing(connection.Id);
        if (users.Count > 0)
        {
            // The plans are user data, one per line below the label.
            _dialogs.ShowError(Loc.T("s3connections.title"), Loc.F("s3connections.inUse", ("name", connection.Name)) +
                Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, users.Select(name => "• " + name)));
            return;
        }
        if (!_dialogs.ConfirmDefaultNo(Loc.T("s3connections.title"), Loc.F("s3connections.deleteConfirm", ("name", connection.Name))))
            return;
        try
        {
            _connections.Delete(connection.Id);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            _dialogs.ShowError(Loc.T("s3connections.title"), Loc.F("s3connections.saveFailed", ("error", ex.Message)));
        }
        Selected = null;
    }

    private void SaveFromDialog(S3ConnectionInfo? existing)
    {
        if (_dialogs.EditS3Connection(existing) is not { } result)
            return;
        try
        {
            _connections.Save(result);
        }
        catch (Exception ex) when (IsStoreFailure(ex) || ex is ArgumentException)
        {
            _dialogs.ShowError(Loc.T("s3connections.title"), Loc.F("s3connections.saveFailed", ("error", ex.Message)));
            return;
        }
        Selected = Connections.FirstOrDefault(c => string.Equals(c.Id, result.Id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStoreFailure(Exception ex) => ex is JsonException or IOException or UnauthorizedAccessException;
}
