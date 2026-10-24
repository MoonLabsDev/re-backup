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

    /// <summary>Creates a listening instance whose ACL grants the current user only.</summary>
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }
}
