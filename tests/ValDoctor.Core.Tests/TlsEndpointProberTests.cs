using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ValDoctor.Core.Network;

namespace ValDoctor.Core.Tests;

/// <summary>Exercises the real TCP/TLS code against local loopback servers (no internet needed).</summary>
public class TlsEndpointProberTests
{
    private static TlsEndpointProber Prober(int port) => new(new ProbeOptions
    {
        Port = port,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        TlsTimeout = TimeSpan.FromMilliseconds(700),
        HttpTimeout = TimeSpan.FromMilliseconds(700),
    });

    [Fact]
    public async Task Closed_port_is_reported_as_refused()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await Prober(port).ProbeAsync("auth.riotgames.com", IPAddress.Loopback, TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStage.None, result.Stage);
        Assert.Equal(ProbeFailure.TcpRefused, result.Failure);
    }

    [Fact]
    public async Task Server_that_resets_during_handshake_is_reported_as_tls_failure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            client.Client.LingerState = new LingerOption(true, 0); // RST on close
            client.Close();
        }, TestContext.Current.CancellationToken);

        var result = await Prober(port).ProbeAsync("auth.riotgames.com", IPAddress.Loopback, TestContext.Current.CancellationToken);
        await server;

        // The RST usually arrives during the TLS handshake; on some systems (seen on macOS) it can beat the end of the
        // TCP connect, which is then correctly reported as a TCP reset. Either way the address must not be healthy.
        var resetDuringTls = result.Stage == ProbeStage.TcpConnected &&
                             result.Failure is ProbeFailure.TlsReset or ProbeFailure.TlsHandshakeFailed;
        var resetDuringConnect = result.Stage == ProbeStage.None && result.Failure == ProbeFailure.TcpReset;
        Assert.True(resetDuringTls || resetDuringConnect, $"{result.Stage}/{result.Failure}");
        Assert.False(result.Healthy);
    }

    [Fact]
    public async Task Silent_server_times_out_during_tls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var accepted = new SemaphoreSlim(0);
        TcpClient? held = null;
        var server = Task.Run(async () =>
        {
            held = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            accepted.Release();
        }, TestContext.Current.CancellationToken);

        var result = await Prober(port).ProbeAsync("auth.riotgames.com", IPAddress.Loopback, TestContext.Current.CancellationToken);
        await server;
        held?.Dispose();

        Assert.Equal(ProbeFailure.TlsTimeout, result.Failure);
    }

    [Fact]
    public async Task Self_signed_certificate_is_rejected_with_details()
    {
        using var cert = CreateSelfSigned("block.page.example");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var ssl = new SslStream(client.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(cert);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                // The client aborts the handshake after rejecting the certificate.
            }
        }, TestContext.Current.CancellationToken);

        var result = await Prober(port).ProbeAsync("auth.riotgames.com", IPAddress.Loopback, TestContext.Current.CancellationToken);
        await server;

        Assert.Equal(ProbeFailure.CertificateInvalid, result.Failure);
        Assert.True(result.CertificateErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.True(result.CertificateErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains("block.page.example", result.CertificateIssuer, StringComparison.Ordinal);
        Assert.False(result.Reachable);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK", true, 200)]
    [InlineData("HTTP/1.1 403 Forbidden", true, 403)]
    [InlineData("HTTP/1.0 503", true, 503)]
    [InlineData("HTTP/2 200", false, 0)]
    [InlineData("garbage", false, 0)]
    [InlineData("HTTP/1.1 99 Nope", false, 0)]
    [InlineData("HTTP/1.1 2000 Nope", false, 0)]
    public void Parses_status_line(string line, bool ok, int status)
    {
        Assert.Equal(ok, TlsEndpointProber.TryParseStatus(line, out var parsed));
        if (ok)
        {
            Assert.Equal(status, parsed);
        }
    }

    private static X509Certificate2 CreateSelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Re-import so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "t"), "t", X509KeyStorageFlags.Exportable);
    }
}
