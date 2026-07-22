using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DinoRand.ApClient;

/// <summary>Byte-level process-memory seam so the poll engine / grant planner are testable
/// against synthetic snapshots (AP-CLIENT-PLAN.md Testing).</summary>
public interface IGameMemory
{
    /// <summary>True while the target process is attached and reachable.</summary>
    bool IsAttached { get; }

    /// <summary>Read <paramref name="buffer"/>.Length bytes at <paramref name="va"/>. False on failure.</summary>
    bool Read(uint va, Span<byte> buffer);

    /// <summary>Write the bytes at <paramref name="va"/>. False on failure.</summary>
    bool Write(uint va, ReadOnlySpan<byte> buffer);
}

public static class GameMemoryExtensions
{
    public static uint? ReadU32(this IGameMemory mem, uint va)
    {
        Span<byte> b = stackalloc byte[4];
        return mem.Read(va, b) ? BitConverter.ToUInt32(b) : null;
    }

    public static ushort? ReadU16(this IGameMemory mem, uint va)
    {
        Span<byte> b = stackalloc byte[2];
        return mem.Read(va, b) ? BitConverter.ToUInt16(b) : null;
    }

    public static byte? ReadU8(this IGameMemory mem, uint va)
    {
        Span<byte> b = stackalloc byte[1];
        return mem.Read(va, b) ? b[0] : null;
    }
}

/// <summary>
/// External memory-poll attach to the running game — kernel32 OpenProcess /
/// ReadProcessMemory / WriteProcessMemory by process name, never DLL injection (D2; K117 rules
/// out any loader cooperation). Both games are 32-bit, no ASLR, so plain static VAs work.
/// Windows host only — WSL cannot attach (documented in USER-GUIDE).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32ProcessMemory : IGameMemory, IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;

    private readonly string _processName;
    private nint _handle;
    private Process? _process;

    public Win32ProcessMemory(string processName = Dc1Symbols.ProcessName) => _processName = processName;

    public bool IsAttached => _handle != 0 && _process is { HasExited: false };

    /// <summary>Attach to the first process matching the configured name. False when not running.</summary>
    public bool TryAttach()
    {
        if (IsAttached) return true;
        Detach();
        var p = Process.GetProcessesByName(_processName).FirstOrDefault();
        if (p is null) return false;
        nint h = OpenProcess(ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, p.Id);
        if (h == 0) return false;
        _process = p;
        _handle = h;
        return true;
    }

    public bool Read(uint va, Span<byte> buffer)
    {
        if (!IsAttached) return false;
        byte[] tmp = new byte[buffer.Length];
        if (!ReadProcessMemory(_handle, (nint)va, tmp, tmp.Length, out nint read) || read != tmp.Length)
            return false;
        tmp.CopyTo(buffer);
        return true;
    }

    public bool Write(uint va, ReadOnlySpan<byte> buffer)
    {
        if (!IsAttached) return false;
        byte[] tmp = buffer.ToArray();
        return WriteProcessMemory(_handle, (nint)va, tmp, tmp.Length, out nint written) && written == tmp.Length;
    }

    public void Detach()
    {
        if (_handle != 0) CloseHandle(_handle);
        _handle = 0;
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Detach();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint baseAddress, byte[] buffer, int size, out nint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint process, nint baseAddress, byte[] buffer, int size, out nint bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
