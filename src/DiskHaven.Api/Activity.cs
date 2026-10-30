using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiskHaven.Api;

/// <summary>
/// Activity log plugin: who changed what, when (config/activity.jsonl, newest 5 000 kept). Only changes are logged,
/// never reads, and never anything from the hidden folder: its activity would reveal that it exists.
/// </summary>
public sealed class ActivityLog
{
    public sealed record Item(DateTimeOffset At, string User, string Action, string[] Paths, string? Detail, string? Ip);
    private const int Keep = 5000;
    private readonly string _file;
    private readonly Lock _lock = new();
    private readonly List<Item> _items;

    public ActivityLog(string configDir)
    {
        _file = Path.Combine(configDir, "activity.jsonl");
        _items = File.Exists(_file)
            ? File.ReadLines(_file).Select(l => { try { return JsonSerializer.Deserialize<Item>(l); } catch (JsonException) { return null; } })
                .OfType<Item>().TakeLast(Keep).ToList()
            : [];
        if (_items.Count >= Keep) Rewrite();
    }

    public void Add(Item item)
    {
        lock (_lock)
        {
            _items.Add(item);
            File.AppendAllText(_file, JsonSerializer.Serialize(item) + "\n");
            if (_items.Count > Keep * 1.2) { _items.RemoveRange(0, _items.Count - Keep); Rewrite(); }
        }
    }

    public IReadOnlyList<Item> Query(string? text, string? user, string? action, int limit)
    {
        lock (_lock)
            return _items.AsEnumerable().Reverse()
                .Where(i => user is null || i.User.Equals(user, StringComparison.OrdinalIgnoreCase))
                .Where(i => action is null || i.Action == action)
                .Where(i => string.IsNullOrWhiteSpace(text) || i.Paths.Any(p => p.Contains(text, StringComparison.OrdinalIgnoreCase)))
                .Take(Math.Clamp(limit, 1, 1000)).ToList();
    }

    private void Rewrite()
    {
        var tmp = _file + ".tmp";
        File.WriteAllLines(tmp, _items.Select(i => JsonSerializer.Serialize(i)));
        File.Move(tmp, _file, overwrite: true);
    }
}

public static class ActivityEndpoints
{
    /// <summary>Records <paramref name="action"/> when the endpoint succeeds and the activity plugin is installed.</summary>
    public static TBuilder LogsActivity<TBuilder>(this TBuilder builder, string action) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var result = await next(ctx);
            var http = ctx.HttpContext;
            var plugins = http.RequestServices.GetRequiredService<PluginStore>();
            if (!plugins.IsInstalled("activity") || http.Response.StatusCode >= 400 || result is IStatusCodeHttpResult { StatusCode: >= 400 })
                return result;
            var paths = new List<string>();
            string? detail = null;
            foreach (var arg in ctx.Arguments)
            {
                // only the request bodies (records declared in this assembly); services and the user are not data
                if (arg is null || arg.GetType().Assembly != typeof(ActivityLog).Assembly || arg is ActivityLog) continue;
                JsonObject? node;
                try { node = JsonSerializer.SerializeToNode(arg) as JsonObject; }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { continue; }
                if (node is null) continue;
                foreach (var key in new[] { "Path", "Paths", "Parent", "Destination", "RelativeName" })
                {
                    if (node[key] is JsonArray arr) paths.AddRange(arr.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0));
                    else if (node[key] is JsonValue val && val.TryGetValue<string>(out var str) && str.Length > 0)
                    {
                        if (key is "Destination" or "Parent") detail = "→ /" + str; else paths.Add(str);
                    }
                }
                if (node["Name"] is JsonValue nv && nv.TryGetValue<string>(out var made) && made.Length > 0)   // new folder
                {
                    paths.Add(detail is { } d ? d["→ /".Length..] + "/" + made : made);
                    detail = null;
                }
                if (node["NewName"] is JsonValue nn && nn.TryGetValue<string>(out var newName)) detail = "→ " + newName;
            }
            http.RequestServices.GetRequiredService<ActivityLog>().Add(new ActivityLog.Item(
                DateTimeOffset.UtcNow, http.User.Identity?.Name ?? "?", action, paths.ToArray(), detail,
                http.Connection.RemoteIpAddress?.ToString()));
            return result;
        });
}

/// <summary>Quick access plugin: favourites and recently opened items, per user (config/quick.json).</summary>
public sealed class QuickAccess(string configDir)
{
    private sealed class UserData { public List<string> Favorites { get; set; } = []; public List<string> Recent { get; set; } = []; }
    private readonly string _file = Path.Combine(configDir, "quick.json");
    private readonly Lock _lock = new();

    private Dictionary<string, UserData> Load() =>
        File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, UserData>>(File.ReadAllText(_file)) ?? [] : [];

    private void Save(Dictionary<string, UserData> d)
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(d));
        File.Move(tmp, _file, overwrite: true);
    }

    public (IReadOnlyList<string> Favorites, IReadOnlyList<string> Recent) Get(string user)
    {
        lock (_lock) return Load().TryGetValue(user, out var u) ? (u.Favorites, u.Recent) : ([], []);
    }

    public void Change(string user, Action<List<string>, List<string>> change)
    {
        lock (_lock)
        {
            var all = Load();
            if (!all.TryGetValue(user, out var u)) all[user] = u = new UserData();
            change(u.Favorites, u.Recent);
            Save(all);
        }
    }

    public static string Name(ClaimsPrincipal u) => u.Identity!.Name!;
}
