using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using ReBackup.Shared.Wpf.S3;
using ReBackup.Shared.Wpf.Tests.Fakes;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.Tests;

/// <summary>
/// The dialogs' XAML loads with the theme dictionaries the apps merge and lays out with test results, without showing a
/// window. The look itself (Dark/Light, both languages) is checked by hand.
/// </summary>
public class S3ConnectionDialogSmokeTests
{
    private static readonly string[] ThemeFiles = ["Colors.Dark.xaml", "Fonts.xaml", "Icons.xaml", "Flags.xaml", "Controls.xaml"];

    [Fact]
    public void The_dialog_loads_and_lays_out_its_results()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            var (client, fake) = FakeS3Client.Create();
            fake.BucketRegion = "eu-west-1";
            var account = new S3Account("acc", "Main", "AKIA1", "stored");
            var vm = new S3ConnectionDialogViewModel(
                new S3ConnectionInfo("id", "Backups", "eu-central-1", "my-bucket", "acc"), _ => false,
                new S3ConnectionTester(_ => client), () => [account], () => null);
            vm.TestCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            var content = Layout(new S3ConnectionDialog(vm));

            vm.Results.Should().HaveCount(5);
            content.DesiredSize.Height.Should().BeGreaterThan(0);
            FindAll<PasswordBox>(content).Should().BeEmpty("the secret belongs to the account");
            FindAll<ComboBox>(content).Should().Contain(combo => ReferenceEquals(combo.ItemsSource, vm.Accounts));
            FindAll<Button>(content).Should().Contain(button => ReferenceEquals(button.Command, vm.NewAccountCommand));
            FindAll<Button>(content).Where(button => button.Visibility == Visibility.Visible && ReferenceEquals(button.Command, vm.UseRegionCommand))
                .Should().ContainSingle("the region warning offers its region");
        });
    }

    [Fact]
    public void The_account_dialog_loads_and_lays_out_its_result()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            var vm = new S3AccountDialogViewModel(new S3Account("acc", "Main", "AKIA1", "stored"), _ => false,
                new S3ConnectionTester(_ => FakeS3Client.Create().Client));
            vm.TestCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            var content = Layout(new S3AccountDialog(vm));

            vm.TestResult.Should().NotBeNull();
            content.DesiredSize.Height.Should().BeGreaterThan(0);
            FindAll<PasswordBox>(content).Should().ContainSingle();
        });
    }

    [Fact]
    public void The_accounts_dialog_loads_and_lays_out_its_list()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            var dir = Path.Combine(Path.GetTempPath(), "rebackup-wpf-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new S3ConnectionStore(Path.Combine(dir, "connections.json"));
                store.SaveAccount(new S3Account("a", "Main", "AKIA1", "s1"));
                store.Save(new S3ConnectionInfo("c", "Backups", "eu-central-1", "my-bucket", "a"));
                var vm = new S3AccountsViewModel(store, new S3ConnectionTester(_ => FakeS3Client.Create().Client), new FakeAccountDialogs());

                var content = Layout(new S3AccountsDialog(vm));

                content.DesiredSize.Height.Should().BeGreaterThan(0);
                FindAll<ListView>(content).Should().ContainSingle().Which.Items.Count.Should().Be(1);
                FindAll<Button>(content).Should().Contain(button => ReferenceEquals(button.Command, vm.DeleteCommand));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        });
    }

    /// <summary>A window lays out only once shown; its content can be laid out on its own.</summary>
    private static FrameworkElement Layout(Window dialog)
    {
        var content = (FrameworkElement)dialog.Content;
        content.Measure(new Size(560, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
        return content;
    }

    private static void EnsureApplication()
    {
        if (Application.Current is not null)
            return;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var file in ThemeFiles)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/ReBackup.Shared.Wpf;component/Theme/{file}", UriKind.Absolute),
            });
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindAll<T>(child))
                yield return nested;
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }
}
