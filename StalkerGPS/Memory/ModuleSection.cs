namespace StalkerGPS.Memory;

public readonly record struct ModuleSection(string Name, nint Address, int Size, uint Characteristics)
{
    private const uint MemExecute = 0x20000000;
    private const uint MemWrite = 0x80000000;

    /// <summary>Writable, non-executable sections (e.g. .data) hold the engine's global variables.</summary>
    public bool IsWritableData => (Characteristics & MemWrite) != 0 && (Characteristics & MemExecute) == 0;
}
