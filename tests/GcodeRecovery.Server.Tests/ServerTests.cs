using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GcodeRecovery.Server;
using GcodeRecovery.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GcodeRecovery.Server.Tests;

public sealed class ServerFixture : IDisposable
{
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"gcr-community-{Guid.NewGuid():N}.db");
    public WebApplicationFactory<Program> Factory { get; }

    public ServerFixture()
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Community:Database", DatabasePath));
    }

    public void Dispose()
    {
        Factory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath);
    }
}

public class ServerTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    private static JobReport Job(int grams = 120, string printer = "snapmaker-u1") =>
        new(Guid.NewGuid(), "1.0.2", "osx-arm64", printer, "stream", grams);

    [Fact]
    public async Task Heartbeats_count_distinct_sessions_currently_active()
    {
        var before = (await _client.GetFromJsonAsync<CommunityStats>("/v1/stats"))!.ActiveNow;
        var a = new Heartbeat(Guid.NewGuid(), "1.0.2", "osx-arm64");
        var b = new Heartbeat(Guid.NewGuid(), "1.0.2", "win-x64");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/v1/heartbeat", a)).StatusCode);
        await _client.PostAsJsonAsync("/v1/heartbeat", a);
        await _client.PostAsJsonAsync("/v1/heartbeat", b);
        var stats = await _client.GetFromJsonAsync<CommunityStats>("/v1/stats");
        Assert.Equal(before + 2, stats!.ActiveNow);
    }

    [Fact]
    public async Task Completed_jobs_are_summed_and_duplicates_ignored()
    {
        var before = (await _client.GetFromJsonAsync<CommunityStats>("/v1/stats"))!;
        var job = Job(250);
        await _client.PostAsJsonAsync("/v1/jobs", job);
        await _client.PostAsJsonAsync("/v1/jobs", job); // retried delivery of the same job
        await _client.PostAsJsonAsync("/v1/jobs", Job(50, "bambu-p1s"));
        var after = (await _client.GetFromJsonAsync<CommunityStats>("/v1/stats"))!;
        Assert.Equal(before.CompletedJobs + 2, after.CompletedJobs);
        Assert.Equal(before.GramsSaved + 300, after.GramsSaved);
    }

    [Theory]
    [InlineData("""{"job":"00000000-0000-0000-0000-000000000000","version":"1.0.2","platform":"osx-arm64","printer":"custom","method":"stream","gramsSaved":5}""")]
    [InlineData("""{"job":"7d9a0b52-1111-4c4c-9a9a-111111111111","version":"latest","platform":"osx-arm64","printer":"custom","method":"stream","gramsSaved":5}""")]
    [InlineData("""{"job":"7d9a0b52-1111-4c4c-9a9a-111111111111","version":"1.0.2","platform":"my-laptop","printer":"custom","method":"stream","gramsSaved":5}""")]
    [InlineData("""{"job":"7d9a0b52-1111-4c4c-9a9a-111111111111","version":"1.0.2","platform":"osx-arm64","printer":"192.168.1.5","method":"stream","gramsSaved":5}""")]
    [InlineData("""{"job":"7d9a0b52-1111-4c4c-9a9a-111111111111","version":"1.0.2","platform":"osx-arm64","printer":"custom","method":"stream","gramsSaved":999999}""")]
    public async Task Invalid_reports_are_rejected(string json)
    {
        var response = await _client.PostAsync("/v1/jobs", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Extra_fields_like_gcode_are_never_stored()
    {
        var id = Guid.NewGuid();
        var json = $$"""{"job":"{{id}}","version":"1.0.2","platform":"osx-arm64","printer":"custom","method":"upload","gramsSaved":7,"gcode":"G1 X10 Y10 E1","file":"secret.gcode"}""";
        var response = await _client.PostAsync("/v1/jobs", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var db = new SqliteConnection($"Data Source={fixture.DatabasePath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('jobs') ORDER BY cid";
        var columns = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) columns.Add(r.GetString(0));
        Assert.Equal(["job_id", "day", "version", "platform", "printer", "method", "grams"], columns);

        cmd.CommandText = "SELECT day FROM jobs WHERE job_id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString("N"));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", (string)cmd.ExecuteScalar()!); // date only, no time of day
        var tables = new List<string>();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        cmd.Parameters.Clear();
        using (var r = cmd.ExecuteReader()) while (r.Read()) tables.Add(r.GetString(0));
        Assert.Equal(["daily_peak", "jobs"], tables);
    }

    [Fact]
    public async Task Oversized_bodies_are_refused()
    {
        var big = new string('x', 5000);
        var response = await _client.PostAsync("/v1/jobs", new StringContent($$"""{"pad":"{{big}}"}""", Encoding.UTF8, "application/json"));
        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Update_check_validates_version_and_page_shows_totals()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/v1/update?version=abc&platform=osx-arm64")).StatusCode);
        var page = await _client.GetStringAsync("/");
        Assert.Contains("filament saved", page);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/healthz")).StatusCode);
    }
}

public class ReleaseFeedTests
{
    private sealed class FakeGitHub(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private const string Release = """
        {"tag_name":"v1.2.0","html_url":"https://github.com/ddann/gcode-recovery/releases/tag/v1.2.0","body":"notes",
         "assets":[{"name":"GcodeRecovery-1.2.0-macos-arm64.zip","browser_download_url":"https://x/zip"},
                   {"name":"GcodeRecovery-1.2.0-macos-arm64.dmg","browser_download_url":"https://x/dmg"}]}
        """;

    [Fact]
    public async Task Newer_release_is_offered_with_the_platform_download()
    {
        var gh = new FakeGitHub(Release);
        var feed = new ReleaseFeed(new HttpClient(gh), "ddann/gcode-recovery", NullLogger<ReleaseFeed>.Instance);
        var info = await feed.CheckAsync(new Version(1, 0, 1), "osx-arm64", CancellationToken.None);
        Assert.True(info.UpdateAvailable);
        Assert.Equal("1.2.0", info.Latest);
        Assert.Equal("https://x/dmg", info.DownloadUrl);

        var same = await feed.CheckAsync(new Version(1, 2, 0), "win-x64", CancellationToken.None);
        Assert.False(same.UpdateAvailable);
        Assert.Null(same.DownloadUrl);
        Assert.Equal(1, gh.Calls); // cached
    }
}

public class ClientPrivacyTests
{
    [Fact]
    public void Payloads_contain_only_the_contract_fields()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var job = JsonDocument.Parse(JsonSerializer.Serialize(CommunityClient.CreateJobReport("snapmaker-u1", "stream", 123.6), opts));
        Assert.Equal(["job", "version", "platform", "printer", "method", "gramsSaved"], job.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(124, job.RootElement.GetProperty("gramsSaved").GetInt32());

        var beat = JsonDocument.Parse(JsonSerializer.Serialize(new Heartbeat(Guid.NewGuid(), "1.0.2", "osx-arm64"), opts));
        Assert.Equal(["session", "version", "platform"], beat.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Unknown_printer_profiles_are_reported_as_custom()
    {
        Assert.Equal("custom", CommunityClient.CreateJobReport("my-voron-at-192.168.1.9", "upload", 1).Printer);
        Assert.Equal("other", Contract.NormalisePlatform("freebsd-x64"));
    }

    private sealed class FlakyServer : HttpMessageHandler
    {
        public bool Online { get; set; }
        public List<string> Received { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Online) throw new HttpRequestException("offline");
            Received.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Reports_are_queued_offline_and_sent_later()
    {
        var dir = Directory.CreateTempSubdirectory("gcr-queue").FullName;
        var server = new FlakyServer();
        using var client = new CommunityClient("https://example.invalid", dir, server);
        await client.ReportJobAsync(CommunityClient.CreateJobReport("bambu-p1s", "upload", 42));
        Assert.True(File.Exists(Path.Combine(dir, "pending-job-reports.json")));
        Assert.DoesNotContain("gcode", File.ReadAllText(Path.Combine(dir, "pending-job-reports.json")), StringComparison.OrdinalIgnoreCase);

        server.Online = true;
        await client.FlushQueueAsync();
        Assert.Single(server.Received);
        Assert.False(File.Exists(Path.Combine(dir, "pending-job-reports.json")));
    }
}

public class CompletionTrackerTests
{
    [Fact]
    public void Counts_only_after_running_then_finished()
    {
        var count = 0;
        var t = new CompletionTracker("part-resume-L43.gcode.3mf", () => count++);
        t.Observe("FINISH", "previous-job");      // stale state of another job
        t.Observe("FINISH", "part-resume-L43");   // not seen running yet
        Assert.Equal(0, count);
        t.Observe("PREPARE", "part-resume-L43");
        t.Observe("RUNNING", "part-resume-L43");
        t.Observe("FINISH", "part-resume-L43");
        t.Observe("FINISH", "part-resume-L43");
        Assert.Equal(1, count);
        Assert.True(t.IsDone);
    }

    [Fact]
    public void Cancelled_jobs_are_not_counted()
    {
        var count = 0;
        var t = new CompletionTracker("part-resume-L43.gcode", () => count++);
        t.Observe("printing", "part-resume-L43.gcode");
        t.Observe("cancelled", "part-resume-L43.gcode");
        t.Observe("complete", "part-resume-L43.gcode");
        Assert.Equal(0, count);
    }
}

public class ResilientDnsTests
{
    [Fact]
    public async Task Doh_resolves_the_community_server()
    {
        try
        {
            var ips = await ResilientDns.ResolveViaDohAsync("one.one.one.one", CancellationToken.None);
            Assert.Contains(ips, ip => ip.ToString() is "1.1.1.1" or "1.0.0.1");
        }
        catch (HttpRequestException)
        {
            // No internet on this runner: nothing to check.
        }
    }
}
