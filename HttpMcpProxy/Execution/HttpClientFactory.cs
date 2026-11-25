using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HttpMcpProxy.Configuration;

namespace HttpMcpProxy.Execution;

/// <summary>Builds the per-service <see cref="HttpClient"/> from http/tls/proxy/security config.</summary>
internal static class HttpClientFactory
{
    public static HttpClient Create(ServiceDefinition service)
    {
        var config = service.Config;
        var http = service.Settings.Http;

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(http.Timeout.ConnectMs),
            AllowAutoRedirect = false, // redirects are followed manually so every hop is host-checked
            AutomaticDecompression = http.Compression ? DecompressionMethods.All : DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        };

        if (config.Proxy.Url is { Length: > 0 } proxyUrl)
        {
            handler.Proxy = new WebProxy(proxyUrl, BypassOnLocal: false, BypassList: config.Proxy.NoProxy.Select(ToBypassRegex).ToArray());
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        // SSRF protection: check the actual IP we connect to (defeats DNS rebinding). Not possible through
        // a proxy, where host allow-listing still applies.
        if (!config.Security.AllowPrivateNetworks && handler.Proxy is null)
            handler.ConnectCallback = ConnectPublicOnlyAsync;

        handler.SslOptions = BuildSslOptions(config.Tls, service.Directory);
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static SslClientAuthenticationOptions BuildSslOptions(TlsConfig tls, string baseDir)
    {
        var options = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = tls.MinVersion == "1.3" ? SslProtocols.Tls13 : SslProtocols.Tls12 | SslProtocols.Tls13,
        };

        if (!tls.Verify)
        {
            options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else if (tls.CaFile is { Length: > 0 } caFile)
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(Resolve(caFile, baseDir));
            options.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            options.CertificateChainPolicy.CustomTrustStore.AddRange(roots);
        }

        if (tls.ClientCert is { Length: > 0 } certFile)
        {
            var certPath = Resolve(certFile, baseDir);
            var keyPath = tls.ClientKey is { Length: > 0 } k ? Resolve(k, baseDir) : null;
            var cert = tls.ClientKeyPassword is { Length: > 0 } password
                ? X509Certificate2.CreateFromEncryptedPemFile(certPath, password, keyPath)
                : X509Certificate2.CreateFromPemFile(certPath, keyPath);
            options.ClientCertificates = [cert];
        }

        return options;
    }

    private static string Resolve(string path, string baseDir) => Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path);

    private static string ToBypassRegex(string host) =>
        "^" + System.Text.RegularExpressions.Regex.Escape(host).Replace(@"\*", ".*") + "$";

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);

        var allowed = addresses.Where(a => !NetworkGuard.IsPrivate(a)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"Blocked: {context.DnsEndPoint.Host} resolves to a private/loopback address (security.allow_private_networks is false)");

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
}

internal static class NetworkGuard
{
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                   || b[0] == 127
                   || b[0] == 0
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)            // link-local, cloud metadata
                   || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
    }
}
