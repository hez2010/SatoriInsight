using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Processes;

[SupportedOSPlatform("windows")]
internal static class WindowsProcessCommandLine
{
    private const uint QueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaximumBufferSize = 128 * 1024;

    public static Task<IReadOnlyList<LocalDotNetProcess>> ReadAllAsync(IReadOnlyList<LocalDotNetProcess> processes, CancellationToken token)
    {
        return Task.Run<IReadOnlyList<LocalDotNetProcess>>(() =>
        {
            var result = new List<LocalDotNetProcess>(processes.Count);

            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                result.Add(process with { CommandLine = Read(process) });
            }

            return result;
        }, token);
    }

    private static string? Read(LocalDotNetProcess identity)
    {
        using var process = OpenProcess(QueryLimitedInformation, false, identity.ProcessId);

        // Use the same handle for identity validation and the query, avoiding PID reuse races.
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out _, out _)
            || created != identity.StartTimeUtc.ToFileTimeUtc())
        {
            return null;
        }

        var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, 0, 0, out var required);
        var headerSize = Marshal.SizeOf<UnicodeString>();

        if (status != StatusInfoLengthMismatch || required < headerSize || required > MaximumBufferSize)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(required);

        try
        {
            status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, required, out var written);

            if (status < 0 || written < headerSize || written > required)
            {
                return null;
            }

            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = text.Buffer.ToInt64() - buffer.ToInt64();

            if (text.Length == 0 || text.Length % sizeof(char) != 0 || text.Length > text.MaximumLength
                || offset < headerSize || offset > written - text.Length)
            {
                return null;
            }

            return Marshal.PtrToStringUni(text.Buffer, text.Length / sizeof(char));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, nint buffer, int length, out int required);
}
