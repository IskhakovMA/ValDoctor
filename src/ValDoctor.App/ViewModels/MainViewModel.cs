using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using ValDoctor.App.Services;
using ValDoctor.Core;
using ValDoctor.Core.Diagnostics;
using ValDoctor.Core.Elevation;
using ValDoctor.Core.Hosts;
using ValDoctor.Core.Logging;
using ValDoctor.Core.Network;
using ValDoctor.Core.Updates;

namespace ValDoctor.App.ViewModels;

/// <summary>Wires the Core engine to Windows: hosts path, DoH client, prober, elevation, logs.</summary>
internal sealed class AppComposition
{
    private static readonly HttpClient DohHttp = new(DohResolver.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly HttpClient UpdateHttp = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(6) };

    private AppComposition(IAppLog log, HostsFileService hosts)
    {
        Log = log;
        Hosts = hosts;
        Elevation = new ElevationService(hosts, log);
    }

    public IAppLog Log { get; }

    public HostsFileService Hosts { get; }

    public ElevationService Elevation { get; }

    public static AppComposition Create(IAppLog log) =>
        new(log, new HostsFileService(new PhysicalFileSystem(), HostsLocator.GetHostsPath(), log));

    public IEndpointProber Prober { get; } = new TlsEndpointProber(new ProbeOptions { UserAgent = $"ValDoctor/{AppInfo.Version} (connectivity check)" });

    /// <summary>Read-only diagnosis. Never flushes the DNS cache or changes anything else on the system.</summary>
    public DiagnosticsEngine CreateEngine() => DiagnosticsEngine.CreateDefault(
        new SystemResolver(),
        TrustedResolvers.Defaults.Select(d => (ITrustedResolver)new DohResolver(d, DohHttp, TimeSpan.FromSeconds(4))).ToList(),
        Prober,
        new SystemProxyDetector(),
        Hosts.Load,
        Log);

    public FixPreflight CreatePreflight(Func<CancellationToken, Task<DiagnosisReport>> diagnose) =>
        new(diagnose, Prober, Hosts.Load, log: Log, systemDns: new SystemResolver());

    public UpdateChecker CreateUpdateChecker() => new(
        UpdateHttp,
        new FileUpdateStateStore(Path.Join(AppInfo.DataDirectory, "update-state.json"), Path.Join(AppInfo.LegacyDataDirectory, "update-state.json")),
        AppInfo.SemanticVersion,
        log: Log);
}

internal sealed class MainViewModel : ObservableObject
{
    private readonly AppComposition _app;
    private readonly IDialogs _dialogs;
    private CancellationTokenSource? _cts;
    private DiagnosisReport? _report;

    private StatusTone _tone = StatusTone.Neutral;
    private string _headline = "Готово к проверке";
    private string _body = "ValDoctor проверит, доступны ли сервисы Riot Games с этого компьютера, и предложит исправление, если найдёт проблему. Без вашего согласия ничего не меняется.";
    private bool _isBusy;
    private bool _canCancel;
    private string _progressText = string.Empty;
    private bool _fixReady;
    private bool _hasChecked;
    private bool _canRevert;
    private string _details = "Проверка ещё не выполнялась.";
    private ReleaseInfo? _update;
    private bool _statusChanged;
    private UpdateChecker? _updateChecker;

    public MainViewModel(AppComposition app, IDialogs dialogs)
    {
        _app = app;
        _dialogs = dialogs;
        CheckCommand = new AsyncCommand(CheckAsync, () => !IsBusy);
        FixCommand = new AsyncCommand(FixAsync, () => !IsBusy && _fixReady);
        RevertCommand = new AsyncCommand(RevertAsync, () => !IsBusy && CanRevert);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => CanCancel);
        CopyReportCommand = new RelayCommand(CopyReport);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        OpenRepositoryCommand = new RelayCommand(() => OpenUrl(AppInfo.RepositoryUrl));
        DownloadUpdateCommand = new RelayCommand(() =>
        {
            if (_update is not null)
            {
                OpenUrl(_update.Url.AbsoluteUri);
            }
        });
        DismissUpdateCommand = new RelayCommand(DismissUpdate);
        OpenDetailsCommand = new RelayCommand(() => _dialogs.ShowDetails(this));
        RefreshHostsState();
        if (CanRevert)
        {
            _body += " Изменения, внесённые раньше (в том числе программой под прежним названием RiotFix), можно отменить кнопкой «Отменить изменения».";
        }
    }

    public AsyncCommand CheckCommand { get; }

    public AsyncCommand FixCommand { get; }

    public AsyncCommand RevertCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand CopyReportCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public RelayCommand OpenRepositoryCommand { get; }

    public RelayCommand DownloadUpdateCommand { get; }

    public RelayCommand DismissUpdateCommand { get; }

    public bool HasUpdate => _update is not null;

    public string UpdateHeadline => _update is null ? string.Empty : $"Доступна новая версия {_update.Version}";

    public string UpdateSummary => _update is null ? string.Empty
        : string.IsNullOrEmpty(_update.Summary) ? _update.Title : _update.Summary;

    /// <summary>
    /// Non-blocking update check (at most once per 24 h, silent on any failure). Only reads GitHub Releases;
    /// nothing is downloaded or started.
    /// </summary>
    public async void StartUpdateCheck(UpdateChecker checker)
    {
        _updateChecker = checker;
        try
        {
            var release = await Task.Run(() => checker.CheckAsync(CancellationToken.None));
            if (release is not null)
            {
                ShowUpdate(release);
            }
        }
        catch (Exception ex)
        {
            _app.Log.Info($"Update check failed silently: {ex.GetType().Name}.");
        }
    }

    /// <summary>Also used by the smoke test.</summary>
    public void ShowUpdate(ReleaseInfo? release)
    {
        _update = release;
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateHeadline));
        OnPropertyChanged(nameof(UpdateSummary));
    }

    /// <summary>Opens the technical details window (report, logs, hosts path). Never shown by default.</summary>
    public RelayCommand OpenDetailsCommand { get; }

    public string Version { get; } = $"Версия {AppInfo.Version}";

    public string HostsPath => _app.Hosts.HostsPath;

    public string BackupPath
    {
        get
        {
            try
            {
                return _app.Hosts.EffectiveOriginalBackupPath; // RiotFix's original backup if it made one
            }
            catch (HostsAccessException)
            {
                return _app.Hosts.OriginalBackupPath;
            }
        }
    }

    public StatusTone Tone { get => _tone; private set => SetProperty(ref _tone, value); }

    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }

    public string Body { get => _body; private set => SetProperty(ref _body, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public string Details { get => _details; private set => SetProperty(ref _details, value); }


    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(ShowRecheckLink));
                OnPropertyChanged(nameof(ShowRevertLink));
                RaiseCommands();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public bool CanCancel
    {
        get => _canCancel;
        private set
        {
            if (SetProperty(ref _canCancel, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when the last check produced a verified plan: the primary button becomes «Исправить».</summary>
    public bool FixReady
    {
        get => _fixReady;
        private set
        {
            if (SetProperty(ref _fixReady, value))
            {
                OnPropertyChanged(nameof(ShowCheckAsPrimary));
                OnPropertyChanged(nameof(ShowRecheckLink));
                RaiseCommands();
            }
        }
    }

    public bool ShowCheckAsPrimary => !FixReady;

    /// <summary>«Проверить снова» as a quiet link while «Исправить подключение» is the main button.</summary>
    public bool ShowRecheckLink => FixReady && !IsBusy;

    /// <summary>«Отменить изменения» appears only when ValDoctor changed something before.</summary>
    public bool ShowRevertLink => CanRevert && !IsBusy;

    public string CheckText => _hasChecked ? "Проверить снова" : "Проверить подключение";

    public bool CanRevert
    {
        get => _canRevert;
        private set
        {
            if (SetProperty(ref _canRevert, value))
            {
                OnPropertyChanged(nameof(ShowRevertLink));
                RaiseCommands();
            }
        }
    }

    /// <summary>Smoke test only: shows the "checking" or "revert available" look without doing any work.</summary>
    internal void Preview(StatusMessage? busy = null, bool? canRevert = null)
    {
        if (canRevert is { } revert)
        {
            CanRevert = revert;
        }

        IsBusy = busy is not null;
        if (busy is not null)
        {
            ProgressText = "Проверено 2 из 6";
            SetBusyStatus(busy);
        }
        else
        {
            ProgressText = string.Empty;
        }
    }

    /// <summary>Used by the smoke test to render states without touching the network.</summary>
    public void ShowReport(DiagnosisReport report, StatusMessage message)
    {
        _report = report;
        _hasChecked = true;
        OnPropertyChanged(nameof(CheckText));
        SetStatus(message);

        Details = ReportFormatter.Format(report, AppInfo.Version);
        FixReady = report.Plan.HasChanges;
        RefreshHostsState();
    }

    private async Task CheckAsync()
    {
        await RunBusyAsync(new StatusMessage(StatusTone.Neutral, "Проверяем подключение…", "Это займёт несколько секунд."), cancellable: true, async ct =>
        {
            var report = await RunDiagnosisAsync(ct);
            ShowReport(report, Texts.ForReport(report));
        });
    }

    private async Task FixAsync()
    {
        if (_report is not { Plan.HasChanges: true })
        {
            return;
        }

        // 1. Before asking anything: is the result still valid? (age, hosts unchanged, addresses still answer)
        var ready = await PreflightAsync();
        if (ready is null || !_dialogs.Confirm("Исправить подключение", BuildFixConfirmation(ready.Plan)))
        {
            return;
        }

        // 2. The dialog may have been open for a while: re-check once more right before requesting UAC.
        ready = await PreflightAsync();
        if (ready is null)
        {
            return;
        }

        var plan = ready.Plan;
        await RunBusyAsync(new StatusMessage(StatusTone.Neutral, "Применяем исправление…", "Подтвердите запрос Windows на внесение изменений."), cancellable: false, async ct =>
        {
            var outcome = await _app.Elevation.RunAsync(ElevatedRequest.Apply(plan.DesiredPins, plan.RequiresConflictConsent));
            if (!HandleElevationOutcome(outcome, "Ничего не изменено."))
            {
                return;
            }

            // The re-check is not cancellable: the hosts file has already changed and the user must see the real result.
            // No automatic rollback: on a real PC sign-in and the game worked while some services still failed.
            SetBusyStatus(new StatusMessage(StatusTone.Neutral, "Проверяем результат…", "Это займёт несколько секунд."));
            await FlushDnsAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
            DiagnosisReport after;
            try
            {
                after = await RunDiagnosisAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _app.Log.Error("Re-check after fix failed.", ex);
                RefreshHostsState();
                SetStatus(new StatusMessage(StatusTone.Warning, "Изменения применены",
                    "Изменения внесены, но повторная проверка завершилась ошибкой. Нажмите «Проверить снова» или «Отменить изменения»."));
                FixReady = false;
                return;
            }

            ShowReport(after, outcome.ExitCode == ElevatedExitCode.NoChanges
                ? Texts.ForReport(after) with { Headline = "Исправление уже было применено" }
                : Texts.AfterFix(after, plan.NewPins));
        });
    }

    /// <summary>Returns a report whose plan is still confirmed, or null (and shows why) when nothing should be written.</summary>
    private async Task<DiagnosisReport?> PreflightAsync()
    {
        DiagnosisReport? ready = null;
        await RunBusyAsync(new StatusMessage(StatusTone.Neutral, "Проверяем подключение…", "Перед исправлением ValDoctor ещё раз убеждается, что оно нужно."), cancellable: true, async ct =>
        {
            var preflight = _app.CreatePreflight(RunDiagnosisAsync);
            var result = await preflight.CheckAsync(_report!, ct);
            switch (result.Status)
            {
                case PreflightStatus.Ready:
                    if (!ReferenceEquals(result.Report, _report))
                    {
                        ShowReport(result.Report, Texts.ForReport(result.Report));
                    }

                    ready = result.Report;
                    break;
                case PreflightStatus.PlanChanged:
                    ShowReport(result.Report, new StatusMessage(StatusTone.Warning, "Результат проверки изменился",
                        "Перед исправлением ValDoctor проверил подключение ещё раз, и результат изменился. Ничего не изменено. " +
                        "Посмотрите новый результат и при необходимости нажмите «Исправить» ещё раз."));
                    break;
                default:
                    var message = Texts.ForReport(result.Report);
                    ShowReport(result.Report, message with
                    {
                        Body = "Перед исправлением ValDoctor проверил ещё раз: исправление больше не требуется или его не удалось подтвердить. Ничего не изменено. " + message.Body,
                    });
                    break;
            }
        });
        return ready;
    }

    private void DismissUpdate()
    {
        if (_update is not null)
        {
            _updateChecker?.Dismiss(_update);
        }

        ShowUpdate(null);
    }

    private async Task RevertAsync()
    {
        const string message =
            "ValDoctor удалит только свои изменения и вернёт настройки, которые отключал. " +
            "Всё остальное содержимое файла, включая ваши изменения, останется как есть.\n\n" +
            "Windows запросит разрешение администратора. Продолжить?";
        if (!_dialogs.Confirm("Отменить изменения", message))
        {
            return;
        }

        await RunBusyAsync(new StatusMessage(StatusTone.Neutral, "Отменяем изменения…", "Подтвердите запрос Windows на внесение изменений."), cancellable: false, async _ =>
        {
            var outcome = await _app.Elevation.RunAsync(ElevatedRequest.Revert());
            if (!HandleElevationOutcome(outcome, "Ничего не изменено."))
            {
                return;
            }

            await FlushDnsAsync();
            _report = null;
            FixReady = false;
            Details = "Изменения ValDoctor отменены. Проверка ещё не выполнялась.";
            SetStatus(outcome.ExitCode == ElevatedExitCode.NoChanges
                ? new StatusMessage(StatusTone.Neutral, "Отменять нечего", "Изменений ValDoctor (или RiotFix) на этом компьютере нет.")
                : new StatusMessage(StatusTone.Success, "Изменения отменены",
                    "Изменения ValDoctor удалены, остальные настройки не тронуты. Перезапустите Riot Client. Чтобы проверить подключение, нажмите «Проверить подключение»."));
        });
    }

    private async Task<DiagnosisReport> RunDiagnosisAsync(CancellationToken ct)
    {
        // May be called from a thread-pool continuation (preflight): always marshal progress to the UI thread.
        var dispatcher = Application.Current.Dispatcher;
        var progress = new Progress<DiagnosisProgress>(p => dispatcher.BeginInvoke(() => ProgressText = p.Message));
        var engine = _app.CreateEngine();
        return await Task.Run(() => engine.RunAsync(RiotServices.All, progress, ct), ct);
    }

    private bool HandleElevationOutcome(ElevationOutcome outcome, string unchanged)
    {
        switch (outcome.Status)
        {
            case ElevationStatus.UserDeclined:
                SetStatus(new StatusMessage(StatusTone.Neutral, "Изменения не внесены", $"Разрешение администратора не было получено. {unchanged}"));
                return false;
            case ElevationStatus.Failed:
                SetStatus(new StatusMessage(StatusTone.Error, "Не удалось запустить изменение", $"Не получилось запустить ValDoctor с правами администратора. {unchanged}"));
                return false;
        }

        switch (outcome.ExitCode)
        {
            case ElevatedExitCode.Success:
            case ElevatedExitCode.NoChanges:
                RefreshHostsState();
                return true;
            case ElevatedExitCode.Conflict:
                SetStatus(new StatusMessage(StatusTone.Warning, "Не удалось исправить",
                    $"После проверки другая программа изменила сетевые настройки для Riot. {unchanged} Проверьте подключение ещё раз."));
                return false;
            case ElevatedExitCode.InvalidArguments:
                SetStatus(new StatusMessage(StatusTone.Error, "Изменение отклонено", $"Запрос не прошёл проверку безопасности. {unchanged}"));
                return false;
            default:
                var kind = ElevatedRunner.ToHostsError(outcome.ExitCode);
                SetStatus(new StatusMessage(StatusTone.Error, "Не удалось исправить",
                    kind is { } k ? Texts.HostsError(k) : $"Непредвиденная ошибка. {unchanged}"));
                RefreshHostsState();
                return false;
        }
    }

    private async Task RunBusyAsync(StatusMessage busy, bool cancellable, Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        var previous = new StatusMessage(Tone, Headline, Body);
        _statusChanged = false;
        IsBusy = true;
        CanCancel = cancellable;
        ProgressText = string.Empty;
        SetBusyStatus(busy);
        try
        {
            await action(cts.Token);
            if (!_statusChanged)
            {
                // Nothing new to report (e.g. preflight confirmed the plan): keep showing the previous result.
                SetStatus(previous);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetStatus(new StatusMessage(StatusTone.Neutral, "Проверка отменена", "Вы остановили проверку. Ничего не изменено."));
        }
        catch (Exception ex)
        {
            _app.Log.Error("Unexpected error in UI operation.", ex);
            SetStatus(new StatusMessage(StatusTone.Error, "Что-то пошло не так",
                "Операция завершилась с ошибкой. Подробности — по кнопке ⓘ в правом верхнем углу."));
        }
        finally
        {
            _cts = null;
            CanCancel = false;
            IsBusy = false;
            ProgressText = string.Empty;
        }
    }

    /// <summary>Plain-language confirmation. Exact addresses and lines are in «Подробности».</summary>
    private static string BuildFixConfirmation(FixPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ValDoctor нашёл проверенный способ улучшить подключение к Riot Games и внесёт небольшое изменение в системные настройки сети Windows (файл hosts).");
        if (plan.ConflictsToDisable.Count > 0 || plan.RemovedPins.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Старые настройки для Riot, которые сейчас не работают, будут отключены. Их можно вернуть кнопкой «Отменить изменения».");
        }

        sb.AppendLine();
        sb.AppendLine("Перед изменением сохраняется резервная копия. Отменить изменение можно в любой момент кнопкой «Отменить изменения».");
        sb.AppendLine("Windows попросит разрешение администратора.");

        bool riotRunning;
        try
        {
            riotRunning = RiotProcesses.AnyRunning();
        }
        catch (InvalidOperationException)
        {
            riotRunning = false;
        }

        if (riotRunning)
        {
            sb.AppendLine();
            sb.AppendLine("Riot Client сейчас запущен — после исправления его нужно перезапустить.");
        }

        sb.AppendLine();
        sb.Append("Продолжить?");
        return sb.ToString();
    }

    private static async Task FlushDnsAsync()
    {
        try
        {
            await Task.Run(DnsCache.Flush);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Not fatal: Windows also notices hosts changes on its own.
        }
    }

    private void SetBusyStatus(StatusMessage message)
    {
        Tone = message.Tone;
        Headline = message.Headline;
        Body = message.Body;
    }

    private void SetStatus(StatusMessage message)
    {
        _statusChanged = true;
        Tone = message.Tone;
        Headline = message.Headline;
        Body = message.Body;
    }

    private void RefreshHostsState()
    {
        try
        {
            CanRevert = _app.Hosts.Load().HasManagedContent;
        }
        catch (HostsAccessException)
        {
            CanRevert = false;
        }
    }

    private void RaiseCommands()
    {
        CheckCommand.RaiseCanExecuteChanged();
        FixCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private void CopyReport()
    {
        try
        {
            Clipboard.SetText(Details);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _dialogs.Info("Копирование", "Не удалось скопировать отчёт: буфер обмена занят другой программой.");
        }
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppInfo.LogDirectory}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _dialogs.Info("Журналы", $"Журналы находятся в папке:\n{AppInfo.LogDirectory}");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
