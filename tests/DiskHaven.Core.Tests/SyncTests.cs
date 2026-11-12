using DiskHaven.Core.Storage;
using DiskHaven.Core.Sync;
using Microsoft.Extensions.Time.Testing;

namespace DiskHaven.Core.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string _src = Directory.CreateTempSubdirectory("diskhaven-src-").FullName;
    private readonly string _dst = Directory.CreateTempSubdirectory("diskhaven-dst-").FullName;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private static readonly DateTime T0 = new(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Directory.Delete(_src, true);
        Directory.Delete(_dst, true);
    }

    private void Write(string root, string rel, string content, DateTime? mtime = null)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, mtime ?? T0);
    }

    private async Task<(SyncPlan Plan, SyncReport Report)> Sync(params string[] folders)
    {
        var plan = SyncPlanner.Plan(_src, folders, _dst);
        var report = await new SyncExecutor(_clock).ExecuteAsync(plan, _src, _dst, ct: TestContext.Current.CancellationToken);
        return (plan, report);
    }

    private IReadOnlyList<TrashItem> Trash() => new FileOperations(new VaultRoot(_dst), _clock).ListTrash();

    [Fact]
    public async Task A_file_that_became_a_folder_stays_on_the_backup()
    {
        Write(_src, "D/X", "eski dosya");
        await Sync("D");
        File.Delete(Path.Combine(_src, "D/X"));
        Write(_src, "D/X/ic.txt", "yeni", T0.AddDays(1));

        var (_, report) = await Sync("D");

        Assert.True(report.Success);
        Assert.Equal("yeni", File.ReadAllText(Path.Combine(_dst, "D/X/ic.txt")));
        Assert.Contains(Trash(), t => t.OriginalPath == "D/X");
        Assert.True(SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken).IsEmpty);
    }

    [Fact]
    public async Task A_folder_that_became_a_file_stays_on_the_backup()
    {
        Write(_src, "D/X/ic.txt", "eski");
        await Sync("D");
        Directory.Delete(Path.Combine(_src, "D/X"), true);
        Write(_src, "D/X", "yeni dosya", T0.AddDays(1));

        var (_, report) = await Sync("D");

        Assert.True(report.Success);
        Assert.Equal("yeni dosya", File.ReadAllText(Path.Combine(_dst, "D/X")));
        Assert.Contains(Trash(), t => t.OriginalPath == "D/X");
        Assert.True(SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken).IsEmpty);
    }

    [Fact]
    public async Task A_deleted_folder_counts_every_file_inside_for_the_approval_limit()
    {
        for (var i = 0; i < 10; i++)
            Write(_src, $"D/k{i}.txt", "k");
        for (var i = 0; i < 90; i++)
            Write(_src, $"D/Buyuk/b{i}.txt", "b" + i);
        await Sync("D");
        Directory.Delete(Path.Combine(_src, "D/Buyuk"), true);

        var plan = SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken);

        Assert.Equal(90, plan.DeletedFiles);
        Assert.Equal(100, plan.TargetFiles);
        Assert.True(plan.NeedsApproval(50));
    }

    [Fact]
    public async Task A_folder_replaced_by_a_file_counts_for_the_approval_limit()
    {
        Write(_src, "D/k.txt", "k");
        for (var i = 0; i < 90; i++)
            Write(_src, $"D/X/b{i}.txt", "b" + i);
        await Sync("D");
        Directory.Delete(Path.Combine(_src, "D/X"), true);
        Write(_src, "D/X", "dosya", T0.AddDays(1));

        Assert.True(SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken).NeedsApproval(50));
    }

    [Fact]
    public async Task A_renamed_folder_does_not_ask_for_approval()
    {
        for (var i = 0; i < 90; i++)
            Write(_src, $"D/Eski/b{i}.txt", "b" + i, T0.AddMinutes(i));
        Write(_src, "D/k.txt", "k");
        await Sync("D");
        Directory.Move(Path.Combine(_src, "D/Eski"), Path.Combine(_src, "D/Yeni"));

        var plan = SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken);

        Assert.Equal(0, plan.DeletedFiles);
        Assert.False(plan.NeedsApproval(50));
    }

    [Fact]
    public async Task A_lost_disk_stops_the_backup_before_anything_more_is_written()
    {
        Write(_src, "D/a.txt", "a");
        Write(_src, "D/b.txt", "b");
        Write(_dst, "D/sil.txt", "boyutu farkli");
        var plan = SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken);
        var calls = 0;

        var report = await new SyncExecutor(_clock).ExecuteAsync(plan, _src, new VaultRoot(_dst), "", null,
            TestContext.Current.CancellationToken, () => ++calls >= 2 ? "disk gitti" : null);

        Assert.False(report.Success);
        Assert.Contains(report.Errors, e => e.Message == "disk gitti");
        Assert.Equal(1, report.Copied);
        Assert.False(File.Exists(Path.Combine(_dst, "D/b.txt")));
        Assert.True(File.Exists(Path.Combine(_dst, "D/sil.txt")));
    }

    [Fact]
    public void Backup_store_ignores_ids_that_are_not_plain_names()
    {
        var config = Directory.CreateTempSubdirectory("diskhaven-cfg-").FullName;
        try
        {
            var keep = Path.Combine(config, "keep.json");
            File.WriteAllText(keep, "[]");
            var store = new BackupStore(Path.Combine(config, "c"));
            Directory.CreateDirectory(Path.Combine(config, "c", "backup-history"));
            store.Remove("../..");
            Assert.True(File.Exists(keep));
            Assert.Empty(store.History("../.."));
        }
        finally { Directory.Delete(config, true); }
    }

    [Fact]
    public async Task First_sync_copies_everything_with_times_and_second_sync_is_empty()
    {
        Write(_src, "Belgeler/a.txt", "a");
        Write(_src, "Belgeler/Alt/b.txt", "bb");
        Directory.CreateDirectory(Path.Combine(_src, "Belgeler/Boş"));

        var (plan, report) = await Sync("Belgeler");
        Assert.Equal(2, plan.Count(SyncActionKind.Copy));
        Assert.True(report.Success);
        Assert.Equal(2, report.Verified);
        Assert.Equal("bb", File.ReadAllText(Path.Combine(_dst, "Belgeler/Alt/b.txt")));
        Assert.Equal(T0, File.GetLastWriteTimeUtc(Path.Combine(_dst, "Belgeler/a.txt")));
        Assert.True(Directory.Exists(Path.Combine(_dst, "Belgeler/Boş")));
        Assert.False(Directory.Exists(Path.Combine(_dst, SyncExecutor.TempFolder)));

        Assert.True(SyncPlanner.Plan(_src, ["Belgeler"], _dst, TestContext.Current.CancellationToken).IsEmpty);
    }

    [Fact]
    public async Task Changed_and_deleted_files_go_to_disk_trash_not_away()
    {
        Write(_src, "D/keep.txt", "1");
        Write(_src, "D/change.txt", "eski");
        Write(_src, "D/remove.txt", "sil");
        await Sync("D");

        Write(_src, "D/change.txt", "yeni içerik", T0.AddHours(1));
        File.Delete(Path.Combine(_src, "D/remove.txt"));
        var (plan, report) = await Sync("D");

        Assert.Equal(1, plan.Count(SyncActionKind.Update));
        Assert.Equal(1, plan.Count(SyncActionKind.Delete));
        Assert.Equal(1, report.Updated);
        Assert.Equal(1, report.Deleted);
        Assert.Equal("yeni içerik", File.ReadAllText(Path.Combine(_dst, "D/change.txt")));
        Assert.False(File.Exists(Path.Combine(_dst, "D/remove.txt")));
        var trashed = Trash().Select(i => i.Name).Order().ToArray();
        Assert.Equal(["change.txt", "remove.txt"], trashed);
    }

    [Fact]
    public async Task Times_within_two_seconds_count_as_equal()
    {
        Write(_src, "D/a.txt", "x", T0);
        Write(_dst, "D/a.txt", "x", T0.AddSeconds(1.5));
        Assert.True((await Sync("D")).Plan.IsEmpty);
    }

    [Fact]
    public async Task Only_assigned_folders_are_touched_on_the_backup()
    {
        Write(_src, "Belgeler/a.txt", "a");
        Write(_dst, "Kişisel/kendi-dosyam.txt", "dokunma");
        Write(_dst, "kökte.txt", "dokunma");
        await Sync("Belgeler");
        Assert.True(File.Exists(Path.Combine(_dst, "Kişisel/kendi-dosyam.txt")));
        Assert.True(File.Exists(Path.Combine(_dst, "kökte.txt")));
    }

    [Fact]
    public async Task Case_change_is_renamed_on_the_backup_instead_of_recopied()
    {
        Write(_src, "D/Foto.jpg", "x");
        await Sync("D");
        File.Move(Path.Combine(_src, "D/Foto.jpg"), Path.Combine(_src, "D/FOTO.jpg"));
        var (plan, _) = await Sync("D");
        Assert.Equal(1, plan.Count(SyncActionKind.FixCase));
        Assert.Equal(0, plan.Count(SyncActionKind.Copy));
        Assert.Equal(["FOTO.jpg"], Directory.GetFiles(Path.Combine(_dst, "D")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Diskhaven_own_files_are_never_copied_or_deleted()
    {
        Write(_src, "D/iyi.txt", "1");
        Write(_src, "D/.diskhaven-disk.json", "marker");
        Write(_dst, "D/.diskhaven-backup.json", "baska is");
        Write(_dst, "D/.diskhaven/trash/eski", "cop");
        var plan = SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(plan.Actions, a => a.Path.Contains(".diskhaven", StringComparison.OrdinalIgnoreCase));
        var report = await new SyncExecutor(_clock).ExecuteAsync(plan, _src, _dst, ct: TestContext.Current.CancellationToken);
        Assert.True(report.Success);
        Assert.Equal("baska is", File.ReadAllText(Path.Combine(_dst, "D/.diskhaven-backup.json")));
        Assert.True(File.Exists(Path.Combine(_dst, "D/.diskhaven/trash/eski")));
        Assert.False(File.Exists(Path.Combine(_dst, "D/.diskhaven-disk.json")));
    }

    [Fact]
    public async Task Windows_invalid_names_are_skipped_and_reported()
    {
        Write(_src, "D/iyi.txt", "1");
        Write(_src, "D/kötü:ad.txt", "2");
        var (_, report) = await Sync("D");
        Assert.Single(report.Skipped);
        Assert.Contains("kötü:ad.txt", report.Skipped[0]);
        Assert.True(File.Exists(Path.Combine(_dst, "D/iyi.txt")));
    }

    [Fact]
    public void Nested_folder_assignments_are_collapsed() =>
        Assert.Equal(["Belgeler", "Müzik"], SyncPlanner.NormalizeFolders(["Belgeler/Okul", "Müzik", "Belgeler", "belgeler"]));

    [Fact]
    public async Task Old_disk_trash_is_purged_after_30_days()
    {
        Write(_src, "D/a.txt", "1");
        await Sync("D");
        File.Delete(Path.Combine(_src, "D/a.txt"));
        await Sync("D");
        Assert.Single(Trash());
        _clock.Advance(TimeSpan.FromDays(31));
        await Sync("D");
        Assert.Empty(Trash());
    }

    [Fact]
    public async Task Cancel_stops_cleanly_without_partial_files()
    {
        for (var i = 0; i < 20; i++)
            Write(_src, $"D/f{i}.txt", new string('x', 1000));
        var plan = SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgressHook(p => { if (p.FilesDone == 5) cts.Cancel(); });
        var report = await new SyncExecutor(_clock).ExecuteAsync(plan, _src, _dst, progress, cts.Token);
        Assert.True(report.Canceled);
        Assert.All(Directory.GetFiles(Path.Combine(_dst, "D")), f => Assert.Equal(1000, new FileInfo(f).Length));
        Assert.False(Directory.Exists(Path.Combine(_dst, SyncExecutor.TempFolder)));
    }

    private sealed class SyncProgressHook(Action<SyncProgress> on) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => on(value);
    }
}

public sealed class SyncMoveTests : IDisposable
{
    private readonly string _src = Directory.CreateTempSubdirectory("diskhaven-msrc-").FullName;
    private readonly string _dst = Directory.CreateTempSubdirectory("diskhaven-mdst-").FullName;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private static readonly DateTime T0 = new(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose() { Directory.Delete(_src, true); Directory.Delete(_dst, true); }

    private void Write(string rel, string content, DateTime? mtime = null)
    {
        var p = Path.Combine(_src, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, mtime ?? T0);
    }

    private async Task<(SyncPlan, SyncReport)> Sync()
    {
        var plan = SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken);
        return (plan, await new SyncExecutor(_clock).ExecuteAsync(plan, _src, _dst, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Renamed_folder_is_moved_on_the_backup_without_copying_again()
    {
        Write("D/Eski/a.bin", "aaaa");
        Write("D/Eski/Alt/b.bin", "bbbbbb", T0.AddDays(1));
        await Sync();

        Directory.Move(Path.Combine(_src, "D/Eski"), Path.Combine(_src, "D/Yeni"));
        var (plan, report) = await Sync();

        Assert.Equal(2, plan.Count(SyncActionKind.Move));
        Assert.Equal(0, plan.Count(SyncActionKind.Copy));
        Assert.Equal(0, plan.BytesToCopy);
        Assert.Equal(2, report.Moved);
        Assert.Equal(0, report.BytesCopied);
        Assert.True(report.Success);
        Assert.Equal("bbbbbb", File.ReadAllText(Path.Combine(_dst, "D/Yeni/Alt/b.bin")));
        Assert.False(Directory.Exists(Path.Combine(_dst, "D/Eski")));
        Assert.Empty(new FileOperations(new VaultRoot(_dst), _clock).ListTrash());
        Assert.True(SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken).IsEmpty);
    }

    [Fact]
    public async Task File_moved_to_another_folder_is_moved_on_the_backup()
    {
        Write("D/A/foto.jpg", "içerik");
        Directory.CreateDirectory(Path.Combine(_src, "D/B"));
        await Sync();
        File.Move(Path.Combine(_src, "D/A/foto.jpg"), Path.Combine(_src, "D/B/foto.jpg"));
        var (plan, report) = await Sync();
        Assert.Equal(1, plan.Count(SyncActionKind.Move));
        Assert.Equal(1, report.Moved);
        Assert.True(File.Exists(Path.Combine(_dst, "D/B/foto.jpg")));
        Assert.False(File.Exists(Path.Combine(_dst, "D/A/foto.jpg")));
    }

    [Fact]
    public async Task Same_size_and_time_but_different_content_falls_back_to_copy_and_keeps_old_in_trash()
    {
        Write("D/eski.txt", "AAAA");
        await Sync();
        File.Delete(Path.Combine(_src, "D/eski.txt"));
        Write("D/yeni.txt", "BBBB"); // same size and time, other content: looks like a move
        var plan = SyncPlanner.Plan(_src, ["D"], _dst, TestContext.Current.CancellationToken);
        Assert.Equal(1, plan.Count(SyncActionKind.Move));
        var report = await new SyncExecutor(_clock).ExecuteAsync(plan, _src, _dst, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, report.Moved);
        Assert.Equal("BBBB", File.ReadAllText(Path.Combine(_dst, "D/yeni.txt")));
        Assert.False(File.Exists(Path.Combine(_dst, "D/eski.txt")));
        var trashed = Assert.Single(new FileOperations(new VaultRoot(_dst), _clock).ListTrash());
        Assert.Equal("AAAA", File.ReadAllText(Path.Combine(_dst, VaultRoot.InternalFolderName, "trash", trashed.Id, "item")));
    }

    [Fact]
    public async Task Copies_of_new_files_with_unrelated_deletes_stay_copies()
    {
        Write("D/silinecek.txt", "12345");
        await Sync();
        File.Delete(Path.Combine(_src, "D/silinecek.txt"));
        Write("D/yeni.txt", "1234567890");
        var (plan, _) = await Sync();
        Assert.Equal(0, plan.Count(SyncActionKind.Move));
        Assert.Equal(1, plan.Count(SyncActionKind.Copy));
        Assert.Equal(1, plan.Count(SyncActionKind.Delete));
    }
}
