using System.Globalization;
using System.Net.Security;
using System.Text;
using ValDoctor.Core.Network;

namespace ValDoctor.Core.Diagnostics;

/// <summary>Plain-text report for the "Детали" section and for copying. Contains no personal data.</summary>
public static class ReportFormatter
{
    public static string Format(DiagnosisReport report, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(report);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(ci, $"ValDoctor {appVersion} — отчёт о проверке").AppendLine();
        sb.Append(ci, $"Время: {report.StartedAt:yyyy-MM-dd HH:mm:ss zzz}, длительность {report.Duration.TotalSeconds:0.0} с").AppendLine();
        sb.Append("Системный прокси: ").AppendLine(report.ProxyConfigured
            ? "настроен (Riot Client может подключаться через него — тогда записи hosts могут не действовать)"
            : "не обнаружен");
        sb.Append("Файл hosts: ").AppendLine(!report.HostsReadable ? "не удалось прочитать"
            : report.CurrentPins.Count > 0 ? $"есть записи ValDoctor ({report.CurrentPins.Count})"
            : report.HasManagedContent ? "есть служебные строки ValDoctor" : "записей ValDoctor нет");
        if (report.HasLegacyRiotFixContent)
        {
            sb.AppendLine("  В том числе строки, записанные RiotFix (прежнее название ValDoctor), — они распознаются и откатываются.");
        }
        sb.AppendLine(Texts.ScopeNote);
        sb.AppendLine();

        foreach (var d in report.Domains)
        {
            sb.Append(ci, $"■ {d.Host} — {Texts.Health(d.Health)}").AppendLine();
            if (d.Override != HostsOverride.None)
            {
                sb.Append("  Адрес задан в hosts: ").AppendLine(d.Override == HostsOverride.ManagedPin ? "запись ValDoctor" : "сторонняя запись");
                if (d.ManagedPinConfirmedByTrustedDns == false)
                {
                    sb.AppendLine("  ! Закреплённый адрес не подтверждает ни один доверенный DNS. Если вы не уверены в нём, нажмите «Отменить изменения» и проверьте снова.");
                }
            }

            if (d.DnsRetried)
            {
                sb.AppendLine(d.SystemDns.Success
                    ? "  Системный DNS ответил не сразу: ответ получен после повторного запроса."
                    : "  Системный DNS не ответил и после повторных запросов.");
            }

            sb.Append("  Системный DNS: ").AppendLine(d.SystemDns.Success
                ? string.Join(", ", d.SystemDns.Addresses)
                : Texts.Dns(d.SystemDns.Failure));

            foreach (var p in d.SystemProbes)
            {
                sb.Append("    ").AppendLine(DescribeProbe(p));
            }

            if (d.TrustedAnswers.Count > 0)
            {
                sb.AppendLine("  Доверенные DNS (DNS-over-HTTPS):");
                foreach (var t in d.TrustedAnswers)
                {
                    sb.Append(ci, $"    {t.ResolverName}: ").AppendLine(t.Responded
                        ? (t.Addresses.Count > 0 ? string.Join(", ", t.Addresses) : "адресов нет")
                        : "нет ответа");
                }

                if (d.SystemMatchesTrusted == false && d.Override == HostsOverride.None)
                {
                    sb.AppendLine("    ! Системный DNS вернул адреса, которых нет в ответах доверенных DNS.");
                }
            }

            if (d.Candidates.Count > 0)
            {
                sb.AppendLine("  Проверка альтернативных адресов:");
                foreach (var c in d.Candidates)
                {
                    var verdict = c.Verified
                        ? $"подтверждён {c.Probes.Count} раза, ~{c.AverageLatency.TotalMilliseconds:0} мс"
                        : "не прошёл проверку: " + Texts.Failure(c.Probes.FirstOrDefault(p => !p.Healthy)?.Failure ?? ProbeFailure.Error);
                    sb.Append(ci, $"    {c.Address} ({string.Join(", ", c.Sources)}) — {verdict}").AppendLine();
                }
            }

            sb.AppendLine();
        }

        var plan = report.Plan;
        if (plan.NewPins.Count > 0 || plan.Unfixable.Count > 0)
        {
            sb.AppendLine("План исправления:");
            foreach (var pin in plan.NewPins)
            {
                sb.Append(ci, $"  + {pin.Address} {pin.Host}").AppendLine();
            }

            foreach (var pin in plan.ReplacedPins)
            {
                sb.Append(ci, $"  − {pin.Address} {pin.Host} (заменяется)").AppendLine();
            }

            foreach (var pin in plan.RemovedPins)
            {
                sb.Append(ci, $"  − {pin.Address} {pin.Host} (запись ValDoctor будет удалена из-за сторонней строки)").AppendLine();
            }

            foreach (var c in plan.ConflictsToDisable)
            {
                sb.Append(ci, $"  # будет закомментирована сторонняя строка: {c.LineText.Trim()}").AppendLine();
            }

            foreach (var u in plan.Unfixable)
            {
                sb.Append(ci, $"  ✕ {u.Host}: {Texts.Unfixable(u.Reason)}").AppendLine();
            }
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string DescribeProbe(ProbeResult p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var ci = CultureInfo.InvariantCulture;
        if (p.Reachable)
        {
            var status = p.HttpStatus switch
            {
                401 or 403 => $"HTTP {p.HttpStatus} (сервер доступен; ответ об авторизации не означает проблему сети)",
                >= 500 => $"HTTP {p.HttpStatus} (ошибка на стороне сервера)",
                _ => $"HTTP {p.HttpStatus}",
            };
            return string.Create(ci, $"{p.Address}: TCP ✓ TLS ✓ ({p.TlsProtocol}, сертификат действителен) {status}, {p.Elapsed.TotalMilliseconds:0} мс");
        }

        var stage = p.Stage switch
        {
            ProbeStage.None => "TCP ✕",
            ProbeStage.TcpConnected => "TCP ✓ TLS ✕",
            ProbeStage.TlsEstablished => "TCP ✓ TLS ✓ HTTP ✕",
            _ => string.Empty,
        };
        var cert = p.Failure == ProbeFailure.CertificateInvalid
            ? $" [{DescribeCertErrors(p.CertificateErrors)}; выдан: {p.CertificateIssuer ?? "?"}]"
            : string.Empty;
        return string.Create(ci, $"{p.Address}: {stage} — {Texts.Failure(p.Failure)}{cert}");
    }

    private static string DescribeCertErrors(SslPolicyErrors errors)
    {
        var parts = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            parts.Add("сертификат выдан для другого имени");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            parts.Add("цепочка сертификата не доверена");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            parts.Add("сертификат не предоставлен");
        }

        return parts.Count == 0 ? "ошибка проверки" : string.Join(", ", parts);
    }
}
