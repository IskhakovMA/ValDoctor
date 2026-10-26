using RiotFix.Core.Hosts;
using RiotFix.Core.Network;

namespace RiotFix.Core.Diagnostics;

public enum StatusTone
{
    Neutral,
    Success,
    Warning,
    Error,
}

public sealed record StatusMessage(StatusTone Tone, string Headline, string Body);

/// <summary>
/// All user-facing wording. Kept in Core so tests can assert that RiotFix never claims more than it verified:
/// the checks cover DNS, TCP 443, TLS (certificate) and an HTTP response — not sign-in or game servers.
/// </summary>
public static class Texts
{
    public const string ScopeNote = "Проверяются DNS, подключение TCP 443, сертификат TLS и HTTPS-ответ сервера. Это не гарантирует успешный вход и работу игровых серверов.";

    public static string Services(int count) => $"{count} {Plural(count, "сервис", "сервиса", "сервисов")}";

    public static string Plural(int n, string one, string few, string many)
    {
        var mod100 = Math.Abs(n) % 100;
        var mod10 = mod100 % 10;
        if (mod100 is >= 11 and <= 14)
        {
            return many;
        }

        return mod10 switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many,
        };
    }

    // Main-screen wording. Plain language only: no host names, IP addresses, protocol names or file paths —
    // those live in the technical report (ReportFormatter), opened from «Подробности».
    public static StatusMessage ForReport(DiagnosisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var essentialDown = report.Domains.Any(d => d.IsEssential && !d.IsReachable);

        return report.Verdict switch
        {
            Verdict.AllHealthy => new(StatusTone.Success, "Подключение работает",
                "Проблем с доступом к сервисам Riot не обнаружено."),
            Verdict.LimitedConnectivity => new(StatusTone.Success, "Подключение работает",
                "Основные сервисы Riot отвечают. Один из вспомогательных сейчас недоступен — вход и игра при этом могут работать."),
            Verdict.FixAvailable when essentialDown => new(StatusTone.Warning, "Обнаружена проблема с подключением",
                "RiotFix нашёл проверенное исправление. Нажмите «Исправить подключение»."),
            Verdict.FixAvailable => new(StatusTone.Warning, "Обнаружена проблема с подключением",
                "Основные сервисы Riot отвечают, но один из вспомогательных недоступен. Если Riot Client работает, исправлять не обязательно."),
            Verdict.ProblemsWithoutFix => new(StatusTone.Error, "Не удалось найти безопасное исправление",
                $"Ничего не изменено. {NoFixReason(report)}"),
            Verdict.ServiceErrors => new(StatusTone.Warning, "Есть проблемы с подключением",
                "Похоже, сбой на стороне Riot. На этом компьютере его не исправить — попробуйте позже."),
            Verdict.NoConnectivity => new(StatusTone.Error, "Нет подключения к интернету",
                "Проверьте подключение к сети и нажмите «Проверить снова»."),
            _ => new(StatusTone.Neutral, "Проверка завершена", string.Empty),
        };
    }

    /// <summary>
    /// Outcome after applying a fix, based only on the re-check that followed it. Never promises that sign-in works
    /// and never recommends an automatic rollback just because some services still fail.
    /// </summary>
    public static StatusMessage AfterFix(DiagnosisReport after, IReadOnlyCollection<HostPin> applied)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(applied);
        var appliedHosts = applied.Select(p => p.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stillDown = after.Domains.Where(d => !d.IsReachable).ToList();
        var pinnedStillDown = stillDown.Count(d => appliedHosts.Contains(d.Host));
        const string Headline = "Изменения применены";
        const string Restart = "Перезапустите Riot Client и попробуйте войти.";

        if (stillDown.Count == 0 || after.EssentialReachable && pinnedStillDown < applied.Count)
        {
            return new(StatusTone.Success, Headline, Restart);
        }

        if (applied.Count > 0 && pinnedStillDown == applied.Count)
        {
            return new(StatusTone.Warning, Headline,
                $"Проверка показывает, что проблема осталась. {Restart} Если не получится, нажмите «Отменить изменения».");
        }

        return new(StatusTone.Warning, Headline, $"Часть проблем осталась. {Restart}");
    }

    /// <summary>A short, jargon-free recommendation for the case without a safe fix.</summary>
    private static string NoFixReason(DiagnosisReport report)
    {
        var reasons = report.Plan.Unfixable.Select(u => u.Reason).ToHashSet();
        if (reasons.Contains(UnfixableReason.HostsUnreadable))
        {
            return "Не удалось прочитать системные настройки сети. Попробуйте проверить ещё раз.";
        }

        if (reasons.Contains(UnfixableReason.ConflictCannotBeDisabled) && !reasons.Contains(UnfixableReason.NoVerifiedCandidate))
        {
            return "В системных настройках уже есть чужие правила для Riot — RiotFix их не трогает.";
        }

        return "Попробуйте позже или перезагрузите роутер. Вход в игру при этом может работать.";
    }

    public static string Health(DomainHealth health) => health switch
    {
        DomainHealth.Healthy => "Доступен",
        DomainHealth.PartiallyReachable => "Доступен (не все адреса)",
        DomainHealth.Degraded => "Работает нестабильно",
        DomainHealth.ServerError => "Отвечает с ошибкой",
        DomainHealth.Failed => "Недоступен",
        _ => health.ToString(),
    };

    public static string Failure(ProbeFailure failure) => failure switch
    {
        ProbeFailure.None => "без ошибок",
        ProbeFailure.NotPublicAddress => "адрес не публичный — похоже на блокировку через DNS",
        ProbeFailure.TcpTimeout => "нет ответа на подключение к порту 443",
        ProbeFailure.TcpRefused => "подключение отклонено",
        ProbeFailure.TcpReset => "соединение сброшено",
        ProbeFailure.NetworkUnreachable => "сеть недоступна (нет маршрута)",
        ProbeFailure.TlsTimeout => "нет ответа при установке защищённого соединения",
        ProbeFailure.TlsReset => "защищённое соединение сброшено — возможна блокировка",
        ProbeFailure.TlsHandshakeFailed => "не удалось установить защищённое соединение",
        ProbeFailure.CertificateInvalid => "недействительный сертификат — подключение небезопасно",
        ProbeFailure.HttpTimeout => "сервер не ответил на HTTPS-запрос",
        ProbeFailure.HttpReset => "соединение сброшено во время HTTPS-запроса",
        ProbeFailure.HttpInvalidResponse => "некорректный ответ сервера",
        ProbeFailure.Timeout => "превышено время ожидания",
        ProbeFailure.Error => "ошибка сети",
        _ => failure.ToString(),
    };

    public static string Dns(DnsFailureKind kind) => kind switch
    {
        DnsFailureKind.None => "ок",
        DnsFailureKind.NotFound => "имя не найдено",
        DnsFailureKind.Timeout => "нет ответа DNS",
        DnsFailureKind.Error => "ошибка DNS",
        _ => kind.ToString(),
    };

    public static string Unfixable(UnfixableReason reason) => reason switch
    {
        UnfixableReason.NoVerifiedCandidate => "нет адреса, прошедшего проверку",
        UnfixableReason.ConflictCannotBeDisabled => "в hosts есть сторонняя строка, которая затрагивает и другие сайты — RiotFix её не трогает",
        UnfixableReason.HostsUnreadable => "не удалось прочитать файл hosts",
        _ => reason.ToString(),
    };

    public static string HostsError(HostsErrorKind kind) => kind switch
    {
        HostsErrorKind.AccessDenied => "Нет доступа к файлу hosts. Возможно, его защищает антивирус (например, «Контролируемый доступ к папкам» в Защитнике Windows). RiotFix не отключает защиту — разрешите изменение вручную или добавьте записи сами.",
        HostsErrorKind.ReadOnly => "Файл hosts помечен как «Только для чтения». RiotFix не снимает этот атрибут сам: снимите его в свойствах файла, если доверяете изменению, и повторите.",
        HostsErrorKind.Locked => "Файл hosts занят другой программой (часто — антивирусом). Закройте её или повторите попытку позже.",
        HostsErrorKind.ConcurrentModification => "Файл hosts постоянно меняется другой программой. RiotFix ничего не записал, чтобы не потерять чужие изменения.",
        HostsErrorKind.VerificationFailed => "После записи содержимое файла hosts не совпало с ожидаемым. Проверьте антивирус; резервная копия сохранена рядом с файлом hosts.",
        HostsErrorKind.UnsupportedFormat => "Не удалось распознать формат файла hosts. RiotFix не стал его изменять.",
        HostsErrorKind.IoError => "Не удалось прочитать или записать файл hosts.",
        _ => "Ошибка при работе с файлом hosts.",
    };
}
