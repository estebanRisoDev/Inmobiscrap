using System.Net;
using System.Net.Sockets;

namespace Inmobiscrap.Infrastructure;

/// <summary>
/// HttpClient que solo se conecta a IPs públicas (protección SSRF).
/// La validación se hace en ConnectCallback, así que aplica también a cada
/// redirect y no es vulnerable a DNS rebinding (se conecta a la IP validada).
/// </summary>
public static class SafeHttp
{
    public static HttpClient CreateClient(bool allowRedirect, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect        = allowRedirect,
            MaxAutomaticRedirections = 5,
            UseProxy                 = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression   = DecompressionMethods.All,
            ConnectCallback          = ConnectToPublicAddressAsync,
        };

        return new HttpClient(handler) { Timeout = timeout };
    }

    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct);

        var allowed = addresses.Where(IsPublicAddress).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"Destino no permitido (IP privada o reservada): {host}");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0                                   // 0.0.0.0/8
                  || b[0] == 10                                  // 10.0.0.0/8
                  || b[0] == 127                                 // loopback
                  || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)  // 100.64.0.0/10 CGNAT
                  || (b[0] == 169 && b[1] == 254)                // link-local / metadata cloud
                  || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12
                  || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
                  || (b[0] == 192 && b[1] == 0 && b[2] == 0)     // 192.0.0.0/24
                  || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // benchmarking
                  || b[0] >= 224);                               // multicast + reservado
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
                || ip.IsIPv6Multicast)
                return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique local
            return true;
        }

        return false;
    }
}
