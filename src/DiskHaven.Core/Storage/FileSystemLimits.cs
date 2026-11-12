using System.Runtime.InteropServices;

namespace DiskHaven.Core.Storage;

public enum FileSystemKind { Unknown, Fat, ExFat, Ntfs, Other }

/// <summary>
/// Limits of the file system a folder lives on. The important one is FAT32: it cannot hold a file of
/// 4 GB or more, and .NET reports that as a bare <see cref="ArgumentException"/>, not an
/// <see cref="IOException"/>. Checking before the first byte is written gives every caller the same,
/// understandable message (and the user interface translates it).
/// </summary>
public static class FileSystemLimits
{
    /// <summary>Largest file a FAT32 file system can hold: 2^32 - 1 bytes.</summary>
    public const long Fat32MaxFileSize = (4L << 30) - 1;

    public const string TooLargeMessage =
        "The file is larger than this disk's file system allows (FAT32: at most 4 GB in one file).";

    // linux/magic.h
    private const long MsdosMagic = 0x4d44;        // vfat, fat, msdos, umsdos
    private const long ExfatMagic = 0x2011BAB0;
    private const long NtfsMagic = 0x5346544e;     // 'NTFS'

    [DllImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static extern int statfs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    /// <summary>The file system <paramref name="path"/> is on. <see cref="FileSystemKind.Unknown"/> means "do not assume a limit".</summary>
    public static FileSystemKind Detect(string path)
    {
        if (OperatingSystem.IsLinux() && TryStatFs(path) is { } magic)
            return Classify(magic);
        try { return ClassifyName(new DriveInfo(path).DriveFormat); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return FileSystemKind.Unknown; }
    }

    /// <summary>Throws a clear <see cref="IOException"/> when <paramref name="size"/> cannot fit on the file system of <paramref name="targetDirectory"/>.</summary>
    public static void EnsureFileFits(string targetDirectory, long size) => EnsureFileFits(Detect(targetDirectory), size);

    public static void EnsureFileFits(FileSystemKind kind, long size)
    {
        if (kind == FileSystemKind.Fat && size > Fat32MaxFileSize)
            throw new IOException(TooLargeMessage);
    }

    /// <summary>
    /// True only for the exception .NET throws when a write would make a file longer than the file system
    /// allows ("Specified file length was too large for the file system", from SetLength/preallocation).
    /// A narrow test on purpose: a general ArgumentException can mean a real bug and must not be hidden.
    /// </summary>
    public static bool IsFileTooLarge(ArgumentException e) =>
        e.ParamName == "value" && e.Message.Contains("too large", StringComparison.OrdinalIgnoreCase);

    private static long? TryStatFs(string path)
    {
        var buffer = Marshal.AllocHGlobal(512);   // struct statfs is at most ~120 bytes on 64-bit Linux
        try
        {
            // f_type is at offset 0 in every Linux ABI
            return statfs(path, buffer) == 0 ? Marshal.ReadInt64(buffer) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static FileSystemKind Classify(long magic) => magic switch
    {
        MsdosMagic => FileSystemKind.Fat,
        ExfatMagic => FileSystemKind.ExFat,
        NtfsMagic => FileSystemKind.Ntfs,
        _ => FileSystemKind.Other,
    };

    private static FileSystemKind ClassifyName(string format) => format.Trim().ToLowerInvariant() switch
    {
        "vfat" or "fat" or "fat32" or "msdos" or "umsdos" or "msdosfs" => FileSystemKind.Fat,
        "exfat" => FileSystemKind.ExFat,
        "ntfs" or "ntfs3" => FileSystemKind.Ntfs,
        "" => FileSystemKind.Unknown,
        _ => FileSystemKind.Other,
    };
}
