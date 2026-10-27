using Microsoft.Win32;
using PharmaLIMS.Infrastructure;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace PharmaLIMS.Services;

/// <summary>Interactive, non-elevated Windows account handoff. Never changes trust or permissions.</summary>
internal static class WindowsUserSwitchService
{
    private const string SwitchedUserArgument = "--switched-windows-user";
    private const int CredentialCharacters = 513;

    // Runs before AppConfig or logging can resolve a path inherited from the parent actor.
    internal static (string Server, string Database)? PrepareSwitchedProcess(string[] arguments)
    {
        int index = Array.IndexOf(arguments, SwitchedUserArgument);
        if (index < 0) return null;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Windows identity is unavailable.");
        if (index + 3 >= arguments.Length || arguments[index + 1] != sid)
            throw new InvalidOperationException("The switched Windows identity could not be confirmed.");
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
        string profile = Environment.ExpandEnvironmentVariables(key?.GetValue("ProfileImagePath") as string ?? "");
        if (!Path.IsPathFullyQualified(profile) || !Directory.Exists(profile))
            throw new InvalidOperationException("The selected Windows profile is unavailable.");
        var changes = new Dictionary<string, string?>();
        SwitchedProcessEnvironment.Apply(changes, profile);
        foreach (string name in SwitchedProcessEnvironment.RemovedOverrides)
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        foreach (var entry in changes)
            Environment.SetEnvironmentVariable(entry.Key, entry.Value, EnvironmentVariableTarget.Process);
        string[] account = identity.Name.Split('\\', 2);
        Environment.SetEnvironmentVariable("USERDOMAIN", account.Length == 2 ? account[0] : "");
        Environment.SetEnvironmentVariable("USERNAME", account[^1]);
        Environment.SetEnvironmentVariable("HOMEDRIVE", Path.GetPathRoot(profile)?.TrimEnd('\\'));
        Environment.SetEnvironmentVariable("HOMEPATH", profile[Path.GetPathRoot(profile)!.TrimEnd('\\').Length..]);
        return (arguments[index + 2], arguments[index + 3]);
    }

    // Null means the user cancelled; false means the new process exists but has not shown login yet.
    internal static async Task<bool?> SwitchAsync(IntPtr owner)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "PharmaLIMS.exe");
        if (!File.Exists(executable) || !string.Equals(Environment.ProcessPath, executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Switch User requires the installed PharmaLIMS.exe desktop application.");
        var prompt = new CredentialUiInfo
        {
            Size = Marshal.SizeOf<CredentialUiInfo>(), Parent = owner,
            Caption = "PharmaLIMS - Switch Windows User",
            Message = "Enter the Windows account for the next user (for example, " +
                Environment.MachineName + @"\LimsReview). This is the Windows password, not the PharmaLIMS password."
        };
        var username = new StringBuilder(CredentialCharacters);
        IntPtr passwordBuffer = Marshal.AllocHGlobal(CredentialCharacters * sizeof(char));
        bool save = false;
        try
        {
            ClearBuffer(passwordBuffer);
            // Generic credentials, always prompt, do not persist, exclude certificates; no save checkbox.
            int result = CredUIPromptForCredentials(ref prompt, "PharmaLIMS Windows User Switch",
                IntPtr.Zero, 0, username, CredentialCharacters, passwordBuffer, CredentialCharacters,
                ref save, 0x40000 | 0x80 | 0x2 | 0x8);
            if (result == 1223) return null;
            if (result != 0) throw new Win32Exception(result);
            using var password = new SecureString();
            for (int i = 0; i < CredentialCharacters; i++)
            {
                char value = (char)Marshal.ReadInt16(passwordBuffer, i * sizeof(char));
                if (value == '\0') break;
                password.AppendChar(value);
            }
            password.MakeReadOnly();
            ClearBuffer(passwordBuffer);
            string account = username.ToString().Trim();
            if (password.Length == 0 || account.Length == 0)
                throw new InvalidOperationException("Enter a Windows account and its Windows password.");
            string domain = Environment.MachineName;
            string user = account;
            int separator = account.IndexOf('\\');
            if (separator >= 0) { domain = account[..separator]; user = account[(separator + 1)..]; }
            if (domain == ".") domain = Environment.MachineName;
            if (string.IsNullOrWhiteSpace(user) || user.Contains('@') ||
                !domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use a local standard Windows workstation account on this computer.");
            IntPtr plain = Marshal.SecureStringToGlobalAllocUnicode(password);
            string sid;
            try
            {
                if (!LogonUser(user, domain, plain, 2, 0, out SafeAccessTokenHandle token))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                using (WindowsIdentity current = WindowsIdentity.GetCurrent())
                {
                    sid = identity.User?.Value ?? throw new InvalidOperationException("Windows identity is unavailable.");
                    if (sid == current.User?.Value)
                        throw new InvalidOperationException("This is already the current Windows account. Use Sign In to resume, or choose another Windows user.");
                    if (identity.Groups?.Any(group => group.Value == "S-1-5-32-544") == true)
                        throw new InvalidOperationException("Choose a standard Windows workstation account. Administrator accounts cannot be used for this handoff.");
                }
            }
            finally { Marshal.ZeroFreeGlobalAllocUnicode(plain); }

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, UserName = user, Domain = domain,
                Password = password, LoadUserProfile = true, WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add(SwitchedUserArgument);
            start.ArgumentList.Add(sid);
            var target = SwitchedProcessEnvironment.ReadTarget(AppConfig.ConnectionString);
            start.ArgumentList.Add(target.Server);
            start.ArgumentList.Add(target.Database);
            foreach (string name in SwitchedProcessEnvironment.RemovedOverrides) start.Environment.Remove(name);
            foreach (string name in new[] { "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP", "USERNAME", "USERDOMAIN", "HOMEPATH", "HOMEDRIVE" })
                start.Environment.Remove(name);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("The new application could not be started.");
            // Keep the old, logged-out window until the child's real login is visible.
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (process.HasExited)
                    throw new InvalidOperationException("The application exited under the selected Windows account. Review that account's PharmaLIMS startup log.");
                process.Refresh();
                if (SessionTransitionCoordinator.CanFinishHandoff(process.HasExited,
                    process.MainWindowHandle != IntPtr.Zero, process.MainWindowTitle))
                    return true;
                await Task.Delay(200);
            }
            return false;
        }
        finally
        {
            ClearBuffer(passwordBuffer);
            Marshal.FreeHGlobal(passwordBuffer);
        }
    }

    private static void ClearBuffer(IntPtr buffer)
    {
        for (int i = 0; i < CredentialCharacters; i++) Marshal.WriteInt16(buffer, i * sizeof(char), 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredentialUiInfo
    {
        public int Size;
        public IntPtr Parent;
        [MarshalAs(UnmanagedType.LPWStr)] public string Message;
        [MarshalAs(UnmanagedType.LPWStr)] public string Caption;
        public IntPtr Banner;
    }

    [DllImport("credui.dll", EntryPoint = "CredUIPromptForCredentialsW", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForCredentials(ref CredentialUiInfo info, string target, IntPtr reserved,
        int error, StringBuilder username, int usernameMax, IntPtr password, int passwordMax,
        [MarshalAs(UnmanagedType.Bool)] ref bool save, int flags);

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string user, string domain, IntPtr password,
        int logonType, int provider, out SafeAccessTokenHandle token);
}
