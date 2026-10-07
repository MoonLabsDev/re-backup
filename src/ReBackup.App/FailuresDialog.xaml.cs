using System.Windows;
using System.Windows.Automation;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App;

/// <summary>
/// A message with a list of paths and reasons (read-only, copyable). With a confirm text it asks to go on
/// (confirm → true, Cancel → false); without one it only informs (Close).
/// </summary>
public partial class FailuresDialog : Window
{
    /// <summary>Lines shown at most; the rest is counted ("… and 1,234 more").</summary>
    public const int MaxLines = 1000;

    public FailuresDialog(string title, string message, IReadOnlyList<string> lines, string? confirmText)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = title;
        MessageText.Text = message;
        LinesText.Text = Join(lines);
        if (confirmText is not null)
        {
            ConfirmButton.Content = confirmText;
            AutomationProperties.SetName(ConfirmButton, confirmText);
            ConfirmButton.Visibility = Visibility.Visible;
            ConfirmButton.IsDefault = true;
            // Replaces the label binding of the XAML: this dialog lives only as long as one question.
            CloseButton.Content = Loc.T("common.cancel");
            CloseButton.IsDefault = false;
            AutomationProperties.SetName(CloseButton, Loc.T("common.cancel"));
        }
    }

    /// <summary>The lines, one per row, cut after <see cref="MaxLines"/>.</summary>
    internal static string Join(IReadOnlyList<string> lines)
    {
        var shown = string.Join(Environment.NewLine, lines.Take(MaxLines));
        return lines.Count > MaxLines
            ? shown + Environment.NewLine + Loc.F("dialog.failures.more", ("count", lines.Count - MaxLines))
            : shown;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
