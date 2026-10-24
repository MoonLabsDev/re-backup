using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ReBackup.Control;

public static class ControlPipe
{
    public const int Protocol = 1;

    /// <summary>Pipe name (without <c>\\.\pipe\</c>): <c>ReBackup.Control.&lt;SID&gt;</c>.</summary>
    [SupportedOSPlatform("windows")]
    public static string NameForCurrentUser() => "ReBackup.Control." + WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>
    /// Creates a listening instance whose ACL grants the current user only. The first instance of a pipe must pass
    /// <paramref name="firstInstance"/> so the call fails when the name is already taken (no joining a foreign pipe).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateServerStream(string pipeName, bool firstInstance = false)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        // .NET's CurrentUserOnly client compares the pipe owner with the token's default owner (not the user SID); an
        // elevated admin token defaults to Administrators. Using the same value pairs only same-elevation processes.
        security.SetOwner(WindowsIdentity.GetCurrent().Owner!);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 0, 0, security);
    }
}
