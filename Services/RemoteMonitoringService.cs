using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Deviso.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deviso.Services;

public sealed class RemoteMonitoringService(DiskService diskService) : IAsyncDisposable
{
    public const int Port = 47821;
    private const string CertificateName = "Deviso authenticated LAN monitoring";
    private WebApplication? _webApplication;
    private X509Certificate2? _certificate;
    private string? _token;

    public async Task<RemoteServerInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_webApplication is not null)
        {
            throw new InvalidOperationException("Remote monitoring is already running.");
        }

        var addresses = GetPrivateIpv4Addresses();
        if (addresses.Count == 0)
        {
            throw new InvalidOperationException(
                "No active private IPv4 network address was found. Connect to a private LAN before starting remote monitoring.");
        }

        var certificate = LoadOrCreateCertificate();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            foreach (var address in addresses)
            {
                options.Listen(address, Port, listenOptions => listenOptions.UseHttps(
                    certificate,
                    httpsOptions => httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13));
            }
        });

        var app = builder.Build();
        app.MapGet("/api/health", async (HttpContext context) =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            var expected = Encoding.UTF8.GetBytes($"Bearer {token}");
            var supplied = Encoding.UTF8.GetBytes(authorization);
            if (expected.Length != supplied.Length ||
                !CryptographicOperations.FixedTimeEquals(expected, supplied))
            {
                return Results.Unauthorized();
            }

            var disks = await diskService.GetDisksAsync(context.RequestAborted);
            return Results.Json(disks);
        });

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            certificate.Dispose();
            throw;
        }

        _webApplication = app;
        _certificate = certificate;
        _token = token;
        return new RemoteServerInfo(
            addresses.Select(address => address.ToString()).ToArray(),
            Port,
            token,
            certificate.GetCertHashString(HashAlgorithmName.SHA256));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var app = _webApplication;
        _webApplication = null;
        _token = null;
        if (app is not null)
        {
            await app.StopAsync(cancellationToken);
            await app.DisposeAsync();
        }

        _certificate?.Dispose();
        _certificate = null;
    }

    public async Task<IReadOnlyList<PhysicalDisk>> ReadPeerAsync(
        string ipv4Address,
        string token,
        string certificateSha256,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ipv4Address, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !IsPrivateIpv4(address))
        {
            throw new ArgumentException("Enter a private LAN IPv4 address (10.x, 172.16–31.x, or 192.168.x).",
                nameof(ipv4Address));
        }

        var fingerprint = new string(certificateSha256.Where(Uri.IsHexDigit).ToArray());
        if (fingerprint.Length != 64 || string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Enter the peer's 64-character SHA-256 certificate fingerprint and pairing token.");
        }

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
                DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
                string.Equals(
                    certificate.GetCertHashString(HashAlgorithmName.SHA256),
                    fingerprint,
                    StringComparison.OrdinalIgnoreCase)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://{address}:{Port}/api/health");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("The peer rejected the pairing token.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<PhysicalDisk>>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The peer returned an invalid drive inventory.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static List<IPAddress> GetPrivateIpv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                              network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                              IsPrivateIpv4(address))
            .Distinct()
            .ToList();

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static X509Certificate2 LoadOrCreateCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectName, CertificateName, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(certificate =>
                certificate.HasPrivateKey &&
                DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
                DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
                certificate.Extensions
                    .OfType<X509EnhancedKeyUsageExtension>()
                    .Any(extension => extension.EnhancedKeyUsages
                        .Cast<System.Security.Cryptography.Oid>()
                        .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1")));
        if (existing is not null)
        {
            return new X509Certificate2(existing);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={CertificateName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var enhancedKeyUsages = new System.Security.Cryptography.OidCollection
        {
            new("1.3.6.1.5.5.7.3.1")
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedKeyUsages, true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(Environment.MachineName);
        foreach (var address in GetPrivateIpv4Addresses())
        {
            subjectAlternativeNames.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddYears(2));
        var exportPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var privateKeyBundle = generated.Export(X509ContentType.Pfx, exportPassword);
        try
        {
            using var persisted = X509CertificateLoader.LoadPkcs12(
                privateKeyBundle,
                exportPassword,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            if (!persisted.HasPrivateKey)
            {
                throw new CryptographicException(
                    "Windows did not persist the Deviso LAN certificate's private key.");
            }

            store.Add(persisted);
            return new X509Certificate2(persisted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBundle);
        }
    }
}

public sealed record RemoteServerInfo(
    IReadOnlyList<string> Addresses,
    int Port,
    string PairingToken,
    string CertificateSha256);
