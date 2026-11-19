using DiskHaven.Core.Accounts;

namespace DiskHaven.Core.Tests;

public class PermissionStoreTests
{
    private static PermissionStore New() => new(Directory.CreateTempSubdirectory("dh-perm-").FullName);

    [Fact]
    public void A_permission_covers_the_folder_and_what_is_inside_but_not_a_folder_with_a_longer_name()
    {
        var p = New();
        p.Set("", "Aile", "ali", AccessLevel.Read, "azim");
        Assert.Equal(AccessLevel.Read, p.LevelOf("ali", "", "Aile"));
        Assert.Equal(AccessLevel.Read, p.LevelOf("ALI", "", "Aile/Tatil/a.jpg"));   // names are not case sensitive
        Assert.Equal(AccessLevel.None, p.LevelOf("ali", "", "Aile2"));
        Assert.Equal(AccessLevel.None, p.LevelOf("ali", "", "Aile2/x"));
        Assert.Equal(AccessLevel.None, p.LevelOf("ali", "disk-1", "Aile"));          // another drive
        Assert.Equal(AccessLevel.None, p.LevelOf("veli", "", "Aile"));
    }

    [Fact]
    public void The_highest_level_wins_and_everyone_counts_for_all()
    {
        var p = New();
        p.Set("d", "", "ali", AccessLevel.Read, "azim");
        p.Set("d", "Ortak", PermissionStore.Everyone, AccessLevel.Write, "azim");
        Assert.Equal(AccessLevel.Write, p.LevelOf("ali", "d", "Ortak/x"));
        Assert.Equal(AccessLevel.Write, p.LevelOf("yeni-kisi", "d", "Ortak"));
        Assert.Equal(AccessLevel.Read, p.LevelOf("ali", "d", "Baska"));
        Assert.Equal(AccessLevel.None, p.LevelOf("yeni-kisi", "d", "Baska"));
    }

    [Fact]
    public void A_folder_lists_only_the_way_to_what_is_shared()
    {
        var p = New();
        p.Set("", "A/B/C", "ali", AccessLevel.Read, "azim");
        p.Set("", "A/D", "ali", AccessLevel.Write, "azim");
        p.Set("", "X", "veli", AccessLevel.Read, "azim");
        Assert.Equal(["A"], p.VisibleIn("ali", "", ""));
        Assert.Equal(new HashSet<string> { "B", "D" }, p.VisibleIn("ali", "", "A"));
        Assert.Null(p.VisibleIn("ali", "", "A/D"));            // readable: everything inside
        Assert.Empty(p.VisibleIn("ali", "", "X")!);
        Assert.True(p.CanSee("ali", "", "A/B"));
        Assert.False(p.CanSee("ali", "", "A/E"));
        Assert.False(p.CanSee("ali", "", "X"));
    }

    [Fact]
    public void Rename_moves_permissions_and_delete_removes_them_and_both_survive_a_restart()
    {
        var dir = Directory.CreateTempSubdirectory("dh-perm-").FullName;
        var p = new PermissionStore(dir);
        p.Set("", "Eski/Alt", "ali", AccessLevel.Read, "azim");
        p.Set("", "Eskisi", "ali", AccessLevel.Read, "azim");   // a longer name stays where it is
        p.Moved("", "Eski", "Yeni");
        Assert.Equal(AccessLevel.Read, p.LevelOf("ali", "", "Yeni/Alt"));
        Assert.Equal(AccessLevel.None, p.LevelOf("ali", "", "Eski/Alt"));
        Assert.Equal(AccessLevel.Read, p.LevelOf("ali", "", "Eskisi"));
        p.Deleted("", "Yeni");
        Assert.Equal(AccessLevel.None, p.LevelOf("ali", "", "Yeni/Alt"));
        p.Set("", "Kalan", "ali", AccessLevel.Write, "azim");
        var again = new PermissionStore(dir);
        Assert.Equal(AccessLevel.Write, again.LevelOf("ali", "", "Kalan"));
        Assert.Equal(AccessLevel.Read, again.LevelOf("ali", "", "Eskisi"));
        again.RemoveUser("ali");
        Assert.Empty(again.All());
    }

    [Fact]
    public void Setting_none_removes_and_setting_again_replaces()
    {
        var p = New();
        p.Set("", "A", "ali", AccessLevel.Read, "azim");
        p.Set("", "A", "Ali", AccessLevel.Write, "azim");
        Assert.Single(p.All());
        Assert.Equal(AccessLevel.Write, p.LevelOf("ali", "", "A"));
        Assert.Null(p.Set("", "A", "ali", AccessLevel.None, "azim"));
        Assert.Empty(p.All());
        Assert.Equal(new HashSet<string>(), p.SharedIn("", ""));
        p.Set("", "A/B", "ali", AccessLevel.Read, "azim");
        Assert.Equal(["B"], p.SharedIn("", "A"));
        Assert.Empty(p.SharedIn("", ""));
    }
}
