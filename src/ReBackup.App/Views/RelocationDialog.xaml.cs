using System.Windows;
using ReBackup.App.ViewModels;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App.Views;

/// <summary>Confirms a <see cref="RelocationDialogViewModel"/>: true when the user chose Apply (only enabled while it can apply).</summary>
public partial class RelocationDialog : Window
{
    private readonly RelocationDialogViewModel _viewModel;

    public RelocationDialog(RelocationDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loc.Bind(this, TitleProperty, "ignore.relocation.title");
        DataContext = viewModel;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanApply)
            DialogResult = true;
    }
}
