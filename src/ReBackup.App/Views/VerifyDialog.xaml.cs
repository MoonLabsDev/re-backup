using System.ComponentModel;
using System.Windows;
using ReBackup.App.ViewModels;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App.Views;

/// <summary>
/// "Verify backup…" on a <see cref="VerifyViewModel"/>. Closing it while a verify runs stops the verify first (the window
/// closes once it has stopped and released its lock).
/// </summary>
public partial class VerifyDialog : Window
{
    private readonly VerifyViewModel _viewModel;
    private bool _closeWhenStopped;

    public VerifyDialog(VerifyViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loc.Bind(this, TitleProperty, "verify.title");
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VerifyViewModel.IsRunning) && !_viewModel.IsRunning && _closeWhenStopped)
            Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsRunning)
        {
            e.Cancel = true;
            _closeWhenStopped = true;
            _viewModel.Stop();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Stop();
        base.OnClosed(e);
    }
}
