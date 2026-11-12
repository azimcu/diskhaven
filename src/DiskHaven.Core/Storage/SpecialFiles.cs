using System.Runtime.InteropServices;

namespace DiskHaven.Core.Storage;

/// <summary>
/// Tells regular files from named pipes, sockets and device files. .NET shows all of them as ordinary files
/// (FileInfo, length 0), and opening a named pipe for reading waits forever for a writer - a copy, a download or
/// a ZIP that met one would hang and could not even be canceled. Disks from Linux machines (home folder backups)
/// do contain such entries. Uses statx, whose layout is the same on every Linux architecture.
/// </summary>
public static class SpecialFiles
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const int ModeOffset = 28;   // struct statx: stx_mode (u16)
    private const int TypeMask = 0xF000, Regular = 0x8000, Directory = 0x4000, Symlink = 0xA000;

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);

    private static bool _unavailable = !OperatingSystem.IsLinux();

    /// <summary>
    /// True when the path is a pipe, socket or device. False for regular files, folders, symbolic links (not
    /// followed), and paths that do not exist or cannot be checked (opening those then fails the normal way).
    /// </summary>
    public static bool IsSpecial(string path)
    {
        if (_unavailable)
            return false;
        var buffer = new byte[256];
        try
        {
            if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType, buffer) != 0)
                return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return false;
        }
        var type = BitConverter.ToUInt16(buffer, ModeOffset) & TypeMask;
        return type is not (Regular or Directory or Symlink);
    }

    /// <summary>Throws before a special file is opened.</summary>
    public static void EnsureNotSpecial(string path)
    {
        if (IsSpecial(path))
            throw new SpecialFileException();
    }
}

/// <summary>The item is a named pipe, socket or device: it holds no data to copy or open.</summary>
public sealed class SpecialFileException() : IOException("This is a special file (pipe, socket or device), not a file with data; it was skipped.");
