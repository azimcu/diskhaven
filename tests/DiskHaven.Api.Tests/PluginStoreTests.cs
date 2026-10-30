using DiskHaven.Api;

namespace DiskHaven.Api.Tests;

public class PluginStoreTests
{
    [Fact]
    public void A_new_default_plugin_is_installed_once_on_upgrade_but_removed_ones_stay_removed()
    {
        var d = Directory.CreateTempSubdirectory();
        try
        {
            // a v0.4 plugins.json: no "Known" list, the admin had removed the activity log
            File.WriteAllText(Path.Combine(d.FullName, "plugins.json"),
                """{"Installed":["archive","sharing","disks","gauge","quick"],"Settings":{}}""");
            var store = new PluginStore(d.FullName);
            Assert.True(store.IsInstalled("versions"));
            Assert.False(store.IsInstalled("activity"));
            Assert.False(store.IsInstalled("vault"));

            store.SetInstalled("versions", false);
            Assert.False(new PluginStore(d.FullName).IsInstalled("versions"));   // stays removed after a restart
        }
        finally { d.Delete(true); }
    }

    [Fact]
    public void A_fresh_server_gets_the_default_set()
    {
        var d = Directory.CreateTempSubdirectory();
        try
        {
            var store = new PluginStore(d.FullName);
            Assert.True(store.IsInstalled("versions"));
            Assert.False(store.IsInstalled("vault"));
        }
        finally { d.Delete(true); }
    }
}
