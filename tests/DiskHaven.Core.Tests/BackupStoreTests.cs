using DiskHaven.Core.Sync;

namespace DiskHaven.Core.Tests;

public sealed class BackupStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dh-backups-");

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { }
    }

    [Fact]
    public void The_backup_that_was_already_set_up_on_a_disk_survives_the_upgrade()
    {
        var finished = DateTimeOffset.UtcNow.AddMinutes(-5);
        new DiskSettingsStore(_dir.FullName).Save(new DiskSettings(
            "fs-1", "disk-1", "Arşiv", ["Belgeler", "Fotoğraflar"],
            new SyncSchedule(true, "04:30", null), AfterSync.StayOn,
            new SyncSummary(finished, Success: true, Canceled: false, Copied: 7, Updated: 2, Deleted: 1,
                Verified: 9, Errors: 0, BytesCopied: 4321, Message: "bitti")));

        var job = Assert.Single(new BackupStore(_dir.FullName).All());
        Assert.Equal("Arşiv yedeği", job.Name);
        Assert.Equal("", job.SourceDrive);
        Assert.Equal("fs-1", job.TargetDrive);
        Assert.Equal("", job.TargetFolder);
        Assert.Equal(["Belgeler", "Fotoğraflar"], job.SourceFolders);
        Assert.True(job.Enabled);
        Assert.Equal("04:30", job.Time);
        Assert.True(job.Legacy);
        Assert.True(job.AdoptExisting);   // the folders on that disk belong to this backup already
        Assert.Equal(7, job.LastRun!.Copied);
        Assert.Equal(finished, job.LastSuccessAt);
    }

    [Fact]
    public void A_disk_without_a_marker_is_not_turned_into_a_backup()
    {
        new DiskSettingsStore(_dir.FullName).Save(new DiskSettings(
            "fs-2", "", "Yeni disk", ["Belgeler"], SyncSchedule.Off, AfterSync.StayOn, null));
        Assert.Empty(new BackupStore(_dir.FullName).All());
    }

    [Fact]
    public void Reading_twice_does_not_duplicate_the_migration()
    {
        new DiskSettingsStore(_dir.FullName).Save(new DiskSettings(
            "fs-3", "disk-3", "Yedek", ["Belgeler"], new SyncSchedule(true, "02:00", null), AfterSync.StayOn, null));
        Assert.Single(new BackupStore(_dir.FullName).All());
        Assert.Single(new BackupStore(_dir.FullName).All());
    }

    [Fact]
    public void Two_jobs_may_not_mirror_into_the_same_folder()
    {
        var created = DateTimeOffset.UtcNow;
        var a = new BackupJob("a", "A", "", ["Belgeler"], "fs-1", "Yedek", false, "03:00", 50, created);
        var same = new BackupJob("b", "B", "fs-2", ["Veri/Belgeler"], "fs-1", "yedek", false, "03:00", 50, created);
        var other = new BackupJob("c", "C", "", ["Belgeler"], "fs-1", "Baska", false, "03:00", 50, created);
        Assert.True(a.SameTarget(same));
        Assert.False(a.SameTarget(other));
    }
}
