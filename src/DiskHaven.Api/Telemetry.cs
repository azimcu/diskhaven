using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DiskHaven.Api;

/// <summary>
/// OpenTelemetry: request timings, errors, .NET runtime numbers and DiskHaven's own counters.
/// Off unless an OTLP/HTTP address is configured, so a plain install sends nothing anywhere:
///   DiskHaven:Telemetry:Metrics  e.g. http://prometheus:9090/api/v1/otlp/v1/metrics
///   DiskHaven:Telemetry:Traces   e.g. http://jaeger:4318/v1/traces
///   DiskHaven:Telemetry:ServiceName (default "diskhaven")
/// </summary>
public static class Telemetry
{
    public const string Name = "DiskHaven";
    public static readonly ActivitySource Source = new(Name);
    static readonly Meter Meter = new(Name);

    public static readonly Counter<long> FileOperations =
        Meter.CreateCounter<long>("diskhaven.file_operations", description: "Completed file changes (folder, rename, move, copy, delete, restore)");
    public static readonly Counter<long> Uploads =
        Meter.CreateCounter<long>("diskhaven.uploads", description: "Files uploaded and verified");
    public static readonly Counter<long> UploadedBytes =
        Meter.CreateCounter<long>("diskhaven.uploaded_bytes", unit: "By", description: "Bytes uploaded and verified");
    public static readonly Counter<long> FailedLogins =
        Meter.CreateCounter<long>("diskhaven.failed_logins", description: "Sign-ins with a wrong user name or password");
    public static readonly Counter<long> ShareDownloads =
        Meter.CreateCounter<long>("diskhaven.share_downloads", description: "Files fetched through public share links");

    public static void AddDiskHavenTelemetry(this WebApplicationBuilder builder)
    {
        var metrics = builder.Configuration["DiskHaven:Telemetry:Metrics"];
        var traces = builder.Configuration["DiskHaven:Telemetry:Traces"];
        if (string.IsNullOrWhiteSpace(metrics) && string.IsNullOrWhiteSpace(traces))
            return;
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(builder.Configuration["DiskHaven:Telemetry:ServiceName"] ?? "diskhaven"));
        if (!string.IsNullOrWhiteSpace(metrics))
            otel.WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(Name)
                .AddOtlpExporter((o, reader) =>
                {
                    o.Endpoint = new Uri(metrics);
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 30_000;
                }));
        if (!string.IsNullOrWhiteSpace(traces))
            otel.WithTracing(t => t
                // health checks run every few seconds and would drown out real requests
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/api/health");
                    // share tokens and file names must not leave the app: keep only the route template
                    o.EnrichWithHttpResponse = (activity, response) => Redact(activity, response.HttpContext);
                })
                .AddHttpClientInstrumentation()
                .AddSource(Name)
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(traces);
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                }));
    }

    /// <summary>Replaces the real path (which can hold a share token or file names) with the route template, drops the query.</summary>
    public static void Redact(Activity activity, HttpContext ctx)
    {
        var template = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        activity.SetTag("url.path", template is null ? "(unmatched)" : "/" + template.TrimStart('/'));
        activity.SetTag("url.query", null);
    }

    /// <summary>Counts a file change once the endpoint answered with success; the kind is the last path segment.</summary>
    public static async ValueTask<object?> CountFileOperation(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var result = await next(ctx);
        var http = ctx.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && (result is not IStatusCodeHttpResult r || r.StatusCode is null or < 400))
            FileOperations.Add(1, new KeyValuePair<string, object?>("operation", OperationName(http.Request.Path)));
        return result;
    }

    public static string OperationName(PathString path) =>
        path.Value?.TrimEnd('/').Split('/') switch
        {
            [.., "trash", _, "restore"] => "restore",
            [.., "trash", _] or [.., "trash"] => "purge",
            [.., var last] when last.Length > 0 => last,
            _ => "unknown",
        };
}
