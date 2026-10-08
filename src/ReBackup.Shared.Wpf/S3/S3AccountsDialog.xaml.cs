using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.S3;

/// <summary>
/// The S3 accounts of an app's <see cref="S3ConnectionStore"/>: add, edit (double-click) and delete; changes are stored at once.
/// Closed with Close or Esc; <see cref="Window.ShowDialog"/>'s result carries no meaning.
/// </summary>
public partial class S3AccountsDialog : Window
{
    private readonly S3AccountsViewModel _viewModel;

    /// <summary>The dialog with the real account dialog and message boxes, owned by this window.</summary>
    public S3AccountsDialog(S3ConnectionStore store, S3ConnectionTester tester)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tester);
        _viewModel = new S3AccountsViewModel(store, new S3AccountDialogs(tester, () => this));
        Initialize();
    }

    public S3AccountsDialog(S3AccountsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        Initialize();
    }

    private void Initialize()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loc.Bind(this, TitleProperty, "s3.accounts.title");
        DataContext = _viewModel;
    }

    /// <summary>A double-click on a row edits it (not on the header or the empty area).</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AccountList, (DependencyObject)e.OriginalSource) is ListViewItem
            && _viewModel.EditCommand.CanExecute(null))
            _viewModel.EditCommand.Execute(null);
    }
}
