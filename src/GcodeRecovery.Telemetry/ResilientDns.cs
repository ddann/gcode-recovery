using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace GcodeRecovery.Telemetry;

/// <summary>
/// HTTP handler whose connections fall back to Cloudflare DNS-over-HTTPS (queried by IP, so it needs no DNS itself)
/// when the system resolver cannot resolve the community server — e.g. a router, ISP or VPN resolver still caching a
/// "does not exist" answer. Only the server's host name is looked up; TLS still validates the real certificate.
/// </summary>
public static class ResilientDns
{
    private static readonly HttpClient DoH = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
    {
        BaseAddress = new Uri("https://1.1.1.1/"),
        Timeout = TimeSpan.FromSeconds(8),
    };

    public static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            IPAddress[] system;
            try
            {
                system = await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (SocketException)
            {
                system = [];
            }

            // Prefer IPv4; only consult DoH when the system gave nothing usable (no answer, or IPv6 only that
            // may be unroutable on this network) or when none of its addresses accepts a connection.
            var stream = await TryConnectAsync(system, port, ct);
            if (stream is not null) return stream;
            var doh = await ResolveViaDohAsync(host, ct);
            return await TryConnectAsync(doh, port, ct)
                ?? throw new HttpRequestException($"Could not connect to {host} (system DNS: {system.Length} address(es), DoH: {doh.Length}).");
        },
    };

    private static async Task<Stream?> TryConnectAsync(IEnumerable<IPAddress> addresses, int port, CancellationToken ct)
    {
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(address, port, timeout.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                socket.Dispose();
            }
        }
        return null;
    }

    /// <summary>Resolves A records for <paramref name="host"/> with Cloudflare's JSON DNS-over-HTTPS API.</summary>
    public static async Task<IPAddress[]> ResolveViaDohAsync(string host, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(host)}&type=A");
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await DoH.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("Answer", out var answers)) return [];
        return answers.EnumerateArray()
            .Where(a => a.GetProperty("type").GetInt32() == 1)
            .Select(a => IPAddress.TryParse(a.GetProperty("data").GetString(), out var ip) ? ip : null)
            .OfType<IPAddress>()
            .ToArray();
    }
}
