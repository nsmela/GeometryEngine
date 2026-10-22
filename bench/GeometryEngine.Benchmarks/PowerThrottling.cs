using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Keeps Windows from throttling the bench. A console process that is not in the foreground is
/// given EcoQoS a few seconds after it starts - lower clocks, efficiency cores - and runs about 5x
/// slower from then on, so a table's later rows would read slow for reasons that have nothing to do
/// with the code being measured.
/// </summary>
internal static class PowerThrottling
{
    private const int ProcessPowerThrottling = 4;
    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeed = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref State information, int size);

    /// <summary>Opts the process out of execution-speed throttling; a no-op off Windows.</summary>
    public static void Disable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Taking control of the flag and leaving it clear means never throttle, whatever Windows
        // would otherwise decide.
        var state = new State { Version = CurrentVersion, ControlMask = ExecutionSpeed, StateMask = 0 };
        SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<State>());
    }
}
