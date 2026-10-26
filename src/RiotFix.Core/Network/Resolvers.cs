using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace RiotFix.Core.Network;

public enum DnsFailureKind
{
    None,
    NotFound,
    Timeout,
    Error,
}

/// <summary>Result of resolving a name through the operating system (this includes the hosts file).</summary>
public sealed record SystemDnsAnswer(IReadOnlyList<IPAddress> Addresses, DnsFailureKind Failure, string? Error = null)
{
    public bool Success => Failure == DnsFailureKind.None && Addresses.Count > 0;
}

/// <summary>Answer from one trusted DNS-over-HTTPS resolver.</summary>
public sealed record TrustedDnsAnswer(string ResolverName, bool Responded, IReadOnlyList<IPAddress> Addresses, string? Error = null);

public interface ISystemResolver
{
    Task<SystemDnsAnswer> ResolveAsync(string host, CancellationToken cancellationToken);
}

public interface ITrustedResolver
{
    string Name { get; }

    Task<TrustedDnsAnswer> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Resolves names the same way games do: through the OS resolver, which honours the hosts file.</summary>
public sealed class SystemResolver : ISystemResolver
{
    public async Task<SystemDnsAnswer> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, cancellationToken).ConfigureAwait(false);
            return addresses.Length == 0
                ? new SystemDnsAnswer([], DnsFailureKind.NotFound)
                : new SystemDnsAnswer(addresses, DnsFailureKind.None);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            return new SystemDnsAnswer([], DnsFailureKind.NotFound, ex.SocketErrorCode.ToString());
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TryAgain || ex.SocketErrorCode == SocketError.TimedOut)
        {
            return new SystemDnsAnswer([], DnsFailureKind.Timeout, ex.SocketErrorCode.ToString());
        }
        catch (SocketException ex)
        {
            return new SystemDnsAnswer([], DnsFailureKind.Error, ex.SocketErrorCode.ToString());
        }
    }
}

public sealed record DohResolverDefinition(string Name, IReadOnlyList<Uri> Endpoints);

public static class TrustedResolvers
{
    /// <summary>
    /// Well-known public DoH resolvers. Each is tried by host name first and then by IP, so a poisoned
    /// system DNS answer for the resolver itself cannot block it. All connections use normal certificate validation.
    /// </summary>
    public static IReadOnlyList<DohResolverDefinition> Defaults { get; } =
    [
        new("Cloudflare DNS", [new("https://cloudflare-dns.com/dns-query"), new("https://1.1.1.1/dns-query"), new("https://1.0.0.1/dns-query")]),
        new("Google Public DNS", [new("https://dns.google/dns-query"), new("https://8.8.8.8/dns-query"), new("https://8.8.4.4/dns-query")]),
        new("Quad9", [new("https://dns.quad9.net/dns-query"), new("https://9.9.9.9/dns-query")]),
        new("AdGuard DNS (без фильтрации)", [new("https://unfiltered.adguard-dns.com/dns-query")]),
    ];
}

/// <summary>DNS-over-HTTPS client (RFC 8484, GET with application/dns-message). Queries A and AAAA.</summary>
public sealed class DohResolver : ITrustedResolver
{
    private const int MaxResponseBytes = 65535;
    private readonly IReadOnlyList<Uri> _endpoints;
    private readonly HttpMessageInvoker _http;
    private readonly TimeSpan _timeout;

    public DohResolver(DohResolverDefinition definition, HttpMessageInvoker http, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Name = definition.Name;
        _endpoints = definition.Endpoints;
        _http = http;
        _timeout = timeout;
    }

    public string Name { get; }

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = false,
        UseCookies = false,
        // Certificate validation stays at the platform default (full chain + host name). Never overridden.
    };

    public async Task<TrustedDnsAnswer> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        string? lastError = null;
        foreach (var endpoint in _endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var v4 = QueryAsync(endpoint, host, DnsRecordType.A, cancellationToken);
                var v6 = QueryAsync(endpoint, host, DnsRecordType.Aaaa, cancellationToken);
                try
                {
                    await Task.WhenAll(v4, v6).ConfigureAwait(false);
                }
                catch (Exception) when (v4.IsCompletedSuccessfully && !cancellationToken.IsCancellationRequested)
                {
                    // AAAA failed but A succeeded: IPv4 answers are enough.
                }

                var a = await v4.ConfigureAwait(false);
                if (a.ResponseCode is not (DnsResponse.NoError or DnsResponse.NameError))
                {
                    lastError = $"rcode {a.ResponseCode}";
                    continue;
                }

                var v6Addresses = v6.IsCompletedSuccessfully && v6.Result.ResponseCode == DnsResponse.NoError ? v6.Result.Addresses : [];
                var addresses = a.Addresses.Concat(v6Addresses).Distinct().ToList();
                return new TrustedDnsAnswer(Name, true, addresses);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastError = "timeout";
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.HttpRequestError.ToString();
            }
            catch (DnsFormatException)
            {
                lastError = "invalid response";
            }
            catch (InvalidDataException)
            {
                lastError = "invalid response";
            }
        }

        return new TrustedDnsAnswer(Name, false, [], lastError);
    }

    private async Task<DnsResponse> QueryAsync(Uri endpoint, string host, DnsRecordType type, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var query = Convert.ToBase64String(DnsWireFormat.BuildQuery(host, type)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, $"?dns={query}"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/dns-message", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unexpected DoH content type.");
        }

        var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxResponseBytes + 1];
            var total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxResponseBytes)
                {
                    throw new InvalidDataException("DoH response is too large.");
                }
            }

            return DnsWireFormat.ParseResponse(buffer.AsSpan(0, total), host, type);
        }
    }
}
