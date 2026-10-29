using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiskHaven.Api.Disks;

public sealed class DiskAgentException(string message) : Exception(message);

/// <summary>Agent's view of one disk (see agent/diskhaven-diskd.py).</summary>
public sealed record AgentDisk(
    string Id, string Kind, string Label, string FsType, long Size, string Model, string Serial,
    string? MountPoint, string State, string Power);

/// <summary>Talks to diskhaven-diskd over its Unix socket: one JSON line out, one JSON line back.</summary>
public sealed class DiskAgentClient(IConfiguration config)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _socket = config["DiskHaven:AgentSocket"] ?? "/run/diskhaven/diskd.sock";

    public bool Available => File.Exists(_socket);

    public async Task<JsonObject> CallAsync(object request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!Available)
            throw new DiskAgentException("Disk servisi çalışmıyor.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socket), cts.Token);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json) + "\n"), cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync(cts.Token) ?? throw new DiskAgentException("Disk servisi yanıt vermedi.");
        var response = JsonNode.Parse(line)!.AsObject();
        if (response["ok"]?.GetValue<bool>() != true)
            throw new DiskAgentException(Translate(response["error"]?.GetValue<string>() ?? "unknown error"));
        return response;
    }

    public async Task<IReadOnlyList<AgentDisk>> ListAsync(CancellationToken ct = default)
    {
        var r = await CallAsync(new { op = "list" }, ct: ct);
        return r["disks"].Deserialize<List<AgentDisk>>(Json) ?? [];
    }

    public Task MountAsync(string id, CancellationToken ct = default) => CallAsync(new { op = "mount", id }, ct: ct);
    public Task UnmountAsync(string id, CancellationToken ct = default) => CallAsync(new { op = "unmount", id }, ct: ct);
    public Task StandbyAsync(string id, CancellationToken ct = default) => CallAsync(new { op = "standby", id }, ct: ct);
    public Task PowerOffAsync(string id, CancellationToken ct = default) => CallAsync(new { op = "powerOff", id }, TimeSpan.FromMinutes(2), ct);
    public Task PowerOnAsync(string id, CancellationToken ct = default) => CallAsync(new { op = "powerOn", id }, TimeSpan.FromMinutes(2), ct);
    public Task<JsonObject> SmartAsync(string id, CancellationToken ct = default) => CallAsync(new { op = "smart", id }, ct: ct);

    private static string Translate(string error) => error switch
    {
        "disk not found or not allowed" => "Disk bulunamadı ya da DiskHaven'un bu diske erişim izni yok.",
        "disk is unplugged; connect it again" => "Disk takılı değil. Kabloyu tekrar tak.",
        "disk did not come back; unplug and reconnect it" => "Disk yeniden açılmadı. Kabloyu çıkarıp tekrar tak.",
        "unknown disk" => "Bu disk daha önce görülmedi. Kabloyu tak.",
        _ when error.StartsWith("umount failed") && error.Contains("busy") => "Disk şu an kullanılıyor, çıkarılamadı. Biraz sonra tekrar dene.",
        _ => error,
    };
}
