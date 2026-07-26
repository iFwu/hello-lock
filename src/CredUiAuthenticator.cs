using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace HelloLock;

public enum AuthOutcome { Verified, Failed, HelloUnavailable }

// Uses the same supported flow as Chromium's Windows device authentication:
// CredUI collects a credential and LSA verifies it for the current user.
public static class CredUiAuthenticator
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ErrorSuccess = 0;
    private const uint ErrorCancelled = 1223;
    private const uint CredUiEnumerateCurrentUser = 0x00000200;
    private const int InteractiveLogon = 2;

    public static Task<(AuthOutcome outcome, string detail)> TryVerifyCurrentUserAsync(
        IntPtr ownerWindow)
    {
        return Task.Run(() => TryVerifyCurrentUser(ownerWindow));
    }

    public static bool IsCredentialUiForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == 0) return false;

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return false;

        try
        {
            var path = new StringBuilder(1024);
            int pathLength = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref pathLength)) return false;

            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "CredentialUIBroker.exe");
            return string.Equals(
                Path.GetFullPath(path.ToString()),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static (AuthOutcome outcome, string detail) TryVerifyCurrentUser(
        IntPtr ownerWindow)
    {
        IntPtr lsaHandle = IntPtr.Zero;
        IntPtr authBuffer = IntPtr.Zero;
        uint authBufferSize = 0;

        try
        {
            uint status = LsaConnectUntrusted(out lsaHandle);
            if (status != 0)
                return Fail("LsaConnectUntrusted", status);

            // CredUI must choose the package. Preselecting Negotiate or adding
            // undocumented packing flags filters the prompt down to passwords
            // on passwordless Microsoft accounts and hides the PIN provider.
            uint authenticationPackage = 0;

            var ui = new CREDUI_INFO
            {
                Size = (uint)Marshal.SizeOf<CREDUI_INFO>(),
                Parent = ownerWindow,
                MessageText = Localization.Get("Credential.Prompt"),
                CaptionText = "HelloLock",
                Banner = IntPtr.Zero,
            };

            bool save = false;
            uint promptResult = CredUIPromptForWindowsCredentials(
                ref ui,
                0,
                ref authenticationPackage,
                IntPtr.Zero,
                0,
                out authBuffer,
                out authBufferSize,
                ref save,
                CredUiEnumerateCurrentUser);

            AppendLog(
                $"CredUI result={promptResult}, package={authenticationPackage}, " +
                $"bufferSize={authBufferSize}");

            if (promptResult == ErrorCancelled)
                return (AuthOutcome.Failed, Localization.Get("Credential.Canceled"));
            if (promptResult != ErrorSuccess)
                return (AuthOutcome.HelloUnavailable, Localization.Format("Credential.PromptFailed", promptResult));
            if (authBuffer == IntPtr.Zero || authBufferSize == 0)
                return (AuthOutcome.Failed, Localization.Get("Credential.NoBuffer"));

            return ValidateAuthenticationBuffer(
                lsaHandle,
                authenticationPackage,
                authBuffer,
                authBufferSize);
        }
        catch (Exception ex)
        {
            AppendLog($"Exception: {ex}");
            return (AuthOutcome.Failed, Localization.Format("Credential.Exception", ex.Message));
        }
        finally
        {
            if (authBuffer != IntPtr.Zero)
            {
                ZeroAndFreeCoTaskMem(authBuffer, authBufferSize);
            }
            if (lsaHandle != IntPtr.Zero)
            {
                LsaDeregisterLogonProcess(lsaHandle);
            }
        }
    }

    private static (AuthOutcome outcome, string detail) ValidateAuthenticationBuffer(
        IntPtr lsaHandle,
        uint authenticationPackage,
        IntPtr authBuffer,
        uint authBufferSize)
    {
        IntPtr originBuffer = IntPtr.Zero;
        IntPtr profileBuffer = IntPtr.Zero;
        IntPtr token = IntPtr.Zero;

        try
        {
            LSA_STRING origin = CreateLsaString("HelloLock", out originBuffer);
            var tokenSource = new TOKEN_SOURCE
            {
                SourceName = Encoding.ASCII.GetBytes("HELLOCK "),
            };

            if (!AllocateLocallyUniqueId(out tokenSource.SourceIdentifier))
            {
                uint error = (uint)Marshal.GetLastWin32Error();
                return (AuthOutcome.Failed, Localization.Format("Credential.LuidFailed", error));
            }

            uint status = LsaLogonUser(
                lsaHandle,
                ref origin,
                InteractiveLogon,
                authenticationPackage,
                authBuffer,
                authBufferSize,
                IntPtr.Zero,
                ref tokenSource,
                out profileBuffer,
                out _,
                out _,
                out token,
                out _,
                out uint subStatus);

            AppendLog(
                $"LsaLogonUser status=0x{status:X8}, subStatus=0x{subStatus:X8}, " +
                $"win32={LsaNtStatusToWinError(status)}");

            if (status != 0)
            {
                uint win32 = LsaNtStatusToWinError(status);
                uint subWin32 = LsaNtStatusToWinError(subStatus);
                return (
                    AuthOutcome.Failed,
                    Localization.Format("Credential.LsaFailed", win32, subWin32, status));
            }

            using var current = WindowsIdentity.GetCurrent();
            using var authenticated = new WindowsIdentity(token);
            if (current.User is null || authenticated.User is null ||
                !current.User.Equals(authenticated.User))
            {
                return (
                    AuthOutcome.Failed,
                    Localization.Format("Credential.OtherUser", authenticated.Name));
            }

            return (AuthOutcome.Verified, "");
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            if (profileBuffer != IntPtr.Zero) LsaFreeReturnBuffer(profileBuffer);
            if (originBuffer != IntPtr.Zero) Marshal.FreeHGlobal(originBuffer);
        }
    }

    private static (AuthOutcome outcome, string detail) Fail(string operation, uint ntStatus)
    {
        uint win32 = LsaNtStatusToWinError(ntStatus);
        AppendLog($"{operation} status=0x{ntStatus:X8}, win32={win32}");
        return (AuthOutcome.Failed, Localization.Format("Credential.OperationFailed", operation, win32, ntStatus));
    }

    private static LSA_STRING CreateLsaString(string value, out IntPtr buffer)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        buffer = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        Marshal.WriteByte(buffer, bytes.Length, 0);
        return new LSA_STRING
        {
            Length = (ushort)bytes.Length,
            MaximumLength = (ushort)(bytes.Length + 1),
            Buffer = buffer,
        };
    }

    private static void ZeroAndFreeCoTaskMem(IntPtr buffer, uint size)
    {
        try
        {
            if (size > 0)
            {
                unsafe
                {
                    CryptographicOperations.ZeroMemory(
                        new Span<byte>(buffer.ToPointer(), checked((int)size)));
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static void AppendLog(string message)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HelloLock");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "authentication.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostic logging must not alter authentication behavior.
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public uint Size;
        public IntPtr Parent;
        [MarshalAs(UnmanagedType.LPWStr)] public string MessageText;
        [MarshalAs(UnmanagedType.LPWStr)] public string CaptionText;
        public IntPtr Banner;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_SOURCE
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] SourceName;
        public LUID SourceIdentifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QUOTA_LIMITS
    {
        public UIntPtr PagedPoolLimit;
        public UIntPtr NonPagedPoolLimit;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public UIntPtr PagefileLimit;
        public long TimeLimit;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern uint CredUIPromptForWindowsCredentials(
        ref CREDUI_INFO uiInfo,
        uint authenticationError,
        ref uint authenticationPackage,
        IntPtr inputAuthenticationBuffer,
        uint inputAuthenticationBufferSize,
        out IntPtr outputAuthenticationBuffer,
        out uint outputAuthenticationBufferSize,
        [MarshalAs(UnmanagedType.Bool)] ref bool save,
        uint flags);

    [DllImport("secur32.dll")]
    private static extern uint LsaConnectUntrusted(out IntPtr lsaHandle);

    [DllImport("secur32.dll")]
    private static extern uint LsaLogonUser(
        IntPtr lsaHandle,
        ref LSA_STRING originName,
        int logonType,
        uint authenticationPackage,
        IntPtr authenticationInformation,
        uint authenticationInformationLength,
        IntPtr localGroups,
        ref TOKEN_SOURCE sourceContext,
        out IntPtr profileBuffer,
        out uint profileBufferLength,
        out LUID logonId,
        out IntPtr token,
        out QUOTA_LIMITS quotas,
        out uint subStatus);

    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(uint status);

    [DllImport("secur32.dll")]
    private static extern uint LsaFreeReturnBuffer(IntPtr buffer);

    [DllImport("secur32.dll")]
    private static extern uint LsaDeregisterLogonProcess(IntPtr lsaHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocateLocallyUniqueId(out LUID locallyUniqueId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executableName,
        ref int size);

}
