using System;
using System.Runtime.InteropServices;

namespace HelloLock;

internal static class WindowsSessionState
{
    private const int WtsCurrentSession = -1;
    private const int WtsSessionInfoEx = 25;
    private const int WtsInfoExLevel1 = 1;
    private const int WtsActive = 0;
    private const int WtsSessionStateLock = 0;
    private const int WtsSessionStateUnlock = 1;

    internal static bool TryGetCurrentSessionLocked(out bool locked)
    {
        locked = false;
        IntPtr buffer = IntPtr.Zero;
        if (!WTSQuerySessionInformation(
                IntPtr.Zero,
                WtsCurrentSession,
                WtsSessionInfoEx,
                out buffer,
                out int bytesReturned))
            return false;

        try
        {
            int level1Offset = IntPtr.Size == 8 ? 8 : 4;
            if (bytesReturned < level1Offset + 12) return false;

            int level = Marshal.ReadInt32(buffer);
            int connectState = Marshal.ReadInt32(buffer, level1Offset + 4);
            int sessionFlags = Marshal.ReadInt32(buffer, level1Offset + 8);
            return TryInterpret(level, connectState, sessionFlags, out locked);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    internal static bool TryInterpret(
        int level,
        int connectState,
        int sessionFlags,
        out bool locked)
    {
        locked = false;
        if (level != WtsInfoExLevel1 ||
            sessionFlags is not (WtsSessionStateLock or WtsSessionStateUnlock))
            return false;

        locked = connectState != WtsActive || sessionFlags == WtsSessionStateLock;
        return true;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);
}
