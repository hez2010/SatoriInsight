using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SatoriInsight.Services.Heap;

// ClrMD's Windows process reader exposes raw reads but not its address map. Query
// the same PID, including the VA-clone PID for snapshots, with read-only rights.
internal sealed class WindowsRegionMemory : IRegionMemory, IDisposable
{
    private readonly SafeProcessHandle _process;

    public int PointerSize { get; }

    public Architecture Architecture { get; }

    public WindowsRegionMemory(int processId, int pointerSize, Architecture architecture)
    {
        PointerSize = pointerSize;
        Architecture = architecture;
        _process = OpenProcess(0x0400 | 0x0010, false, processId);

        if (_process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _process.Dispose();
            throw new Win32Exception(error);
        }
    }

    public IEnumerable<(ulong Address, ulong Size)> ReadableRanges()
    {
        ulong address = 0;

        while (VirtualQueryEx(_process, (nint)address, out var region, (nuint)Marshal.SizeOf<MemoryInfo>()) != 0)
        {
            var start = (ulong)region.BaseAddress;
            var size = (ulong)region.RegionSize;

            if (size == 0 || start > ulong.MaxValue - size || start + size <= address)
            {
                yield break;
            }

            // Exclude no-access and guard pages; querying them does not fault them in.
            if (region.State == 0x1000 && (region.Protect & (0x01u | 0x100u)) == 0)
            {
                yield return (start, size);
            }

            address = start + size;
        }

        var error = Marshal.GetLastWin32Error();

        if (error is not (0 or 87 or 487))
        {
            throw new Win32Exception(error);
        }
    }

    public int Read(ulong address, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        ReadProcessMemory(_process, (nint)address, ref MemoryMarshal.GetReference(destination), (nuint)destination.Length, out var read);
        return (int)read;
    }

    public void Dispose() => _process.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public nuint BaseAddress;
        public nuint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryInfo info, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, ref byte buffer, nuint size, out nuint bytesRead);
}
