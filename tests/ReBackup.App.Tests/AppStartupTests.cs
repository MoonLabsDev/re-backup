using FluentAssertions;
using ReBackup.App.Services;
using ReBackup.Core.Settings;
using ReBackup.App.Tests.TestSupport;
using Xunit;

namespace ReBackup.App.Tests;

public sealed class AppStartupTests
{
    private const string Exe = @"C:\Apps\ReBackup\ReBackup.exe";
    private const string Stale = @"""D:\Old\ReBackup.exe"" --minimized";

    [Fact]
    public void ReBackup_starts_minimized()
    {
        AppStartup.Arguments.Should().Be("--minimized");
    }

    [Fact]
    public void Apply_registers_with_the_app_arguments()
    {
        var key = new InMemoryRunKey();

        AppStartup.Apply(true, key, Exe);

        key.Values["ReBackup"].Should().Be("\"" + Exe + "\" --minimized");
    }

    [Fact]
    public void On_start_with_StartWithWindows_on_a_stale_path_is_replaced()
    {
        var key = new InMemoryRunKey();
        key.Values["ReBackup"] = Stale;

        AppStartup.RefreshOnStart(new AppSettings { StartWithWindows = true }, key, Exe);

        key.Values["ReBackup"].Should().Be("\"" + Exe + "\" --minimized");
    }

    [Fact]
    public void On_start_with_StartWithWindows_off_the_key_is_untouched()
    {
        var key = new InMemoryRunKey();
        key.Values["ReBackup"] = Stale;

        AppStartup.RefreshOnStart(new AppSettings { StartWithWindows = false }, key, Exe);

        key.Values["ReBackup"].Should().Be(Stale);
    }
}
