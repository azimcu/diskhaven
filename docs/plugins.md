# Plugins

DiskHaven is a small core (browse, upload, rename, move, delete, recycle bin, accounts,
admin panel) plus plugins. Related features ship together as one plugin.

| Part | How it works |
|---|---|
| Catalog | `PluginCatalog` in `src/DiskHaven.Api/Plugins.cs`: id, name, description, category, version, default state |
| State | `config/plugins.json` (installed ids + each plugin's settings), changed from the admin panel's store |
| API | a plugin's endpoints carry `.RequiresPlugin("id")`: 404 while it is not installed |
| Web UI | a plugin's screens are lazily imported chunks; menus and buttons check `usePlugins().has("id")` |
| Data | removing a plugin never deletes its data; installing it again brings everything back |

Adding a plugin:

1. Add an entry to `PluginCatalog.All`.
2. Map its endpoints with `.RequiresPlugin("<id>")` (and `.LogsActivity("<action>")` for changes).
3. Put its UI in its own file and load it with `lazy(() => import('./MyPlugin'))`; gate menu
   entries with `usePlugins().has('<id>')`.

Loading third-party plugins from separate packages at runtime is planned; the catalog,
state and gating above are the contract they will use.
