using System.Text.Json;

namespace HomeVault.Core.Storage;

/// <summary>
/// Changes to the vault. Every operation either completes or leaves the original in place:
/// copies are written to a temporary file first and renamed into place, deletes go to the
/// recycle bin (<c>.homevault/trash</c>) instead of being removed.
/// </summary>
public sealed class FileOperations(VaultRoot root, TimeProvider clock, IVaultChangeListener? listener = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public VaultRoot Root { get; } = root;
    private string Internal => Path.Combine(Root.FullPath, VaultRoot.InternalFolderName);
    private string TrashDir => Path.Combine(Internal, "trash");
    private string TempDir => Path.Combine(Internal, "tmp");

    public string CreateFolder(string? parent, string name)
    {
        var dir = ExistingFolder(parent);
        name = FileNames.Validate(name);
        if (FileNames.Exists(dir, name))
            throw new VaultConflictException($"'{name}' already exists.");
        Directory.CreateDirectory(Path.Combine(dir, name));
        listener?.Added(Path.Combine(dir, name));
        return Root.ToRelative(Path.Combine(dir, name));
    }

    public string Rename(string path, string newName)
    {
        var full = ExistingEntry(path);
        newName = FileNames.Validate(newName);
        var parent = Path.GetDirectoryName(full)!;
        var target = Path.Combine(parent, newName);
        var caseOnly = Path.GetFileName(full).Equals(newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && FileNames.Exists(parent, newName))
            throw new VaultConflictException($"'{newName}' already exists.");
        listener?.Removing(full);
        MoveEntry(full, target);
        listener?.Added(target);
        return Root.ToRelative(target);
    }

    /// <summary>Moves entries into a folder. Name clashes are resolved by keeping both ("name (2)").</summary>
    public IReadOnlyList<string> Move(IEnumerable<string> paths, string? destination)
    {
        var dest = ExistingFolder(destination);
        var result = new List<string>();
        foreach (var path in paths)
        {
            var full = ExistingEntry(path);
            if (Path.GetDirectoryName(full) == dest)
            {
                result.Add(Root.ToRelative(full));
                continue;
            }
            EnsureNotInside(full, dest);
            var target = Path.Combine(dest, FileNames.Unique(dest, Path.GetFileName(full)));
            listener?.Removing(full);
            MoveEntry(full, target);
            listener?.Added(target);
            result.Add(Root.ToRelative(target));
        }
        return result;
    }

    /// <summary>Copies entries into a folder. Copying into the same folder creates "name - Kopya".</summary>
    public IReadOnlyList<string> Copy(IEnumerable<string> paths, string? destination)
    {
        var dest = ExistingFolder(destination);
        var result = new List<string>();
        foreach (var path in paths)
        {
            var full = ExistingEntry(path);
            EnsureNotInside(full, dest);
            var name = Path.GetFileName(full);
            if (Path.GetDirectoryName(full) == dest)
                name = Directory.Exists(full)
                    ? $"{name} - Kopya"
                    : $"{Path.GetFileNameWithoutExtension(name)} - Kopya{Path.GetExtension(name)}";
            var target = Path.Combine(dest, FileNames.Unique(dest, name));
            if (Directory.Exists(full))
                CopyDirectory(full, target);
            else
                CopyFile(full, target);
            listener?.Added(target);
            result.Add(Root.ToRelative(target));
        }
        return result;
    }

    /// <summary>Moves entries to the recycle bin.</summary>
    public IReadOnlyList<TrashItem> Delete(IEnumerable<string> paths)
    {
        var items = new List<TrashItem>();
        foreach (var path in paths)
        {
            var full = ExistingEntry(path);
            var id = Guid.NewGuid().ToString("N");
            var slot = Path.Combine(TrashDir, id);
            Directory.CreateDirectory(slot);
            var isDir = Directory.Exists(full);
            var item = new TrashItem(id, Path.GetFileName(full), Root.ToRelative(full),
                isDir ? EntryKind.Folder : EntryKind.File,
                isDir ? DirectorySize(full) : new FileInfo(full).Length,
                clock.GetUtcNow());
            File.WriteAllText(Path.Combine(slot, "info.json"), JsonSerializer.Serialize(item, Json));
            listener?.Removing(full);
            MoveEntry(full, Path.Combine(slot, "item"));
            items.Add(item);
        }
        return items;
    }

    public IReadOnlyList<TrashItem> ListTrash()
    {
        if (!Directory.Exists(TrashDir))
            return [];
        return Directory.EnumerateDirectories(TrashDir)
            .Select(ReadTrashInfo)
            .OfType<TrashItem>()
            .OrderByDescending(i => i.DeletedAt)
            .ToList();
    }

    /// <summary>Puts an item back where it was. Missing parent folders are recreated; clashes keep both.</summary>
    public string Restore(string id)
    {
        var (slot, item) = TrashSlot(id);
        var originalParent = Root.Resolve(Path.GetDirectoryName(item.OriginalPath)?.Replace('\\', '/'));
        // remember the highest folder that has to be recreated, so folder sizes count it
        string? createdTop = null;
        for (var d = originalParent; d.Length > Root.FullPath.Length && !Directory.Exists(d); d = Path.GetDirectoryName(d)!)
            createdTop = d;
        Directory.CreateDirectory(originalParent);
        var target = Path.Combine(originalParent, FileNames.Unique(originalParent, item.Name));
        MoveEntry(Path.Combine(slot, "item"), target);
        listener?.Added(createdTop ?? target);
        Directory.Delete(slot, recursive: true);
        return Root.ToRelative(target);
    }

    /// <summary>Permanently removes one item from the recycle bin.</summary>
    public void Purge(string id)
    {
        var (slot, _) = TrashSlot(id);
        Directory.Delete(slot, recursive: true);
    }

    /// <summary>Permanently removes recycle bin items older than <paramref name="age"/>. Returns how many.</summary>
    public int PurgeOlderThan(TimeSpan age)
    {
        var cutoff = clock.GetUtcNow() - age;
        var old = ListTrash().Where(i => i.DeletedAt < cutoff).ToList();
        foreach (var item in old)
            Purge(item.Id);
        return old.Count;
    }

    private (string Slot, TrashItem Item) TrashSlot(string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
            throw new VaultPathException("Invalid recycle bin id.");
        var slot = Path.Combine(TrashDir, id);
        return ReadTrashInfo(slot) is { } item ? (slot, item) : throw new FileNotFoundException("Item is not in the recycle bin.");
    }

    private static TrashItem? ReadTrashInfo(string slot)
    {
        var info = Path.Combine(slot, "info.json");
        if (!File.Exists(info))
            return null;
        try { return JsonSerializer.Deserialize<TrashItem>(File.ReadAllText(info)); }
        catch (JsonException) { return null; }
    }

    private string ExistingFolder(string? path)
    {
        var full = Root.Resolve(path);
        return Directory.Exists(full) ? full : throw new DirectoryNotFoundException("Folder not found.");
    }

    private string ExistingEntry(string path)
    {
        var full = Root.Resolve(path);
        if (full == Root.FullPath)
            throw new VaultPathException("The root folder cannot be changed.");
        return File.Exists(full) || Directory.Exists(full) ? full : throw new FileNotFoundException("Item not found.");
    }

    private static void EnsureNotInside(string source, string destination)
    {
        if (Directory.Exists(source) &&
            (destination == source || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new VaultConflictException("A folder cannot be moved or copied into itself.");
    }

    private static void MoveEntry(string source, string target)
    {
        if (Directory.Exists(source))
            Directory.Move(source, target);
        else
            File.Move(source, target);
    }

    private void CopyFile(string source, string target)
    {
        Directory.CreateDirectory(TempDir);
        var temp = Path.Combine(TempDir, Guid.NewGuid().ToString("N"));
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
            File.Move(temp, target);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source))
            if (new DirectoryInfo(dir).LinkTarget is null)
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        foreach (var file in Directory.EnumerateFiles(source))
            if (new FileInfo(file).LinkTarget is null)
                CopyFile(file, Path.Combine(target, Path.GetFileName(file)));
        Directory.SetLastWriteTimeUtc(target, Directory.GetLastWriteTimeUtc(source));
    }

    private static long DirectorySize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
}
