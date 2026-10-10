using FluentAssertions;
using ReBackup.Shared.Wpf.Services;
using ReBackup.Shared.Wpf.Tests.Fakes;
using Xunit;

namespace ReBackup.Shared.Wpf.Tests;

public sealed class StartupRegistrationTests
{
    private const string Exe = @"C:\Apps\ReBackup\ReBackup.exe";
    private const string Stale = @"""D:\Old\ReBackup.exe"" --minimized";

    [Fact]
    public void Enabled_writes_the_quoted_exe_followed_by_the_arguments()
    {
        var key = new InMemoryRunKey();

        StartupRegistration.Apply("Demo", true, "--minimized", key, Exe);

        key.Values["Demo"].Should().Be("\"" + Exe + "\" --minimized");
    }

    [Fact]
    public void The_arguments_are_taken_as_given_not_hard_coded()
    {
        var key = new InMemoryRunKey();

        StartupRegistration.Apply("Demo", true, "--tray --quiet", key, Exe);

        key.Values["Demo"].Should().Be("\"" + Exe + "\" --tray --quiet");
    }

    [Fact]
    public void Empty_arguments_leave_only_the_quoted_exe()
    {
        var key = new InMemoryRunKey();

        StartupRegistration.Apply("Demo", true, "", key, Exe);

        key.Values["Demo"].Should().Be("\"" + Exe + "\"");
    }

    [Fact]
    public void Disabled_removes_the_value()
    {
        var key = new InMemoryRunKey();
        key.Values["Demo"] = "old";

        StartupRegistration.Apply("Demo", false, "--minimized", key, Exe);

        key.Values.Should().NotContainKey("Demo");
    }

    [Fact]
    public void Refresh_replaces_a_stale_exe_path_when_enabled()
    {
        var key = new InMemoryRunKey();
        key.Values["Demo"] = Stale;

        StartupRegistration.Refresh("Demo", true, "--minimized", key, Exe);

        key.Values["Demo"].Should().Be("\"" + Exe + "\" --minimized");
    }

    [Fact]
    public void Refresh_leaves_the_key_untouched_when_disabled()
    {
        var key = new InMemoryRunKey();
        key.Values["Other"] = "x";

        StartupRegistration.Refresh("Demo", false, "--minimized", key, Exe);

        key.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["Other"] = "x" });
    }
}
