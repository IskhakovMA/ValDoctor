using System.Net;
using System.Text;
using ValDoctor.Core.Diagnostics;
using ValDoctor.Core.Elevation;
using ValDoctor.Core.Hosts;
using ValDoctor.Core.Logging;
using ValDoctor.Core.Network;
using ValDoctor.Core.Tests.Fakes;

namespace ValDoctor.Core.Tests;

public class AddressPolicyTests
{
    [Theory]
    [InlineData("104.16.206.131", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("2606:4700::6810:ce83", true)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:104.16.1.1", false)]
    public void Classifies_public_unicast(string ip, bool expected)
    {
        Assert.Equal(expected, AddressPolicy.IsPublicUnicast(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("104.16.206.131", true)]
    [InlineData("2606:4700::1", true)]
    [InlineData("1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3.256", false)]
    [InlineData("01.2.3.4", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("fe80::1%3", false)]
    [InlineData("", false)]
    [InlineData("host", false)]
    public void Parses_strictly(string text, bool expected)
    {
        Assert.Equal(expected, AddressPolicy.TryParseStrict(text, out _));
    }
}

public class ElevatedProtocolTests
{
    private const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";

    [Fact]
    public void Round_trips_apply_request()
    {
        var request = ElevatedRequest.Apply(
            [new HostPin("auth.riotgames.com", IPAddress.Parse("104.16.206.131")), new HostPin("account.riotgames.com", IPAddress.Parse("2606:4700::1"))],
            disableConflicts: true);

        var parsed = ElevatedProtocol.Parse(ElevatedProtocol.ToArguments(request));

        Assert.Equal(ElevatedOperation.Apply, parsed.Operation);
        Assert.Equal(request.Pins, parsed.Pins);
        Assert.True(parsed.DisableConflicts);
    }

    [Fact]
    public void Round_trips_revert_request()
    {
        Assert.Equal(ElevatedOperation.Revert, ElevatedProtocol.Parse(ElevatedProtocol.ToArguments(ElevatedRequest.Revert())).Operation);
    }

    [Theory]
    [InlineData("apply", "--pin", "evil.example=104.16.1.1")]
    [InlineData("apply", "--pin", "auth.riotgames.com=127.0.0.1")]
    [InlineData("apply", "--pin", "auth.riotgames.com=192.168.0.1")]
    [InlineData("apply", "--pin", "auth.riotgames.com=1")]
    [InlineData("apply", "--pin", "auth.riotgames.com=1.2.3.4\r\n0.0.0.0 x")]
    [InlineData("apply", "--pin", "auth.riotgames.com=1.2.3.4=5")]
    [InlineData("apply", "--pin")]
    [InlineData("apply", "--hosts-path", "C:\\x")]
    [InlineData("delete")]
    [InlineData("revert", "--pin", "auth.riotgames.com=104.16.1.1")]
    public void Rejects_anything_outside_the_contract(params string[] rest)
    {
        Assert.Throws<ArgumentException>(() => ElevatedProtocol.Parse([ElevatedProtocol.Flag, .. rest]));
    }

    [Fact]
    public void Rejects_duplicate_hosts()
    {
        Assert.Throws<ArgumentException>(() => ElevatedProtocol.Parse(
            [ElevatedProtocol.Flag, "apply", "--pin", "auth.riotgames.com=104.16.1.1", "--pin", "AUTH.riotgames.com=104.16.1.2"]));
    }

    [Fact]
    public void Runner_applies_flushes_and_is_idempotent()
    {
        var fs = new FakeFileSystem();
        fs.Seed(HostsPath, Encoding.ASCII.GetBytes("127.0.0.1 localhost\r\n"));
        var service = new HostsFileService(fs, HostsPath, NullLog.Instance, _ => { });
        var flushed = 0;
        var args = ElevatedProtocol.ToArguments(ElevatedRequest.Apply([new HostPin("auth.riotgames.com", IPAddress.Parse("104.16.206.131"))], false));

        Assert.Equal(ElevatedExitCode.Success, ElevatedRunner.Run(args, service, () => flushed++, NullLog.Instance));
        Assert.Equal(ElevatedExitCode.NoChanges, ElevatedRunner.Run(args, service, () => flushed++, NullLog.Instance));
        Assert.Equal(1, flushed);

        Assert.Equal(ElevatedExitCode.Success, ElevatedRunner.Run(ElevatedProtocol.ToArguments(ElevatedRequest.Revert()), service, () => flushed++, NullLog.Instance));
        Assert.Equal("127.0.0.1 localhost\r\n", Encoding.ASCII.GetString(fs.Get(HostsPath)));
    }

    [Fact]
    public void Runner_maps_errors_to_exit_codes()
    {
        var fs = new FakeFileSystem();
        fs.Seed(HostsPath, Encoding.ASCII.GetBytes("5.6.7.8 auth.riotgames.com\r\n"));
        var service = new HostsFileService(fs, HostsPath, NullLog.Instance, _ => { });
        var args = ElevatedProtocol.ToArguments(ElevatedRequest.Apply([new HostPin("auth.riotgames.com", IPAddress.Parse("104.16.206.131"))], false));

        Assert.Equal(ElevatedExitCode.Conflict, ElevatedRunner.Run(args, service, () => { }, NullLog.Instance));
        Assert.Equal(ElevatedExitCode.InvalidArguments, ElevatedRunner.Run([ElevatedProtocol.Flag, "nope"], service, () => { }, NullLog.Instance));

        fs.Seed(HostsPath, Encoding.ASCII.GetBytes("127.0.0.1 localhost\r\n"), FileAttributes.ReadOnly);
        Assert.Equal(ElevatedExitCode.ReadOnly, ElevatedRunner.Run(args, service, () => { }, NullLog.Instance));

        fs.Seed(HostsPath, Encoding.ASCII.GetBytes("127.0.0.1 localhost\r\n"));
        fs.BeforeOperation = (op, _) =>
        {
            if (op == "replace")
            {
                throw new UnauthorizedAccessException();
            }
        };
        Assert.Equal(ElevatedExitCode.AccessDenied, ElevatedRunner.Run(args, service, () => { }, NullLog.Instance));
    }
}

public class TextsTests
{
    [Theory]
    [InlineData(1, "1 сервис")]
    [InlineData(2, "2 сервиса")]
    [InlineData(5, "5 сервисов")]
    [InlineData(6, "6 сервисов")]
    [InlineData(11, "11 сервисов")]
    [InlineData(21, "21 сервис")]
    public void Pluralizes_russian(int n, string expected) => Assert.Equal(expected, Texts.Services(n));

    private static DomainDiagnosis Domain(string host, DomainHealth health) =>
        new(RiotServices.Find(host)!, new SystemDnsAnswer([IPAddress.Parse("104.16.1.1")], DnsFailureKind.None), [], health, HostsOverride.None, [], []);

    private static DiagnosisReport Report(Verdict verdict, params DomainDiagnosis[] domains) =>
        new(DateTimeOffset.Now, TimeSpan.FromSeconds(1), domains, false, true, false, [], FixPlan.Empty, verdict);

    [Fact]
    public void Healthy_message_states_scope_and_never_claims_the_game_works()
    {
        var msg = Texts.ForReport(Report(Verdict.AllHealthy, Domain("auth.riotgames.com", DomainHealth.Healthy)));
        Assert.Equal(StatusTone.Success, msg.Tone);
        Assert.Equal("Проблем с доступом к сервисам Riot не обнаружено.", msg.Body);
        Assert.DoesNotContain("игра работает", msg.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("гарантир", msg.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void After_fix_reports_only_what_the_recheck_confirmed()
    {
        var pin = new HostPin("auth.riotgames.com", IPAddress.Parse("104.18.1.1"));

        var ok = Texts.AfterFix(Report(Verdict.AllHealthy, Domain("auth.riotgames.com", DomainHealth.Healthy)), [pin]);
        Assert.Equal(StatusTone.Success, ok.Tone);
        Assert.Equal("Перезапустите Riot Client и попробуйте войти.", ok.Body);

        // A pinned host that still fails is reported, but rollback is only suggested if sign-in does not work.
        var failed = Texts.AfterFix(Report(Verdict.ProblemsWithoutFix, Domain("auth.riotgames.com", DomainHealth.Failed)), [pin]);
        Assert.Equal(StatusTone.Warning, failed.Tone);
        Assert.Contains("Если не получится", failed.Body, StringComparison.Ordinal);
        Assert.Contains("Отменить изменения", failed.Body, StringComparison.Ordinal);

        var partial = Texts.AfterFix(Report(Verdict.ProblemsWithoutFix,
            Domain("auth.riotgames.com", DomainHealth.Healthy), Domain("clientconfig.rpg.riotgames.com", DomainHealth.Failed)), [pin]);
        Assert.Equal(StatusTone.Warning, partial.Tone);
        Assert.Contains("Часть проблем осталась", partial.Body, StringComparison.Ordinal);
        Assert.Empty(PlainTextPolicy.FindTechnicalTerms(partial.Body));
    }
}
