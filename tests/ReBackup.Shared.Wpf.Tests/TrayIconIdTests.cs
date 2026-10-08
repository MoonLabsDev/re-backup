using FluentAssertions;
using ReBackup.Shared.Wpf.Services;
using Xunit;

namespace ReBackup.Shared.Wpf.Tests;

public sealed class TrayIconIdTests
{
    [Fact]
    public void Same_app_and_path_give_the_same_id()
    {
        TrayIconId.For("ReBackup", @"E:\Apps\ReBackup-1.2.0-win-x64.exe")
            .Should().Be(TrayIconId.For("ReBackup", @"E:\Apps\ReBackup-1.2.0-win-x64.exe"));
    }

    [Fact]
    public void The_path_is_compared_without_regard_to_case()
    {
        TrayIconId.For("ReBackup", @"E:\Apps\ReBackup.exe")
            .Should().Be(TrayIconId.For("ReBackup", @"e:\apps\rebackup.EXE"));
    }

    [Fact]
    public void Another_path_gives_another_id()
    {
        // Windows binds a tray icon id to the exe path; a renamed or moved exe needs its own id.
        TrayIconId.For("ReBackup", @"E:\Apps\ReBackup-1.2.0-win-x64.exe")
            .Should().NotBe(TrayIconId.For("ReBackup", @"E:\Apps\ReBackup-1.2.1-win-x64.exe"));
    }

    [Fact]
    public void Another_app_gives_another_id()
    {
        TrayIconId.For("ReBackup", @"E:\Apps\app.exe").Should().NotBe(TrayIconId.For("re-s3", @"E:\Apps\app.exe"));
    }

    [Fact]
    public void The_id_is_never_empty()
    {
        TrayIconId.For("ReBackup", "").Should().NotBe(Guid.Empty);
    }
}
