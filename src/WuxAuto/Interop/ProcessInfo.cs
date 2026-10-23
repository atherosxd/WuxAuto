using System.Runtime.InteropServices;

namespace WuxAuto.Interop;

/// <summary>
/// Elevation checks. A non-elevated process cannot inject input into an elevated one (UIPI):
/// SendInput is silently dropped, which looks exactly like "the game ignores my keys".
/// </summary>
public static class ProcessInfo
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    public static bool IsElevated => IsProcessElevated(Environment.ProcessId);

    public static bool IsProcessElevated(int processId)
    {
        if (processId <= 0)
            return false;

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
            return false;

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out IntPtr token))
                return false;

            try
            {
                return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
