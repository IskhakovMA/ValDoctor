using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RiotFix.Core.Network;

public enum ProbeStage
{
    None,
    TcpConnected,
    TlsEstablished,
    HttpResponded,
}

public enum ProbeFailure
{
    None,
    NotPublicAddress,
    TcpTimeout,
    TcpRefused,
    TcpReset,
    NetworkUnreachable,
    TlsTimeout,
    TlsReset,
    TlsHandshakeFailed,
    CertificateInvalid,
    HttpTimeout,
    HttpReset,
    HttpInvalidResponse,

    /// <summary>The whole probe exceeded its overall time budget.</summary>
    Timeout,
    Error,
}

/// <summary>Outcome of one connection attempt to <c>host</c> at a specific address.</summary>
public sealed record ProbeResult(
    IPAddress Address,
    ProbeStage Stage,
    ProbeFailure Failure,
    TimeSpan Elapsed,
    int? HttpStatus = null,
    SslPolicyErrors CertificateErrors = SslPolicyErrors.None,
    string? CertificateSubject = null,
    string? CertificateIssuer = null,
    string? TlsProtocol = null,
    string? Detail = null)
{
    /// <summary>A validated TLS session was established and the server returned an HTTP status line.</summary>
    public bool Reachable => Stage == ProbeStage.HttpResponded;

    /// <summary>Reachable and the response is not a server-side error (5xx).</summary>
    public bool Healthy => Reachable && HttpStatus is >= 100 and < 500;
}

public interface IEndpointProber
{
    Task<ProbeResult> ProbeAsync(string host, IPAddress address, CancellationToken cancellationToken);
}

public sealed record ProbeOptions
{
    public int Port { get; init; } = 443;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan TlsTimeout { get; init; } = TimeSpan.FromSeconds(7);

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(7);

    public string UserAgent { get; init; } = "RiotFix (connectivity check)";
}

/// <summary>
/// Connects to a specific IP, performs a TLS handshake with SNI = <c>host</c> and full certificate validation
/// (chain + host name; revocation follows the .NET/HttpClient default of no online check), then sends a
/// minimal <c>GET /</c> and reads only the status line. No cookies, credentials or tokens are ever sent.
/// Invalid certificates are never accepted — the validation callback only records why validation failed.
/// </summary>
public sealed class TlsEndpointProber(ProbeOptions? options = null) : IEndpointProber
{
    private readonly ProbeOptions _options = options ?? new ProbeOptions();

    public async Task<ProbeResult> ProbeAsync(string host, IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var sw = Stopwatch.StartNew();
        ProbeResult Fail(ProbeStage stage, ProbeFailure failure, string? detail = null, SslPolicyErrors certErrors = SslPolicyErrors.None,
            string? subject = null, string? issuer = null) =>
            new(address, stage, failure, sw.Elapsed, CertificateErrors: certErrors, CertificateSubject: subject, CertificateIssuer: issuer, Detail: detail);

        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        // --- TCP ---
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(_options.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, _options.Port), connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail(ProbeStage.None, ProbeFailure.TcpTimeout);
            }
            catch (SocketException ex)
            {
                return Fail(ProbeStage.None, ex.SocketErrorCode switch
                {
                    SocketError.TimedOut => ProbeFailure.TcpTimeout,
                    SocketError.ConnectionRefused => ProbeFailure.TcpRefused,
                    SocketError.ConnectionReset or SocketError.ConnectionAborted => ProbeFailure.TcpReset,
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown
                        or SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable => ProbeFailure.NetworkUnreachable,
                    _ => ProbeFailure.Error,
                }, ex.SocketErrorCode.ToString());
            }
        }

        // --- TLS ---
        var stream = new NetworkStream(socket, ownsSocket: false);
        SslPolicyErrors captured = SslPolicyErrors.None;
        string? subject = null;
        string? issuer = null;
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, certificate, _, errors) =>
        {
            captured = errors;
            if (certificate is not null)
            {
                subject = certificate.Subject;
                issuer = certificate.Issuer;
            }

            // Only a fully valid certificate for this exact host name is accepted.
            return errors == SslPolicyErrors.None;
        });

        await using (ssl.ConfigureAwait(false))
        {
            using (var tlsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                tlsTimeout.CancelAfter(_options.TlsTimeout);
                try
                {
                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = host,
                            ApplicationProtocols = [SslApplicationProtocol.Http11],
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                            EnabledSslProtocols = SslProtocols.None, // OS default (TLS 1.2/1.3)
                        },
                        tlsTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Fail(ProbeStage.TcpConnected, ProbeFailure.TlsTimeout);
                }
                catch (AuthenticationException ex) when (captured != SslPolicyErrors.None)
                {
                    return Fail(ProbeStage.TcpConnected, ProbeFailure.CertificateInvalid, ex.Message, captured, subject, issuer);
                }
                catch (AuthenticationException ex)
                {
                    return Fail(ProbeStage.TcpConnected, ProbeFailure.TlsHandshakeFailed, ex.Message);
                }
                catch (IOException ex)
                {
                    return Fail(ProbeStage.TcpConnected, ProbeFailure.TlsReset, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
                }
            }

            var protocol = ssl.SslProtocol.ToString();
            subject = ssl.RemoteCertificate?.Subject ?? subject;
            issuer = ssl.RemoteCertificate?.Issuer ?? issuer;

            // --- HTTP ---
            using var httpTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            httpTimeout.CancelAfter(_options.HttpTimeout);
            try
            {
                var request = $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: {_options.UserAgent}\r\nAccept: */*\r\nConnection: close\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), httpTimeout.Token).ConfigureAwait(false);
                await ssl.FlushAsync(httpTimeout.Token).ConfigureAwait(false);

                var statusLine = await ReadStatusLineAsync(ssl, httpTimeout.Token).ConfigureAwait(false);
                if (statusLine is null || !TryParseStatus(statusLine, out var status))
                {
                    return new ProbeResult(address, ProbeStage.TlsEstablished, ProbeFailure.HttpInvalidResponse, sw.Elapsed,
                        CertificateSubject: subject, CertificateIssuer: issuer, TlsProtocol: protocol);
                }

                return new ProbeResult(address, ProbeStage.HttpResponded, ProbeFailure.None, sw.Elapsed, status,
                    CertificateSubject: subject, CertificateIssuer: issuer, TlsProtocol: protocol);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ProbeResult(address, ProbeStage.TlsEstablished, ProbeFailure.HttpTimeout, sw.Elapsed,
                    CertificateSubject: subject, CertificateIssuer: issuer, TlsProtocol: protocol);
            }
            catch (IOException ex)
            {
                return new ProbeResult(address, ProbeStage.TlsEstablished, ProbeFailure.HttpReset, sw.Elapsed,
                    CertificateSubject: subject, CertificateIssuer: issuer, TlsProtocol: protocol, Detail: ex.InnerException?.GetType().Name);
            }
        }
    }

    internal static bool TryParseStatus(string statusLine, out int status)
    {
        status = 0;
        var parts = statusLine.Split(' ', 3);
        return parts.Length >= 2 &&
               parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) &&
               parts[1].Length == 3 &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out status) &&
               status is >= 100 and <= 599;
    }

    private static async Task<string?> ReadStatusLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            var end = Array.IndexOf(buffer, (byte)'\n', 0, total);
            if (end >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
            }
        }

        return null;
    }
}
