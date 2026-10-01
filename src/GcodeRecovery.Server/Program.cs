using System.Threading.RateLimiting;
using GcodeRecovery.Server;
using GcodeRecovery.Telemetry;

// Gcode Recovery community server: update checks + anonymous usage counters.
// Privacy rules enforced here:
//  * no request logging, no forwarded-header processing, no IP address is read or stored;
//  * rate limiting is global (one bucket for everybody), so no per-client state exists;
//  * only the fields of the Telemetry contract are accepted, each validated against an allow-list.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 2048;
    o.AddServerHeader = false;
});

var databasePath = builder.Configuration["Community:Database"] ?? "community.db";
var repo = builder.Configuration["Community:GitHubRepo"] ?? "ddann/gcode-recovery";
builder.Services.AddSingleton(_ => new CommunityStore(databasePath));
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => new ReleaseFeed(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(), repo, sp.GetRequiredService<ILogger<ReleaseFeed>>()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetFixedWindowLimiter("everyone", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1200,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/v1/update", async (string? version, string? platform, ReleaseFeed feed, CancellationToken ct) =>
{
    if (version is null || !Contract.VersionPattern().IsMatch(version) || !Version.TryParse(version, out var current))
        return Results.BadRequest(new { error = "version must look like 1.2.3" });
    var info = await feed.CheckAsync(current, Contract.NormalisePlatform(platform ?? "other"), ct);
    return Results.Ok(info);
});

app.MapPost("/v1/heartbeat", (Heartbeat beat, CommunityStore store) =>
{
    if (beat.Session == Guid.Empty || !Contract.VersionPattern().IsMatch(beat.Version ?? "") || !Contract.Platforms.Contains(beat.Platform))
        return Results.BadRequest();
    store.Heartbeat(beat);
    return Results.NoContent();
});

app.MapPost("/v1/jobs", (JobReport job, CommunityStore store) =>
{
    if (job.Job == Guid.Empty
        || !Contract.VersionPattern().IsMatch(job.Version ?? "")
        || !Contract.Platforms.Contains(job.Platform)
        || !Contract.Printers.Contains(job.Printer)
        || !Contract.Methods.Contains(job.Method)
        || job.GramsSaved < 0 || job.GramsSaved > Contract.MaxGramsPerJob)
        return Results.BadRequest();
    store.AddJob(job);
    return Results.NoContent();
});

app.MapGet("/v1/stats", (CommunityStore store) => Results.Ok(store.Stats()));

app.MapGet("/", (CommunityStore store) =>
{
    var s = store.Stats();
    var kg = s.GramsSaved / 1000.0;
    return Results.Content($$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Gcode Recovery community</title>
        <style>body{font:16px/1.5 system-ui,sans-serif;max-width:640px;margin:48px auto;padding:0 16px;color:#222;background:#fafafa}
        @media(prefers-color-scheme:dark){body{color:#ddd;background:#16191d}a{color:#7ab7ff} }
        .n{font-size:40px;font-weight:600}.g{display:grid;grid-template-columns:repeat(3,1fr);gap:16px;margin:24px 0}</style></head>
        <body><h1>Gcode Recovery</h1>
        <div class="g"><div><div class="n">{{kg:0.0}} kg</div>filament saved</div>
        <div><div class="n">{{s.CompletedJobs}}</div>prints recovered</div>
        <div><div class="n">{{s.ActiveNow}}</div>using it now</div></div>
        <p>Counted since {{s.Since}}. Only completed recovery jobs are counted, with the grams of filament that did not have to be
        printed again. No G-code, file names, IP addresses or identifiers are stored.</p>
        <p><a href="https://github.com/{{repo}}">github.com/{{repo}}</a></p></body></html>
        """, "text/html");
});

app.Run();

public partial class Program;
