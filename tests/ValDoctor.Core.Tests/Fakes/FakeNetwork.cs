using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using ValDoctor.Core.Network;

namespace ValDoctor.Core.Tests.Fakes;

public sealed class FakeSystemResolver : ISystemResolver
{
    private readonly Dictionary<string, SystemDnsAnswer> _answers = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> _failuresLeft = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

    public SystemDnsAnswer Default { get; set; } = new([], DnsFailureKind.NotFound);

    public int CallsTo(string host)
    {
        lock (_calls)
        {
            return _calls.TryGetValue(host, out var n) ? n : 0;
        }
    }

    /// <summary>The next <paramref name="times"/> lookups of <paramref name="host"/> fail, then the normal answer returns.</summary>
    public FakeSystemResolver FailFirst(string host, int times)
    {
        lock (_calls)
        {
            _failuresLeft[host] = times;
        }

        return this;
    }

    public FakeSystemResolver Set(string host, params string[] addresses)
    {
        _answers[host] = new SystemDnsAnswer(addresses.Select(IPAddress.Parse).ToList(), DnsFailureKind.None);
        return this;
    }

    public FakeSystemResolver Fail(string host, DnsFailureKind kind)
    {
        _answers[host] = new SystemDnsAnswer([], kind);
        return this;
    }

    public Task<SystemDnsAnswer> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_calls)
        {
            _calls[host] = (_calls.TryGetValue(host, out var n) ? n : 0) + 1;
            if (_failuresLeft.TryGetValue(host, out var left) && left > 0)
            {
                _failuresLeft[host] = left - 1;
                return Task.FromResult(new SystemDnsAnswer([], DnsFailureKind.Timeout, "transient"));
            }
        }

        return Task.FromResult(_answers.TryGetValue(host, out var a) ? a : Default);
    }
}

public sealed class FakeTrustedResolver(string name) : ITrustedResolver
{
    private readonly Dictionary<string, string[]> _answers = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; } = name;

    public bool Unreachable { get; set; }

    private int _calls;

    public int Calls => _calls;

    public FakeTrustedResolver Set(string host, params string[] addresses)
    {
        _answers[host] = addresses;
        return this;
    }

    public Task<TrustedDnsAnswer> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (Unreachable)
        {
            return Task.FromResult(new TrustedDnsAnswer(Name, false, [], "timeout"));
        }

        var addrs = _answers.TryGetValue(host, out var a) ? a.Select(IPAddress.Parse).ToList() : [];
        return Task.FromResult(new TrustedDnsAnswer(Name, true, addrs));
    }
}

/// <summary>Scripted prober: behaviour per (host, ip). Unknown endpoints fail with a TCP timeout.</summary>
public sealed class FakeProber : IEndpointProber
{
    private readonly ConcurrentDictionary<string, Func<int, CancellationToken, Task<ProbeResult>>> _script = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentBag<string> Probed { get; } = [];

    public int CallsTo(string host, string ip) => _calls.TryGetValue(Key(host, ip), out var c) ? c : 0;

    public FakeProber Ok(string host, string ip, int status = 200, int ms = 50) =>
        On(host, ip, (_, _) => Task.FromResult(Result(ip, ProbeStage.HttpResponded, ProbeFailure.None, ms, status)));

    public FakeProber Fails(string host, string ip, ProbeFailure failure, ProbeStage stage = ProbeStage.None) =>
        On(host, ip, (_, _) => Task.FromResult(Result(ip, stage, failure, 5000)));

    public FakeProber BadCertificate(string host, string ip) =>
        On(host, ip, (_, _) => Task.FromResult(Result(ip, ProbeStage.TcpConnected, ProbeFailure.CertificateInvalid, 80) with
        {
            CertificateErrors = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors,
            CertificateIssuer = "CN=Some Block Page",
        }));

    public FakeProber Hangs(string host, string ip) =>
        On(host, ip, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

    /// <summary>First call succeeds, later calls fail (an unstable address).</summary>
    public FakeProber Flaky(string host, string ip) =>
        On(host, ip, (n, _) => Task.FromResult(n == 1
            ? Result(ip, ProbeStage.HttpResponded, ProbeFailure.None, 40, 200)
            : Result(ip, ProbeStage.TcpConnected, ProbeFailure.TlsReset, 100)));

    public FakeProber On(string host, string ip, Func<int, CancellationToken, Task<ProbeResult>> behaviour)
    {
        _script[Key(host, ip)] = behaviour;
        return this;
    }

    public Task<ProbeResult> ProbeAsync(string host, IPAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(host, address.ToString());
        Probed.Add(key);
        var n = _calls.AddOrUpdate(key, 1, (_, c) => c + 1);
        return _script.TryGetValue(key, out var behaviour)
            ? behaviour(n, cancellationToken)
            : Task.FromResult(Result(address.ToString(), ProbeStage.None, ProbeFailure.TcpTimeout, 5000));
    }

    private static string Key(string host, string ip) => $"{host}|{ip}";

    private static ProbeResult Result(string ip, ProbeStage stage, ProbeFailure failure, int ms, int? status = null) =>
        new(IPAddress.Parse(ip), stage, failure, TimeSpan.FromMilliseconds(ms), status,
            CertificateSubject: stage >= ProbeStage.TlsEstablished ? "CN=riotgames.com" : null,
            TlsProtocol: stage >= ProbeStage.TlsEstablished ? "Tls13" : null);
}

public sealed class FakeProxy(bool configured = false) : IProxyDetector
{
    public bool IsProxyConfigured(string host) => configured;
}
