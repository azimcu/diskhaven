using HomeVault.Core.Sync;
using Microsoft.Extensions.Time.Testing;

namespace HomeVault.Core.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string _src = Directory.CreateTempSubdirectory("homevault-src-").FullName;
    private readonly string _dst = Directory.CreateTempSubdirectory("homevault-dst-").FullName;
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
        var report = await new SyncExecutor(_clock).ExecuteAsync(plan, _src, _dst);
        return (plan, report);
    }

    private string[] TrashFiles() =>
        Directory.Exists(Path.Combine(_dst, SyncExecutor.TrashFolder))
            ? Directory.GetFiles(Path.Combine(_dst, SyncExecutor.TrashFolder), "*", SearchOption.AllDirectories)
            : [];

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

        Assert.True(SyncPlanner.Plan(_src, ["Belgeler"], _dst).IsEmpty);
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
        var trashed = TrashFiles().Select(f => Path.GetFileName(f)!).Order().ToArray();
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
        Assert.Single(TrashFiles());
        _clock.Advance(TimeSpan.FromDays(31));
        await Sync("D");
        Assert.Empty(TrashFiles());
    }

    [Fact]
    public async Task Cancel_stops_cleanly_without_partial_files()
    {
        for (var i = 0; i < 20; i++)
            Write(_src, $"D/f{i}.txt", new string('x', 1000));
        var plan = SyncPlanner.Plan(_src, ["D"], _dst);
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
    private readonly string _src = Directory.CreateTempSubdirectory("homevault-msrc-").FullName;
    private readonly string _dst = Directory.CreateTempSubdirectory("homevault-mdst-").FullName;
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
        var plan = SyncPlanner.Plan(_src, ["D"], _dst);
        return (plan, await new SyncExecutor(_clock).ExecuteAsync(plan, _src, _dst));
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
        Assert.Empty(Directory.Exists(Path.Combine(_dst, SyncExecutor.TrashFolder))
            ? Directory.GetFiles(Path.Combine(_dst, SyncExecutor.TrashFolder), "*", SearchOption.AllDirectories) : []);
        Assert.True(SyncPlanner.Plan(_src, ["D"], _dst).IsEmpty);
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
        var plan = SyncPlanner.Plan(_src, ["D"], _dst);
        Assert.Equal(1, plan.Count(SyncActionKind.Move));
        var report = await new SyncExecutor(_clock).ExecuteAsync(plan, _src, _dst);

        Assert.Equal(0, report.Moved);
        Assert.Equal("BBBB", File.ReadAllText(Path.Combine(_dst, "D/yeni.txt")));
        Assert.False(File.Exists(Path.Combine(_dst, "D/eski.txt")));
        var trashed = Directory.GetFiles(Path.Combine(_dst, SyncExecutor.TrashFolder), "*", SearchOption.AllDirectories);
        Assert.Equal("AAAA", File.ReadAllText(Assert.Single(trashed)));
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
