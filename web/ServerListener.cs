using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Quodery.Web;

internal sealed record ServerListener(IPAddress? Address, int Port, ServerCertificate? Tls, int? HttpPort)
{
    public const string Usage = """
        Listening:
          --address <ip>              Address to listen on. Default: every interface.
          --port <port>               Port to listen on. Default: 443 with a certificate, otherwise 8080.
          --tls-certificate <pem>     Certificate (full chain) for serving HTTPS. Requires --tls-key.
          --tls-key <pem>             Private key of the certificate.
          --http-port <port>          With a certificate, also listen for plain HTTP and redirect it to HTTPS.
        """;

    public static string? TryParse(string[] args, out ServerListener listener)
    {
        listener = new ServerListener(null, 8080, null, null);
        IPAddress? address = null;
        string? certificatePath = null, keyPath = null;
        int? port = null, httpPort = null;
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option is not ("--address" or "--port" or "--tls-certificate" or "--tls-key" or "--http-port"))
                return $"unknown argument: {option}";
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                return $"{option} requires a value";
            var value = args[i];
            switch (option)
            {
                case "--address":
                    if (!IPAddress.TryParse(value, out address))
                        return "--address requires an IP address, such as 0.0.0.0, 127.0.0.1 or ::";
                    break;
                case "--tls-certificate": certificatePath = value; break;
                case "--tls-key": keyPath = value; break;
                default:
                    if (!int.TryParse(value, out var number) || number is <= 0 or >= 65536)
                        return $"{option} requires a port number between 1 and 65535";
                    if (option == "--port") port = number; else httpPort = number;
                    break;
            }
        }
        if ((certificatePath is null) != (keyPath is null))
            return "--tls-certificate and --tls-key must be given together";
        if (certificatePath is null && httpPort is not null)
            return "--http-port redirects to HTTPS, so it requires --tls-certificate and --tls-key";
        var listenPort = port ?? (certificatePath is null ? 8080 : 443);
        if (listenPort == httpPort)
            return "--http-port must differ from --port";

        ServerCertificate? tls = null;
        if (certificatePath is not null)
        {
            X509Certificate2 certificate;
            try { certificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath); }
            catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
            {
                return $"could not load the TLS certificate from {certificatePath} and {keyPath}: {error.Message}";
            }
            if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                return $"the TLS certificate in {certificatePath} expired on {certificate.NotAfter.ToUniversalTime():u}";
            tls = new ServerCertificate(certificate);
        }
        listener = new ServerListener(address, listenPort, tls, httpPort);
        return null;
    }

    public bool IsLoopbackOnly => Address is not null && IPAddress.IsLoopback(Address);

    public string PublicBaseUrl()
    {
        var host = Tls?.Certificate.GetNameInfo(X509NameType.DnsName, false) is { Length: > 0 } name && !name.Contains('*')
            ? name
            : Address is null || Address.Equals(IPAddress.Any) || Address.Equals(IPAddress.IPv6Any) ? "localhost"
            : Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString();
        var scheme = Tls is null ? "http" : "https";
        var defaultPort = Tls is null ? 80 : 443;
        return Port == defaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{Port}";
    }
}

internal sealed record ServerCertificate(X509Certificate2 Certificate)
{
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(21);

    public DateTimeOffset ExpiresAt => new(Certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
    public bool ExpiresSoon => ExpiresAt - DateTimeOffset.UtcNow < ExpiryWarning;
}

internal sealed class CertificateExpiryService(ServerCertificate tls, ILogger<CertificateExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (tls.ExpiresSoon)
                logger.LogWarning("The TLS certificate for {Subject} expires on {ExpiresAt:u}. Replace the certificate files and restart the server.",
                    tls.Certificate.GetNameInfo(X509NameType.DnsName, false), tls.ExpiresAt);
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
