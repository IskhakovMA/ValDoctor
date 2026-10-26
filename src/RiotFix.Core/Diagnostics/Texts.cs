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

    public static StatusMessage ForReport(DiagnosisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var total = report.Domains.Count;
        var unreachable = report.Domains.Where(d => !d.IsReachable).ToList();
        var essentialDown = unreachable.Where(d => d.IsEssential).ToList();
        var partial = report.Domains.Count(d => d.Health == DomainHealth.PartiallyReachable);
        var partialNote = partial > 0
            ? $" У {partial} {Plural(partial, "сервиса", "сервисов", "сервисов")} не отвечает часть адресов, но рабочие адреса есть — Windows переключается на них сама."
            : string.Empty;

        return report.Verdict switch
        {
            Verdict.AllHealthy => new(StatusTone.Success, "Подключение в порядке",
                $"Все {Services(total)} Riot отвечают по сети.{partialNote} {ScopeNote}"),
            Verdict.FixAvailable => new(StatusTone.Warning,
                essentialDown.Count > 0 ? "Найдены проблемы с подключением" : "Основные сервисы отвечают, есть ограничения",
                $"Не отвечают {unreachable.Count} из {Services(total)}" +
                (essentialDown.Count == 0 ? " (только вспомогательные)" : string.Empty) +
                $". Для {report.Plan.NewPins.Count} найден проверенный рабочий адрес." +
                (report.Plan.Unfixable.Count > 0
                    ? $" Для {report.Plan.Unfixable.Count} {Plural(report.Plan.Unfixable.Count, "сервиса", "сервисов", "сервисов")} безопасного исправления нет."
                    : string.Empty) +
                (essentialDown.Count == 0
                    ? " Если Riot Client у вас работает, исправление не обязательно."
                    : " Нажмите «Исправить», чтобы RiotFix добавил записи в файл hosts.")),
            Verdict.LimitedConnectivity => new(StatusTone.Warning, "Основные сервисы отвечают",
                $"Сервисы входа, настроек клиента и доступа к играм отвечают. Не отвечают вспомогательные: {HostList(unreachable)}. " +
                "Проверенного рабочего адреса для них нет, поэтому файл hosts не изменяется. " +
                "Недоступность этих сервисов не всегда мешает входу: на реальном ПК вход и игра работали и в такой ситуации."),
            Verdict.ProblemsWithoutFix => new(StatusTone.Error, "Найдены проблемы, исправления нет",
                $"Не отвечают {unreachable.Count} из {Services(total)}: {HostList(unreachable)}. RiotFix не нашёл адрес, который прошёл бы проверку, поэтому файл hosts изменён не будет. " +
                "Это сетевая проверка: она не доказывает, что вход невозможен. Возможные причины — блокировка, которую не обходит смена адреса, или временный сбой сервисов."),
            Verdict.ServiceErrors => new(StatusTone.Warning, "Сервисы отвечают с ошибками",
                "Серверы Riot доступны по сети, но возвращают ошибки (HTTP 5xx). Скорее всего, это проблема на стороне Riot Games — изменение hosts здесь не поможет."),
            Verdict.NoConnectivity => new(StatusTone.Error, "Нет подключения к интернету",
                "Не удалось связаться ни с сервисами Riot, ни с проверочными DNS-серверами. Проверьте подключение к сети и повторите проверку."),
            _ => new(StatusTone.Neutral, "Проверка завершена", string.Empty),
        };
    }

    /// <summary>
    /// Outcome after applying a fix, based only on the re-check that followed it. Never recommends an automatic
    /// rollback just because some services still fail: on a real PC sign-in and a game worked in exactly that state.
    /// </summary>
    public static StatusMessage AfterFix(DiagnosisReport after, IReadOnlyCollection<HostPin> applied)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(applied);
        var total = after.Domains.Count;
        var reachable = after.HealthyCount;
        var appliedHosts = applied.Select(p => p.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stillDown = after.Domains.Where(d => !d.IsReachable).ToList();
        var pinnedStillDown = stillDown.Where(d => appliedHosts.Contains(d.Host)).ToList();

        if (stillDown.Count == 0)
        {
            return new(StatusTone.Success, "Исправление применено",
                $"Повторная проверка: отвечают все {Services(total)}. Перезапустите Riot Client, чтобы он подключился заново. {ScopeNote}");
        }

        if (applied.Count > 0 && pinnedStillDown.Count == applied.Count)
        {
            return new(StatusTone.Warning, "Исправление не дало результата",
                $"Повторная проверка: отвечают {reachable} из {Services(total)}. Сервисы с новыми записями по-прежнему не отвечают: {HostList(pinnedStillDown)}. " +
                "Перезапустите Riot Client и попробуйте войти. Если вход не работает, отмените изменения кнопкой «Отменить изменения».");
        }

        if (after.EssentialReachable)
        {
            return new(StatusTone.Success, "Исправление применено",
                $"Повторная проверка: основные сервисы входа отвечают ({reachable} из {Services(total)}). " +
                $"Не отвечают вспомогательные: {HostList(stillDown)} — это не всегда мешает входу. Перезапустите Riot Client и попробуйте войти.");
        }

        return new(StatusTone.Warning, "Исправление применено частично",
            $"Повторная проверка: отвечают {reachable} из {Services(total)}. Не отвечают: {HostList(stillDown)}. " +
            "Перезапустите Riot Client и попробуйте войти — сетевая проверка не всегда совпадает с работой клиента.");
    }

    private static string HostList(IEnumerable<DomainDiagnosis> domains) => string.Join(", ", domains.Select(d => d.Host));

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
