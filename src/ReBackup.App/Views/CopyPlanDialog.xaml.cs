using System.ComponentModel;
using System.Windows;
using ReBackup.App.ViewModels;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App.Views;

/// <summary>
/// "Copy plan…" on a <see cref="CopyPlanViewModel"/>. Closing it while a copy runs stops the copy first (the window
/// closes once it has stopped and released its locks).
/// </summary>
public partial class CopyPlanDialog : Window
{
    private readonly CopyPlanViewModel _viewModel;
    private bool _closeWhenStopped;

    public CopyPlanDialog(CopyPlanViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loc.Bind(this, TitleProperty, "copy.title");
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CopyPlanViewModel.IsRunning) && !_viewModel.IsRunning && _closeWhenStopped)
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
