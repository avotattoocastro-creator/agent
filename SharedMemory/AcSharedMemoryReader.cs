using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace AvoTelemetryAgent.SharedMemory;

/// <summary>
/// Opens the three Assetto Corsa memory-mapped files and reads their contents
/// into the corresponding page-file structs.  All reads are done via a raw
/// pointer copy so fixed-buffer structs are handled correctly.
/// </summary>
public sealed class AcSharedMemoryReader
{
    private const string PhysicsMapName  = "acpmf_physics";
    private const string GraphicsMapName = "acpmf_graphics";
    private const string StaticMapName   = "acpmf_static";

    public bool IsConnected { get; private set; }

    public unsafe SPageFilePhysics ReadPhysics()
        => ReadPage<SPageFilePhysics>(PhysicsMapName);

    public unsafe SPageFileGraphics ReadGraphics()
        => ReadPage<SPageFileGraphics>(GraphicsMapName);

    public unsafe SPageFileStatic ReadStatic()
        => ReadPage<SPageFileStatic>(StaticMapName);

    private static unsafe T ReadPage<T>(string name) where T : unmanaged
    {
        try
        {
            using var mmf      = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, sizeof(T), MemoryMappedFileAccess.Read);

            T result = default;
            byte* ptr = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try
            {
                result = *(T*)ptr;
            }
            finally
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            return result;
        }
        catch
        {
            // AC not running or page not yet created.
            return default;
        }
    }

    /// <summary>
    /// Returns true when all three shared-memory pages can be opened.
    /// </summary>
    public bool CheckConnected()
    {
        IsConnected = CanOpen(PhysicsMapName)
                   && CanOpen(GraphicsMapName)
                   && CanOpen(StaticMapName);
        return IsConnected;
    }

    private static bool CanOpen(string name)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Reads a null-terminated wchar_t string from a fixed char buffer.</summary>
    public static unsafe string ReadWString(char* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != '\0') len++;
        return new string(ptr, 0, len);
    }
}
