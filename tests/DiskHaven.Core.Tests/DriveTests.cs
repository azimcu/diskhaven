using System.Security.Cryptography;
using DiskHaven.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace DiskHaven.Core.Tests;

/// <summary>Read-only drives, DiskHaven's reserved names, and verified transfers between two drives.</summary>
public sealed class DriveTests : IDisposable
{
    private readonly string _a = Directory.CreateTempSubdirectory("diskhaven-drive-a-").FullName;
    private readonly string _b = Directory.CreateTempSubdirectory("diskhaven-drive-b-").FullName;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DriveTests()
    {
        Directory.CreateDirectory(A("Fotoğraflar/2025/Tatil"));
        File.WriteAllBytes(A("Fotoğraflar/2025/Tatil/plaj.jpg"), RandomBytes(300_000));
        File.WriteAllBytes(A("Fotoğraflar/2025/kapak.png"), RandomBytes(5_000));
        File.WriteAllText(A("Fotoğraflar/.gizli-not"), "gizli");
        File.WriteAllBytes(A("Fotoğraflar/boş.txt"), []);
        Directory.CreateDirectory(A("Fotoğraflar/Boş Klasör"));
        File.WriteAllText(A("tek dosya.txt"), "merhaba");
        File.SetLastWriteTimeUtc(A("tek dosya.txt"), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Directory.CreateDirectory(B("Hedef"));
    }

    public void Dispose()
    {
        // rm, not Directory.Delete: .NET cannot delete names that are not valid UTF-8
        foreach (var d in new[] { _a, _b })
            if (Directory.Exists(d))
                System.Diagnostics.Process.Start("rm", ["-rf", "--", d])!.WaitForExit();
    }

    private string A(string rel) => Path.Combine(_a, rel);
    private string B(string rel) => Path.Combine(_b, rel);
    private static byte[] RandomBytes(int n) { var b = new byte[n]; Random.Shared.NextBytes(b); return b; }
    private static string Hash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    /// <summary>Every file below a folder with its content hash, relative paths with '/'.</summary>
    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), Hash);

    // ---- reserved names ----------------------------------------------------------------------

    [Theory]
    [InlineData(".diskhaven")]
    [InlineData(".DiskHaven")]
    [InlineData(".homevault")]
    [InlineData(".diskhaven-disk.json")]
    [InlineData(".diskhaven-trash")]
    [InlineData(".diskhaven-tmp")]
    [InlineData(".diskhaven-data")]
    [InlineData(".homevault-trash")]
    public void Backup_disk_names_are_reserved(string name)
    {
        Assert.True(VaultRoot.IsInternalName(name));
        Assert.Throws<VaultPathException>(() => VaultRoot.Normalize(name));
        Assert.Throws<VaultPathException>(() => VaultRoot.Normalize("Klasör/" + name + "/x"));
    }

    [Theory]
    [InlineData(".diskhavenx")]
    [InlineData("diskhaven-trash")]
    [InlineData(".git")]
    [InlineData(".gizli-not")]
    public void Ordinary_dot_names_are_not_reserved(string name) => Assert.False(VaultRoot.IsInternalName(name));

    [Fact]
    public void Reserved_names_on_a_disk_root_are_not_listed()
    {
        Directory.CreateDirectory(B(".diskhaven-trash/2026-01-01_000000"));
        Directory.CreateDirectory(B(".diskhaven-data/hideaway"));
        File.WriteAllText(B(".diskhaven-disk.json"), "{}");
        var names = new FileBrowser(new VaultRoot(_b)).List("").Select(e => e.Name).ToList();
        Assert.Equal(["Hedef"], names);
    }

    // ---- disk settings -----------------------------------------------------------------------

    [Fact]
    public void Settings_saved_before_read_only_existed_stay_writable_backup_disks()
    {
        File.WriteAllText(Path.Combine(_b, "disks.json"), """
            [{"FsId":"ABCD-1234","DiskId":"d1","Name":"Eski yedek","Folders":["Belgeler"],
              "Schedule":{"Enabled":true,"Time":"03:00","LastRun":null},"After":"PowerOff","LastSync":null}]
            """);
        var store = new DiskHaven.Core.Sync.DiskSettingsStore(_b);
        var s = Assert.Single(store.All());
        Assert.False(s.ReadOnly);
        Assert.Equal(["Belgeler"], s.Folders);
        store.Save(new DiskHaven.Core.Sync.DiskSettings("NEW-1", "", "Yeni", [], DiskHaven.Core.Sync.SyncSchedule.Off,
            DiskHaven.Core.Sync.AfterSync.StayOn, null, ReadOnly: true));
        Assert.True(store.Get("NEW-1")!.ReadOnly);
        Assert.False(store.Get("ABCD-1234")!.ReadOnly);
    }

    // ---- read-only drives -------------------------------------------------------------------

    [Fact]
    public void Read_only_drive_refuses_every_change_and_stays_byte_identical()
    {
        File.WriteAllText(A("Fotoğraflar/silinecek.txt"), "x");
        var before = Snapshot(_a);
        var root = new VaultRoot(_a, readOnly: true);
        var ops = new FileOperations(root, _clock);
        Assert.Throws<VaultReadOnlyException>(() => ops.CreateFolder("", "Yeni"));
        Assert.Throws<VaultReadOnlyException>(() => ops.Rename("tek dosya.txt", "b.txt"));
        Assert.Throws<VaultReadOnlyException>(() => ops.Move(["tek dosya.txt"], "Fotoğraflar"));
        Assert.Throws<VaultReadOnlyException>(() => ops.Copy(["tek dosya.txt"], "Fotoğraflar"));
        Assert.Throws<VaultReadOnlyException>(() => ops.Delete(["Fotoğraflar/silinecek.txt"]));
        Assert.Throws<VaultReadOnlyException>(() => ops.Restore(new string('a', 32)));
        Assert.Throws<VaultReadOnlyException>(() => ops.Purge(new string('a', 32)));
        Assert.Throws<VaultReadOnlyException>(() => ops.PurgeOlderThan(TimeSpan.Zero));
        var archives = new Archives(root);
        Assert.Throws<VaultReadOnlyException>(() => archives.Compress(["tek dosya.txt"], null, Ct));
        Assert.Throws<VaultReadOnlyException>(() => archives.Extract("x.zip", long.MaxValue, Ct));
        Assert.Equal(before, Snapshot(_a));
        Assert.False(Directory.Exists(A(".diskhaven")));
    }

    [Fact]
    public async Task Read_only_drive_refuses_uploads()
    {
        var file = Path.Combine(_b, "gelen.bin");
        File.WriteAllBytes(file, RandomBytes(100));
        var committer = new UploadCommitter(new VaultRoot(_a, readOnly: true));
        await Assert.ThrowsAsync<VaultReadOnlyException>(() => committer.CommitAsync(file, "", "a.bin", Hash(file), null, Ct));
        Assert.False(File.Exists(A("a.bin")));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Read_only_drive_can_still_be_browsed_and_read()
    {
        var b = new FileBrowser(new VaultRoot(_a, readOnly: true));
        Assert.Contains(b.List("Fotoğraflar"), e => e.Name == "2025");
        Assert.Equal(A("tek dosya.txt"), b.GetFile("tek dosya.txt"));
    }

    [Fact]
    public void Read_only_root_does_not_rename_the_legacy_folder()
    {
        Directory.CreateDirectory(A(".homevault/trash"));
        _ = new VaultRoot(_a, readOnly: true);
        Assert.True(Directory.Exists(A(".homevault")));
        Assert.False(Directory.Exists(A(".diskhaven")));
    }

    // ---- uploads to another drive -----------------------------------------------------------

    [Fact]
    public async Task Upload_received_on_another_disk_is_staged_verified_and_placed()
    {
        var received = A("gelen-yukleme");   // the main disk's upload folder, outside drive B
        var data = RandomBytes(2_000_000);
        File.WriteAllBytes(received, data);
        var sha = Hash(received);
        var r = await new UploadCommitter(new VaultRoot(_b)).CommitAsync(received, "Hedef", "Klasör/video.mp4", sha, null, Ct);
        Assert.Equal("Hedef/Klasör/video.mp4", r.Path);
        Assert.Equal(sha, Hash(B("Hedef/Klasör/video.mp4")));
        Assert.Empty(Directory.EnumerateFiles(B(".diskhaven/tmp")));   // no leftovers
    }

    [Fact]
    public async Task Upload_with_wrong_checksum_never_reaches_the_other_disk()
    {
        var received = A("gelen-yukleme");
        File.WriteAllBytes(received, RandomBytes(1000));
        await Assert.ThrowsAsync<ChecksumMismatchException>(() =>
            new UploadCommitter(new VaultRoot(_b)).CommitAsync(received, "Hedef", "a.bin", new string('0', 64), null, Ct));
        Assert.Empty(Directory.EnumerateFiles(B("Hedef")));
        Assert.False(Directory.Exists(B(".diskhaven/tmp")) && Directory.EnumerateFiles(B(".diskhaven/tmp")).Any());
    }

    // ---- transfers between drives -----------------------------------------------------------

    private Task<TransferReport> Run(IReadOnlyList<string> paths, string dest, bool move, VaultRoot? src = null, VaultRoot? dst = null,
        CancellationToken? ct = null, IProgress<TransferProgress>? progress = null) =>
        new DriveTransfer(_clock).RunAsync(src ?? new VaultRoot(_a), paths, dst ?? new VaultRoot(_b), dest, move, progress, ct ?? Ct);

    [Fact]
    public async Task Copy_folder_to_another_drive_is_identical_and_source_untouched()
    {
        var before = Snapshot(A("Fotoğraflar"));
        var r = await Run(["Fotoğraflar"], "Hedef", move: false);
        Assert.True(r.Success);
        Assert.Equal(4, r.FilesCopied);
        Assert.Equal(before, Snapshot(B("Hedef/Fotoğraflar")));
        Assert.Equal(before, Snapshot(A("Fotoğraflar")));
        Assert.True(Directory.Exists(B("Hedef/Fotoğraflar/Boş Klasör")));   // empty folders come along
        Assert.Empty(Directory.EnumerateFiles(B(".diskhaven/tmp")));
    }

    [Fact]
    public async Task Copy_keeps_file_times()
    {
        await Run(["tek dosya.txt"], "Hedef", move: false);
        Assert.Equal(File.GetLastWriteTimeUtc(A("tek dosya.txt")), File.GetLastWriteTimeUtc(B("Hedef/tek dosya.txt")));
    }

    [Fact]
    public async Task Existing_names_at_destination_are_never_overwritten()
    {
        File.WriteAllText(B("Hedef/tek dosya.txt"), "ESKİ, DEĞERLİ");
        var r = await Run(["tek dosya.txt"], "Hedef", move: false);
        Assert.Equal("ESKİ, DEĞERLİ", File.ReadAllText(B("Hedef/tek dosya.txt")));
        Assert.Equal("merhaba", File.ReadAllText(B("Hedef/tek dosya (2).txt")));
        Assert.Equal(["Hedef/tek dosya (2).txt"], r.Created);
    }

    [Fact]
    public async Task Move_sends_source_to_its_own_recycle_bin_after_verification()
    {
        var before = Snapshot(A("Fotoğraflar"));
        var r = await Run(["Fotoğraflar"], "Hedef", move: true);
        Assert.True(r.Success);
        Assert.Equal(1, r.SourcesMovedToTrash);
        Assert.False(Directory.Exists(A("Fotoğraflar")));
        Assert.Equal(before, Snapshot(B("Hedef/Fotoğraflar")));
        // recoverable from drive A's recycle bin
        var ops = new FileOperations(new VaultRoot(_a), _clock);
        var item = Assert.Single(ops.ListTrash());
        ops.Restore(item.Id);
        Assert.Equal(before, Snapshot(A("Fotoğraflar")));
    }

    [Fact]
    public async Task Move_from_read_only_drive_is_refused_before_anything_is_written()
    {
        await Assert.ThrowsAsync<VaultReadOnlyException>(() => Run(["Fotoğraflar"], "Hedef", move: true, src: new VaultRoot(_a, readOnly: true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(B("Hedef")));
    }

    [Fact]
    public async Task Copy_from_read_only_drive_works()
    {
        var r = await Run(["Fotoğraflar"], "Hedef", move: false, src: new VaultRoot(_a, readOnly: true));
        Assert.True(r.Success);
        Assert.Equal(Snapshot(A("Fotoğraflar")), Snapshot(B("Hedef/Fotoğraflar")));
    }

    [Fact]
    public async Task Copy_to_read_only_drive_is_refused() =>
        await Assert.ThrowsAsync<VaultReadOnlyException>(() => Run(["Fotoğraflar"], "Hedef", move: false, dst: new VaultRoot(_b, readOnly: true)));

    [Fact]
    public async Task Folder_into_itself_is_refused_on_the_same_drive()
    {
        var root = new VaultRoot(_a);
        await Assert.ThrowsAsync<VaultConflictException>(() => Run(["Fotoğraflar"], "Fotoğraflar/2025", move: false, src: root, dst: root));
    }

    [Fact]
    public async Task Root_and_traversal_are_refused()
    {
        await Assert.ThrowsAsync<VaultPathException>(() => Run([""], "Hedef", move: true));
        await Assert.ThrowsAsync<VaultPathException>(() => Run(["../x"], "Hedef", move: false));
        await Assert.ThrowsAsync<VaultPathException>(() => Run(["tek dosya.txt"], "../", move: false));
        await Assert.ThrowsAsync<VaultPathException>(() => Run([".diskhaven"], "Hedef", move: false));
    }

    [Fact]
    public async Task Symbolic_links_are_skipped_not_followed()
    {
        var secret = Path.Combine(Path.GetTempPath(), "diskhaven-secret-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(secret, "outside");
        try
        {
            File.CreateSymbolicLink(A("Fotoğraflar/link.txt"), secret);
            var r = await Run(["Fotoğraflar"], "Hedef", move: false);
            Assert.False(File.Exists(B("Hedef/Fotoğraflar/link.txt")));
            Assert.Contains(r.Skipped, s => s.Contains("link.txt"));
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public async Task Cancel_keeps_the_source_and_leaves_no_temp_files()
    {
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgressCanceler(cts, afterFiles: 1);
        var r = await Run(["Fotoğraflar"], "Hedef", move: true, ct: cts.Token, progress: progress);
        Assert.True(r.Canceled);
        Assert.Equal(0, r.SourcesMovedToTrash);
        Assert.Equal(4, Snapshot(A("Fotoğraflar")).Count);
        Assert.Empty(Directory.EnumerateFiles(B(".diskhaven/tmp")));
    }

    private static void MakeFifo(string path)
    {
        using var p = System.Diagnostics.Process.Start("mkfifo", [path])!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public async Task Named_pipe_is_skipped_and_never_opened()
    {
        // opening a pipe for reading waits for a writer forever: the copy would hang and not even cancel
        MakeFifo(A("Fotoğraflar/boru"));
        var run = Run(["Fotoğraflar"], "Hedef", move: false);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30), Ct)));
        var r = await run;
        Assert.True(r.Success);
        Assert.Contains(r.Skipped, s => s.Contains("boru") && s.Contains("special"));
        Assert.False(File.Exists(B("Hedef/Fotoğraflar/boru")));
        Assert.Equal(4, r.FilesCopied);
    }

    [Fact]
    public async Task Move_keeps_the_original_when_something_inside_could_not_be_copied()
    {
        MakeFifo(A("Fotoğraflar/boru"));
        File.CreateSymbolicLink(A("Fotoğraflar/kısayol"), "2025/kapak.png");
        var r = await Run(["Fotoğraflar"], "Hedef", move: true);
        Assert.Equal(0, r.SourcesMovedToTrash);
        Assert.True(File.Exists(A("Fotoğraflar/2025/Tatil/plaj.jpg")));
        Assert.NotNull(new FileInfo(A("Fotoğraflar/kısayol")).LinkTarget);
        Assert.Contains(r.Errors, e => e.Path == "Fotoğraflar" && e.Message.Contains("original was kept"));
        Assert.Equal(4, r.FilesCopied);
        Assert.Equal(Hash(A("Fotoğraflar/2025/Tatil/plaj.jpg")), Hash(B("Hedef/Fotoğraflar/2025/Tatil/plaj.jpg")));
    }

    [Fact]
    public async Task Named_pipe_cannot_be_opened_downloaded_or_transferred_on_its_own()
    {
        MakeFifo(A("boru"));
        var root = new VaultRoot(_a);
        Assert.Throws<SpecialFileException>(() => new FileBrowser(root).GetFile("boru"));
        await Assert.ThrowsAsync<SpecialFileException>(() => Run(["boru"], "Hedef", move: false));
        Assert.Throws<SpecialFileException>(() => new Archives(root).Compress(["boru"], ct: TestContext.Current.CancellationToken));
        Assert.True(SpecialFiles.IsSpecial(A("boru")));
        Assert.False(SpecialFiles.IsSpecial(A("tek dosya.txt")));
        Assert.False(SpecialFiles.IsSpecial(A("Fotoğraflar")));
        Assert.False(SpecialFiles.IsSpecial(A("yok")));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Unreadable_folder_is_reported_and_everything_else_is_copied()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root (CI) reads every file: an unreadable file cannot be made");
        Directory.CreateDirectory(A("Fotoğraflar/kilitli"));
        File.WriteAllText(A("Fotoğraflar/kilitli/gizli.txt"), "x");
        File.SetUnixFileMode(A("Fotoğraflar/kilitli"), UnixFileMode.None);
        try
        {
            var r = await Run(["Fotoğraflar"], "Hedef", move: true);
            Assert.Contains(r.Errors, e => e.Path == "Fotoğraflar/kilitli");
            Assert.Equal(4, r.FilesCopied);
            Assert.Equal(0, r.SourcesMovedToTrash);   // not everything arrived: the original stays
            Assert.True(File.Exists(A("Fotoğraflar/2025/Tatil/plaj.jpg")));
            Assert.Equal(Hash(A("Fotoğraflar/2025/Tatil/plaj.jpg")), Hash(B("Hedef/Fotoğraflar/2025/Tatil/plaj.jpg")));
        }
        finally { File.SetUnixFileMode(A("Fotoğraflar/kilitli"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Unreadable_file_is_reported_and_the_rest_copied()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root (CI) reads every file: an unreadable file cannot be made");
        File.WriteAllText(A("Fotoğraflar/okunamaz.txt"), "x");
        File.SetUnixFileMode(A("Fotoğraflar/okunamaz.txt"), UnixFileMode.None);
        var r = await Run(["Fotoğraflar"], "Hedef", move: false);
        Assert.Contains(r.Errors, e => e.Path == "Fotoğraflar/okunamaz.txt");
        Assert.Equal(4, r.FilesCopied);
        Assert.False(File.Exists(B("Hedef/Fotoğraflar/okunamaz.txt")));
        Assert.Empty(Directory.EnumerateFiles(B(".diskhaven/tmp")));
    }

    [Fact]
    public async Task Disk_that_disappears_stops_the_job_and_the_source_stays()
    {
        for (var i = 0; i < 5; i++)
            File.WriteAllText(A($"Fotoğraflar/ek{i}.txt"), "x");
        var files = 0;
        var progress = new Progress<TransferProgress>(_ => { });
        // the check says "gone" from the third file on
        var r = await new DriveTransfer(_clock).RunAsync(new VaultRoot(_a), ["tek dosya.txt", "Fotoğraflar"], new VaultRoot(_b), "Hedef",
            move: true, null, Ct, () => ++files > 2 ? "The disk was disconnected" : null);
        Assert.Contains(r.Errors, e => e.Message.Contains("disconnected"));
        Assert.True(File.Exists(A("Fotoğraflar/2025/Tatil/plaj.jpg")));   // the item in progress was not moved
        Assert.True(r.FilesCopied < 9);
        Assert.Empty(Directory.EnumerateFiles(B(".diskhaven/tmp")));
    }

    [Fact]
    public void Stale_temporary_files_are_removed_fresh_ones_kept()
    {
        var tmp = B(".diskhaven/tmp");
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "copy-old"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(tmp, "copy-old"), DateTime.UtcNow.AddHours(-7));
        File.WriteAllText(Path.Combine(tmp, "copy-new"), "y");
        Assert.Equal(1, VerifiedCopy.CleanStale(tmp, TimeSpan.FromHours(6), DateTime.UtcNow));
        Assert.Equal(["copy-new"], Directory.EnumerateFiles(tmp).Select(Path.GetFileName));
        Assert.Equal(0, VerifiedCopy.CleanStale(B("yok"), TimeSpan.FromHours(6), DateTime.UtcNow));
    }

    /// <summary>Creates a file whose name is not valid UTF-8 (ISO-8859-9 bytes), as old systems left them. .NET cannot.</summary>
    // a file name in an old 8-bit code page (Turkish 'ş' = 0xFE), as copied from an old Windows disk; .NET strings cannot
    // express it, so the name is passed to the kernel as raw bytes (no python3 needed: the CI image does not have it)
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int open(byte[] path, int flags, int mode);
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int close(int fd);

    private static void MakeLatin5File(string folder)
    {
        byte[] name = [.. System.Text.Encoding.UTF8.GetBytes(folder + "/eski "), 0xFE, .. "irket.txt"u8, 0];
        var fd = open(name, 0x41 /* O_WRONLY | O_CREAT */, 0x1A4 /* 0644 */);
        Assert.True(fd >= 0, $"open failed: errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        close(fd);
    }

    [Fact]
    public async Task Name_that_is_not_utf8_is_listed_reported_and_never_hides_the_rest()
    {
        MakeLatin5File(A("Fotoğraflar"));
        MakeFifo(A("Fotoğraflar/boru"));
        var list = new FileBrowser(new VaultRoot(_a)).List("Fotoğraflar");
        Assert.Contains(list, e => e.Name == "kapak.png" || e.Name == "2025");
        Assert.Contains(list, e => e.Problem == EntryProblem.BadName);
        Assert.Contains(list, e => e.Name == "boru" && e.Problem == EntryProblem.Special);
        var r = await Run(["Fotoğraflar"], "Hedef", move: true);
        Assert.Contains(r.Errors, e => e.Message.Contains("UTF-8"));
        Assert.Equal(0, r.SourcesMovedToTrash);
        Assert.Equal(4, r.FilesCopied);
    }

    [Fact]
    public async Task Move_keeps_the_original_when_it_changes_after_its_copy()
    {
        Directory.CreateDirectory(A("Değişen"));
        File.WriteAllText(A("Değişen/a.txt"), "eski içerik");
        File.WriteAllText(A("Değişen/b.txt"), new string('b', 500_000));
        // once the first file is done, a new write arrives in the source folder
        var progress = new ChangeAfterFirstFile(() => File.AppendAllText(A("Değişen/a.txt"), "YENİ"));
        var r = await Run(["Değişen"], "Hedef", move: true, progress: progress);
        Assert.Equal(0, r.SourcesMovedToTrash);
        Assert.True(File.Exists(A("Değişen/a.txt")));
        Assert.Contains(r.Errors, e => e.Message.Contains("changed while it was being copied"));
        Assert.Equal("eski içerikYENİ", File.ReadAllText(A("Değişen/a.txt")));
        Assert.Equal("eski içerik", File.ReadAllText(B("Hedef/Değişen/a.txt")));   // the verified copy
    }

    [Fact]
    public async Task Missing_destination_is_refused() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Run(["tek dosya.txt"], "Yok", move: false));

    private sealed class SyncProgressCanceler(CancellationTokenSource cts, int afterFiles) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            if (value.FilesDone >= afterFiles)
                cts.Cancel();
        }
    }

    /// <summary>Runs an action once, when the transfer reports its first finished file.</summary>
    private sealed class ChangeAfterFirstFile(Action change) : IProgress<TransferProgress>
    {
        private bool _done;

        public void Report(TransferProgress value)
        {
            if (_done || value.FilesDone < 1)
                return;
            _done = true;
            change();
        }
    }

    [Fact]
    public async Task A_destination_that_went_away_before_the_first_item_gets_nothing_written()
    {
        var before = Directory.EnumerateFileSystemEntries(_b, "*", SearchOption.AllDirectories).ToList();
        var r = await new DriveTransfer(_clock).RunAsync(new VaultRoot(_a), ["Fotoğraflar"], new VaultRoot(_b), "", false, null, Ct,
            () => "Hedef disk kayboldu.");
        Assert.False(r.Success);
        Assert.Equal(before.Order(), Directory.EnumerateFileSystemEntries(_b, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".diskhaven")).Order());
    }

    private sealed class Recorder : IVaultChangeListener
    {
        public List<string> Added = [], Removed = [];
        void IVaultChangeListener.Added(string fullPath) => Added.Add(fullPath);
        void IVaultChangeListener.Removing(string fullPath) => Removed.Add(fullPath);
    }

    [Fact]
    public async Task A_move_uses_the_source_drives_own_operations_and_tells_the_destination()
    {
        var src = new Recorder(); var dst = new Recorder();
        var srcRoot = new VaultRoot(_a);
        var r = await new DriveTransfer(_clock).RunAsync(srcRoot, ["Fotoğraflar"], new VaultRoot(_b), "Hedef", true, null, Ct, null,
            new FileOperations(srcRoot, _clock, src), dst);
        Assert.True(r.Success);
        Assert.Contains(src.Removed, p => p.EndsWith("Fotoğraflar"));       // folder sizes of the source hear about the move
        Assert.Contains(dst.Added, p => p.EndsWith(Path.Combine("Hedef", "Fotoğraflar")));
    }
}
