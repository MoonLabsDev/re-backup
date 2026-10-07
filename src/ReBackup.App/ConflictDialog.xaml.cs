using System.Windows;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;
using ReBackup.Core.Versions;

namespace ReBackup.App;

/// <summary>Overwrite / Skip / Keep both / Cancel for the files a restore finds at its destination.</summary>
public partial class ConflictDialog : Window
{
    /// <param name="details">Parts of the version that cannot be read; listed below the question when not empty.</param>
    public ConflictDialog(string title, string message, IReadOnlyList<string> details)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = title;
        MessageText.Text = message;
        if (details.Count > 0)
        {
            DetailsPanel.Visibility = Visibility.Visible;
            DetailsTitle.Text = Loc.F("dialog.conflict.details", ("count", details.Count));
            DetailsText.Text = FailuresDialog.Join(details);
        }
    }

    /// <summary>The answer; only meaningful when <see cref="Window.ShowDialog"/> returned true.</summary>
    public ConflictPolicy Choice { get; private set; }

    private void OnOverwrite(object sender, RoutedEventArgs e) => Close(ConflictPolicy.Overwrite);

    private void OnSkip(object sender, RoutedEventArgs e) => Close(ConflictPolicy.Skip);

    private void OnKeepBoth(object sender, RoutedEventArgs e) => Close(ConflictPolicy.KeepBoth);

    private void Close(ConflictPolicy choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
