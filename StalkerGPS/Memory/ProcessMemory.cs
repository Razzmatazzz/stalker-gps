using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StalkerGPS.Memory;

/// <summary>
/// Read-only access to another process's memory via ReadProcessMemory.
/// </summary>
public sealed partial class ProcessMemory : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private nint _handle;

    public Process Process { get; }
    public nint ModuleBase { get; }

    private ProcessMemory(Process process, nint handle, nint moduleBase)
    {
        Process = process;
        _handle = handle;
        ModuleBase = moduleBase;
    }

    /// <summary>Opens the first running process matching one of <paramref name="processNames"/> (without ".exe").</summary>
    public static ProcessMemory? TryOpen(IEnumerable<string> processNames, out string? error)
    {
        error = null;
        var process = processNames
            .SelectMany(Process.GetProcessesByName)
            .FirstOrDefault();
        if (process is null)
        {
            error = "Game process not found.";
            return null;
        }

        var handle = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, process.Id);
        if (handle == 0)
        {
            error = $"OpenProcess failed (Win32 error {Marshal.GetLastPInvokeError()}). Try running as administrator.";
            return null;
        }

        nint moduleBase;
        try
        {
            moduleBase = process.MainModule?.BaseAddress ?? 0;
        }
        catch (Exception ex)
        {
            CloseHandle(handle);
            error = $"Could not read the game's module base: {ex.Message}";
            return null;
        }

        return new ProcessMemory(process, handle, moduleBase);
    }

    public bool HasExited
    {
        get
        {
            try { return Process.HasExited; }
            catch { return true; }
        }
    }

    public bool TryRead(nint address, byte[] buffer)
    {
        return ReadProcessMemory(_handle, address, buffer, buffer.Length, out var read) && read == buffer.Length;
    }

    /// <summary>Reads <paramref name="size"/> bytes, or returns null if any part is unreadable.</summary>
    public byte[]? ReadBytes(nint address, int size)
    {
        var buffer = new byte[size];
        return TryRead(address, buffer) ? buffer : null;
    }

    public long ReadInt64(nint address) => ReadBytes(address, 8) is { } b ? BitConverter.ToInt64(b) : 0;

    public int ReadInt32(nint address) => ReadBytes(address, 4) is { } b ? BitConverter.ToInt32(b) : 0;

    public nint ReadPointer(nint address) => (nint)ReadInt64(address);

    public bool TryReadPointer(nint address, out nint value)
    {
        value = ReadPointer(address);
        return value != 0;
    }

    /// <summary>Lists the main module's PE sections (absolute address, size, characteristics).</summary>
    public IReadOnlyList<ModuleSection> GetSections()
    {
        var header = ReadBytes(ModuleBase, 0x1000);
        if (header is null)
            return [];

        int pe = BitConverter.ToInt32(header, 0x3C);
        int count = BitConverter.ToUInt16(header, pe + 6);
        int first = pe + 24 + BitConverter.ToUInt16(header, pe + 20);
        var sections = new List<ModuleSection>(count);
        for (int i = 0; i < count; i++)
        {
            int o = first + i * 40;
            sections.Add(new ModuleSection(
                System.Text.Encoding.ASCII.GetString(header, o, 8).TrimEnd('\0'),
                ModuleBase + BitConverter.ToInt32(header, o + 12),
                BitConverter.ToInt32(header, o + 8),
                BitConverter.ToUInt32(header, o + 36)));
        }
        return sections;
    }

    /// <summary>
    /// Resolves a Cheat Engine style pointer chain: <c>[[[module + baseOffset] + o0] + o1] ... + oLast</c>.
    /// Returns the final address (the last offset is added, not dereferenced).
    /// </summary>
    public bool TryResolveChain(long baseOffset, IReadOnlyList<long> offsets, out nint address)
    {
        address = 0;
        if (!TryReadPointer(ModuleBase + (nint)baseOffset, out var current))
            return false;

        for (var i = 0; i < offsets.Count - 1; i++)
        {
            if (!TryReadPointer(current + (nint)offsets[i], out current))
                return false;
        }

        address = offsets.Count > 0 ? current + (nint)offsets[^1] : current;
        return true;
    }

    /// <summary>Reads three consecutive doubles (UE5 FVector, which uses doubles).</summary>
    public bool TryReadVector3d(nint address, out (double X, double Y, double Z) vector)
    {
        var buffer = new byte[24];
        if (TryRead(address, buffer))
        {
            vector = (BitConverter.ToDouble(buffer, 0), BitConverter.ToDouble(buffer, 8), BitConverter.ToDouble(buffer, 16));
            return true;
        }
        vector = default;
        return false;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
        Process.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(nint process, nint baseAddress, [Out] byte[] buffer, nint size, out nint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
