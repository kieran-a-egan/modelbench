using System.Net;
using System.Net.Sockets;

namespace ModelBench.Core.Config;

/// <summary>
/// The "local models only" policy: a model endpoint must be loopback or a private network (RFC 1918, link-local, IPv6 ULA).
/// Checked when the catalog lists or resolves a model, and again for every connection the provider opens (see <see cref="ConnectAsync"/>).
/// </summary>
public static class LocalEndpoint
{
    public static bool IsLocal(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Null when <paramref name="baseUrl"/> is an http(s) URL whose host resolves only to local addresses, else why not.</summary>
    public static string? Check(string? baseUrl)
    {
        if (baseUrl is null) return "no baseUrl";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return $"baseUrl '{baseUrl}' is not an http(s) URL";
        IPAddress[] addrs;
        try { addrs = IPAddress.TryParse(uri.IdnHost, out var ip) ? [ip] : Dns.GetHostAddresses(uri.IdnHost); }
        catch (SocketException) { return $"baseUrl host '{uri.Host}' does not resolve"; }
        return addrs.Length > 0 && addrs.All(IsLocal) ? null : $"baseUrl host '{uri.Host}' is not a loopback or private-network address";
    }

    /// <summary>SocketsHttpHandler.ConnectCallback: connects only to a local address, so DNS changes after the catalog check cannot send prompts elsewhere.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
        => await ConnectAsync(ctx.DnsEndPoint.Host, await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct), ctx.DnsEndPoint.Port, ct);

    /// <summary>Tries each local address in order (e.g. ::1 then 127.0.0.1 for "localhost") and returns the first connection; non-local addresses are never attempted.</summary>
    public static async ValueTask<Stream> ConnectAsync(string host, IEnumerable<IPAddress> addresses, int port, CancellationToken ct)
    {
        var allowed = addresses.Where(IsLocal).ToList();
        if (allowed.Count == 0)
            throw new HttpRequestException($"refusing to connect to '{host}': not a loopback or private-network address (ModelBench runs local models only)");
        var errors = new List<string>();
        foreach (var addr in allowed)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; // throws if e.g. IPv6 is disabled
                await socket.ConnectAsync(new IPEndPoint(addr, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex) { socket?.Dispose(); errors.Add($"{addr}: {ex.SocketErrorCode}"); }
            catch { socket?.Dispose(); throw; } // cancellation and anything unexpected
        }
        throw new HttpRequestException($"could not connect to '{host}' port {port} (tried {string.Join(", ", errors)})");
    }
}
