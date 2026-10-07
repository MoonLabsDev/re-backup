using System.Diagnostics;
using FluentAssertions;

namespace ReBackup.Storage.Tests.TestSupport;

/// <summary>Creates directory junctions for tests. Remove them with <c>Directory.Delete(link)</c>, which removes the link only.</summary>
public static class Junction
{
    public static void Create(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "the junction must exist for this test");
    }
}
