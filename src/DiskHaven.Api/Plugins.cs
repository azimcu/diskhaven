using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiskHaven.Api;

/// <summary>
/// Plugins: everything beyond the bare file manager (browse, upload, rename, move, delete, recycle bin, accounts)
/// is a plugin that an admin installs or removes from the store, without a restart and without touching any data.
/// Related features ship together as one plugin. A removed plugin's endpoints answer 404 and the web UI does not
/// even download its code (each plugin's UI is a separate, lazily loaded chunk).
/// Removing a plugin never deletes what it stored; installing it again brings everything back.
/// </summary>
public sealed record PluginInfo(string Id, string Name, string Description, string Category, string Version,
    bool DefaultInstalled, string[] Features);

public static class PluginCatalog
{
    public static readonly IReadOnlyList<PluginInfo> All =
    [
        new("archive", "ZIP arşivleri", "Seçili öğeleri ZIP dosyasına sıkıştırır, ZIP'leri ayıklar; klasörleri ve çoklu seçimi tek ZIP olarak indirir.",
            "Dosyalar", "1.0.0", true, ["ZIP dosyasına sıkıştır", "Tümünü ayıkla", "ZIP olarak indir"]),
        new("sharing", "Paylaşım bağlantıları", "Dosya ve klasörler için hesap gerektirmeyen, süreli, salt okunur bağlantılar.",
            "Paylaşım", "1.0.0", true, ["Bağlantı oluştur", "Süre sınırı", "Paylaşılanlar listesi"]),
        new("disks", "Diskler ve yedekleme", "Harici diskleri tanır, klasörleri diske eşitler, zamanlar, SMART sağlık bilgisi ve güç aç/kapa.",
            "Depolama", "1.0.0", true, ["Disk eşitleme", "Zamanlama", "SMART", "Güç yönetimi"]),
        new("gauge", "Sunucu göstergesi", "Durum çubuğunda canlı işlemci, bellek, sıcaklık ve disk okuma/yazma hızı.",
            "Sistem", "1.0.0", true, ["Canlı ölçümler", "2 dakikalık işlemci grafiği"]),
        new("quick", "Hızlı erişim", "Favoriler ve son kullanılanlar: sık açtığın klasör ve dosyalar sol menüde bir tık uzakta.",
            "Dosyalar", "1.0.0", true, ["Favorilere ekle", "Son kullanılanlar"]),
        new("activity", "Etkinlik günlüğü", "Kim, ne zaman, hangi dosyada ne yaptı: yükleme, silme, taşıma, paylaşım. Yönetici panelinde aranabilir.",
            "Güvenlik", "1.0.0", true, ["Etkinlik akışı", "Kullanıcıya ve işleme göre süzme"]),
        new("vault", "Gizli klasör", "Parolayla açılan, içeriği şifreli, ayrı bir klasör ağacı. Menüde dikkat çekmeyen bir adla görünür; yem parolası sahte bir içerik açar.",
            "Güvenlik", "1.0.0", false, ["AES-256 şifreleme", "Kurtarma anahtarı", "Yem parolası", "Yedek disklere şifreli kopya", "Çevrimdışı kurtarma aracı"]),
    ];

    public static PluginInfo? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>Which plugins are installed, plus each plugin's small settings object (config/plugins.json).</summary>
public sealed class PluginStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly Lock _lock = new();
    private State _state;

    private sealed class State
    {
        public HashSet<string> Installed { get; set; } = [];
        public Dictionary<string, JsonObject> Settings { get; set; } = [];
    }

    public PluginStore(string configDir)
    {
        _file = Path.Combine(configDir, "plugins.json");
        _state = File.Exists(_file)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(_file)) ?? new State()
            : new State { Installed = PluginCatalog.All.Where(p => p.DefaultInstalled).Select(p => p.Id).ToHashSet() };
    }

    public bool IsInstalled(string id) { lock (_lock) return _state.Installed.Contains(id); }
    public IReadOnlyCollection<string> Installed { get { lock (_lock) return _state.Installed.ToArray(); } }

    public void SetInstalled(string id, bool installed)
    {
        if (PluginCatalog.Find(id) is null)
            throw new KeyNotFoundException("Plugin not found.");
        lock (_lock)
        {
            if (installed) _state.Installed.Add(id); else _state.Installed.Remove(id);
            Save();
        }
    }

    public JsonObject Settings(string id)
    {
        lock (_lock) return _state.Settings.TryGetValue(id, out var s) ? (JsonObject)s.DeepClone() : [];
    }

    public void SaveSettings(string id, JsonObject settings)
    {
        lock (_lock) { _state.Settings[id] = (JsonObject)settings.DeepClone(); Save(); }
    }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
        File.Move(tmp, _file, overwrite: true);
    }
}

public static class PluginEndpoints
{
    /// <summary>Endpoints of a plugin answer 404 while it is not installed, as if they did not exist.</summary>
    public static TBuilder RequiresPlugin<TBuilder>(this TBuilder builder, string id) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.RequestServices.GetRequiredService<PluginStore>().IsInstalled(id)
                ? await next(ctx)
                : Results.Problem("This feature is not installed.", statusCode: StatusCodes.Status404NotFound));
}
