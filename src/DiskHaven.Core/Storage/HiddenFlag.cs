using System.Runtime.InteropServices;

namespace DiskHaven.Core.Storage;

/// <summary>
/// The "Hidden" attribute of Windows, kept on Linux as an extended attribute (user.diskhaven.hidden) of the file
/// itself, so it follows the item when it is renamed or moved. Names starting with a dot are always hidden.
/// On file systems without extended attributes (exFAT, FAT) hiding is not available.
/// </summary>
public static class HiddenFlag
{
    private const string Name = "user.diskhaven.hidden";
    private static readonly byte[] Value = "1"u8.ToArray();

    public static bool IsHidden(FileSystemInfo info)
    {
        if (info.Name.StartsWith('.'))
            return true;
        if (OperatingSystem.IsWindows())
            return info.Attributes.HasFlag(FileAttributes.Hidden);
        try { return lgetxattr(info.FullName, Name, IntPtr.Zero, 0) > 0; }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
    }

    /// <summary>Sets or clears the flag. Throws <see cref="IOException"/> when the disk cannot store it.</summary>
    public static void Set(string fullPath, bool hidden)
    {
        if (OperatingSystem.IsWindows())
        {
            var a = File.GetAttributes(fullPath);
            File.SetAttributes(fullPath, hidden ? a | FileAttributes.Hidden : a & ~FileAttributes.Hidden);
            return;
        }
        if (hidden)
        {
            if (lsetxattr(fullPath, Name, Value, (nuint)Value.Length, 0) != 0)
                throw new IOException(Marshal.GetLastPInvokeError() == 95 /* EOPNOTSUPP */
                    ? "Hiding is not supported on this disk."
                    : $"Could not hide the item (error {Marshal.GetLastPInvokeError()}).");
        }
        else if (lremovexattr(fullPath, Name) != 0 && Marshal.GetLastPInvokeError() is not (61 or 95)) // ENODATA, EOPNOTSUPP
            throw new IOException($"Could not unhide the item (error {Marshal.GetLastPInvokeError()}).");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint lgetxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, nuint size);

    [DllImport("libc", SetLastError = true)]
    private static extern int lsetxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nuint size, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int lremovexattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
