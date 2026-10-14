using Microsoft.Win32;
using SpeedForge.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SpeedForge.Desktop;

public partial class MainWindow : Window
{
    private readonly TrustedTools _trusted;
    private readonly RunStore _store;
    private readonly RunCoordinator _coordinator;
    private IReadOnlyList<ServerEntry> _servers = ServerCatalog.LoadPublished();
    private readonly ServerOverrides _serverOverrides;
    private CancellationTokenSource? _running;
    private bool _initialized, _filtersUpdating, _closing;
    private readonly List<MeasurementProgress> _chart = [];
    private RunResult? _lastResult;
    private double _chartOffset;
    private int _page;
    private readonly List<RunResult> _session = [];
    private string? _profileId;
    private TransportKind _iperf3Transport = TransportKind.Tcp;
    private Task? _activeUiTask;
    private ServerEntry? _verificationServer;
    private int _displayGeneration;
    private Stopwatch? _batchWatch;

    protected override void OnSourceInitialized(EventArgs e)
    { base.OnSourceInitialized(e); NativeWindowTheme.Apply(this); }

    public MainWindow(string? dataRoot = null, bool loadNetworkOnStart = true)
    {
        _trusted = new(dataRoot);
        _store = new(dataRoot);
        _serverOverrides = new(_store.DirectoryPath);
        InitializeComponent();
        BeelineTariff.ItemsSource = BeelineTest.Tariffs;
        try { _servers = _serverOverrides.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { CatalogEditStatus.Text = "Адреса не загружены: " + ex.Message; }
        LoadNetworkPreferences(loadNetworkOnStart);
        Loaded += (_, _) => { NativeWindowTheme.Apply(this); if (_loadNetworkOnStart) _networkStartupTask = RefreshNetworkAsync(); };
        _coordinator = new RunCoordinator(_trusted, _store);
        _initialized = true;
        Refilter(true);
        SelectServer(_servers.First(x => x.Id == "hostkey-https"));
        CompareCombo.ItemsSource = _servers;
        CompareCombo.SelectedItem = _servers.First(x => x.Id == "selectel-https");
        CatalogGrid.ItemsSource = _servers;
        CatalogCount.Text = $"{_servers.Count} серверов · {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        RefreshHistory();
        Navigate(0);
    }

    private static T Choice<T>(ComboBox box) where T : struct, Enum =>
        Enum.TryParse<T>((box.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value)
        ? value : throw new InvalidDataException("Выберите режим");
    private static int Number(TextBox box, string label) =>
        int.TryParse(box.Text.Trim(), out var n) ? n : throw new InvalidDataException("Неверное число: " + label);
    private TestOptions ReadOptions(TestDirection? direction = null)
    {
        var server = CurrentServer();
        bool beeline = BeelineTest.Applies(server);
        var options = new TestOptions(direction ?? Choice<TestDirection>(DirectionCombo),
            Choice<TransportKind>(TransportCombo), Choice<IpFamily>(FamilyCombo),
            Number(DurationBox, "длительность"), beeline ? 1 : Number(ParallelBox, "потоки"), 1,
            beeline ? 0 : checked((long)Number(RateBox, "лимит скорости") * 1_000_000),
            LatencyCheck.IsChecked == true, PrivateCheck.IsChecked == true, ExpertCheck.IsChecked == true,
            null,
            UseIperf2Profiles: Iperf2ProfilesCheck.IsChecked == true);
        if (BeelineTest.Applies(server)) options = ReadBeelineOptions(options);
        options = options with { TimeoutSeconds = RunTiming.RunSeconds(server, options) };
        options.Validate();
        return options;
    }
    private ServerEntry CurrentServer()
    {
        if (_verificationServer != null) return _verificationServer;
        if (!string.IsNullOrWhiteSpace(CustomUrl.Text))
        {
            if (!Uri.TryCreate(CustomUrl.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidDataException("Введите полный http:// или https:// URL тестового файла");
            return CustomEntry(uri.Host, uri.Port, EngineKind.Http, uri.AbsoluteUri) with { Http = new HttpProfile(uri.AbsoluteUri) };
        }
        if (!string.IsNullOrWhiteSpace(CustomHost.Text))
            return CustomEntry(CustomHost.Text.Trim(), Number(CustomPort, "порт"), Choice<EngineKind>(CustomEngine));
        return ServerCombo.SelectedItem as ServerEntry ?? throw new InvalidDataException("Не выбран сервер");
    }
    private static ServerEntry CustomEntry(string host, int port, EngineKind engine, string? url = null)
    {
        var key = System.Text.Encoding.UTF8.GetBytes($"{engine}|{host.ToLowerInvariant()}|{port}|{url}");
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key))[..24].ToLowerInvariant();
        return ServerCatalog.Custom(host, port, engine) with { Id = "custom-" + id };
    }
    private void Custom_Changed(object sender, RoutedEventArgs e) { if (_initialized) { _serverSelectionGeneration++; UpdateProfile(); } }
    private void Filters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _filtersUpdating) return;
        if (!_applyingNetworkSelection) _serverSelectionGeneration++;
        Refilter(sender == MethodFilter);
    }
    private void Refilter(bool reset)
    {
        _filtersUpdating = true;
        try
        {
            string method = (MethodFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
            var baseSet = _servers.Where(x => method == "All" || x.Engine.ToString() == method).ToArray();
            if (reset)
            {
                GroupFilter.ItemsSource = new[] { "Все операторы" }.Concat(baseSet.Select(x => x.Group).Distinct().Order()).ToArray();
                GroupFilter.SelectedIndex = 0;
            }
            string group = GroupFilter.SelectedItem?.ToString() ?? "Все операторы";
            var grouped = baseSet.Where(x => group == "Все операторы" || x.Group == group).ToArray();
            string oldCity = CityFilter.SelectedItem?.ToString() ?? "Все города";
            var cities = new[] { "Все города" }.Concat(grouped.Select(x => x.City).Distinct().Order()).ToArray();
            CityFilter.ItemsSource = cities;
            CityFilter.SelectedItem = !reset && cities.Contains(oldCity) ? oldCity : cities[0];
            string city = CityFilter.SelectedItem?.ToString() ?? cities[0];
            var subset = grouped.Where(x => city == "Все города" || x.City == city).OrderBy(x => x.Group).ThenBy(x => x.City).ToArray();
            string? previous = (ServerCombo.SelectedItem as ServerEntry)?.Id;
            ServerCombo.ItemsSource = subset;
            ServerCombo.SelectedItem = subset.FirstOrDefault(x => x.Id == previous) ?? subset.FirstOrDefault();
        }
        finally { _filtersUpdating = false; }
        UpdateProfile();
    }
    private void SelectServer(ServerEntry entry)
    {
        MethodFilter.SelectedIndex = entry.Engine == EngineKind.Http ? 0 : entry.Engine == EngineKind.Iperf2 ? 2 : 1;
        Refilter(true);
        ServerCombo.SelectedItem = entry;
        UpdateProfile();
    }
    private void Server_Changed(object sender, SelectionChangedEventArgs e)
    { if (_initialized) { if (!_applyingNetworkSelection && !_filtersUpdating) _serverSelectionGeneration++; UpdateProfile(); } }
    private void Profile_Changed(object sender, RoutedEventArgs e) { if (_initialized) UpdateProfile(); }
    private void UpdateProfile()
    {
        ServerEntry server;
        try { server = CurrentServer(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        { ServerDetail.Text = "Заполните адрес и порт собственного сервера"; return; }
        bool http = server.Engine == EngineKind.Http;
        bool newProfile = _profileId != server.Id;
        _profileId = server.Id;
        if (newProfile && _initialized && _running == null) ResetResult();
        if (newProfile && server.Engine == EngineKind.Iperf2)
        { BothCheck.IsChecked = true; DirectionCombo.SelectedIndex = 0; TransportCombo.SelectedIndex = 1; }
        Iperf2ProfilesCheck.Visibility = server.Iperf2Profiles.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool archiveProfiles = server.Iperf2Profiles.Length > 0 && Iperf2ProfilesCheck.IsChecked == true;
        ParallelBox.IsEnabled = !archiveProfiles;
        if (archiveProfiles) ParallelBox.Text = "1";
        if (newProfile && server.Iperf2Profiles.Length > 0) DurationBox.Text = "10";
        if (newProfile && server.Engine == EngineKind.Iperf3)
            TransportCombo.SelectedIndex = _iperf3Transport == TransportKind.Udp ? 1 : 0;
        bool upload = !http || server.Http == null || server.Http.UploadUrl != null;
        ServerDetail.Text = server.Id.StartsWith("custom-", StringComparison.Ordinal) ? $"{server.Host}:{server.Port} · {server.MethodLabel}"
            : $"{server.Group} · {server.City} · {server.MethodLabel}";
        ReadyText.Text = http ? upload ? "Готово · загрузка и отдача через " + server.MethodLabel : "Готово · эта точка измеряет загрузку" : $"Готово · {server.Engine} встроен в программу";
        BothCheck.IsEnabled = upload;
        DirectionCombo.IsEnabled = BothCheck.IsChecked != true;
        ((ComboBoxItem)DirectionCombo.Items[1]).IsEnabled = upload;
        ((ComboBoxItem)DirectionCombo.Items[2]).IsEnabled = server.Engine == EngineKind.Iperf3;
        if (!upload || http && Choice<TestDirection>(DirectionCombo) == TestDirection.Bidirectional) DirectionCombo.SelectedIndex = 0;
        TransportCombo.IsEnabled = !http;
        ((ComboBoxItem)TransportCombo.Items[1]).IsEnabled = !http;
        ((ComboBoxItem)TransportCombo.Items[0]).IsEnabled = server.Engine != EngineKind.Iperf2;
        if (http) TransportCombo.SelectedIndex = 0;
        ProbeButton.IsEnabled = _running == null && (!http || server.Http?.UploadUrl != null);
        UpdateObservations();
        UpdateBeelineControls(server);
        UpdateRateSummary();
    }
    private void Transport_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        try { if (CurrentServer().Engine == EngineKind.Iperf3) _iperf3Transport = Choice<TransportKind>(TransportCombo); }
        catch (InvalidDataException) { }
        UpdateRateSummary();
    }
    private void Rate_Changed(object sender, TextChangedEventArgs e) { if (_initialized) UpdateRateSummary(); }
    private void Direction_Changed(object sender, SelectionChangedEventArgs e) { if (_initialized) UpdateRateSummary(); }
    private void ClearRate_Click(object sender, RoutedEventArgs e) => RateBox.Text = "0";
    private void UpdateRateSummary()
    {
        if (!_initialized) return;
        bool valid = int.TryParse(RateBox.Text.Trim(), out int rate) && rate >= 0;
        RateSummary.Text = !valid ? "Укажите лимит: 0 = максимум" : rate == 0 ? "Скорость: максимум" : $"Лимит скорости: {rate} Мбит/с (всего)";
        try
        {
            var server = CurrentServer();
            if (BeelineTest.Applies(server))
            { RateSummary.Text = BeelineTariff.SelectedItem is int tariff ? $"Тариф: {tariff} Мбит/с · исходящая UDP-проверка" : "Выберите скорость тарифа в параметрах"; return; }
            if (server.Iperf2Profiles.Length > 0 && Iperf2ProfilesCheck.IsChecked == true)
                RateSummary.Text += $"\nБилайн: {server.Iperf2Profiles.Length} профилей по {DurationBox.Text.Trim()} с · полный прогон — 10 с";
            if (valid && rate == 0 && server.Engine == EngineKind.Iperf2
                && (BothCheck.IsChecked == true || Choice<TestDirection>(DirectionCombo) == TestDirection.Download))
                RateSummary.Text += "\nОбратная передача проверяется отдельно";
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { }
        ClearRateButton.IsEnabled = _running == null && RateBox.Text.Trim() != "0";
    }
    private void Busy(bool value)
    {
        RunButton.IsEnabled = true;
        RunButton.Content = value ? "Остановить" : "Проверить скорость";
        CancelButton.IsEnabled = value;
        OptionsPanel.IsEnabled = !value;
        CatalogEditor.IsEnabled = !value;
        ClearRateButton.IsEnabled = !value && RateBox.Text.Trim() != "0";
        ProbeButton.IsEnabled = false;
        if (!value)
        {
            try
            {
                var selected = CurrentServer();
                ProbeButton.IsEnabled = selected.Engine != EngineKind.Http || selected.Http is null || selected.Http.UploadUrl != null;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { }
        }
        DebugProbeButton.IsEnabled = !value;
        CompareButton.IsEnabled = !value;
        RunProgress.IsIndeterminate = value;
    }
    private void ResetResult()
    {
        _displayGeneration++;
        DownloadValue.Text = UploadValue.Text = PingValue.Text = JitterValue.Text = "—";
        JitterSource.Text = "мс"; PacketLossValue.Text = "Потери: —"; LatencyContext.Text = "До начала передачи";
        PingValue.Foreground = JitterValue.Foreground = PacketLossValue.Foreground = QualityBrush(MetricQuality.Unknown);
        PingValue.ToolTip = JitterValue.ToolTip = PacketLossValue.ToolTip = null;
        DownloadStatus.Foreground = UploadStatus.Foreground = QualityBrush(MetricQuality.Unknown);
        _lastResult = null;
        DownloadStatus.Text = UploadStatus.Text = "Не проверено";
        UploadTxDetail.Text = ""; UploadTxDetail.Visibility = Visibility.Collapsed;
        PhaseTitle.Text = "Готово";
        SummaryText.Text = "Доступные направления определяются проверкой";
        TimeInfo.Text = "";
        ErrorText.Text = "";
        ErrorPanel.Visibility = Visibility.Collapsed;
        _chart.Clear(); _chartOffset = 0;
        RenderChart();
        ProgressBox.Clear();
        _session.Clear(); DebugRuns.ItemsSource = null; DebugBox.Clear();
        ApplySmallMetricColors();
    }
    private async Task Execute(Func<Task> action)
    {
        if (_running != null) { Log("Проверка уже выполняется"); return; }
        _running = new CancellationTokenSource();
        _batchWatch = Stopwatch.StartNew();
        ResetResult();
        Busy(true);
        try
        {
            _runClientNetwork = null;
            _runClientNetwork = await RefreshNetworkForRunAsync(_running.Token);
            _running.Token.ThrowIfCancellationRequested();
            await action();
        }
        catch (OperationCanceledException) { PhaseTitle.Text = "Проверка остановлена"; }
        catch (Exception ex)
        {
            PhaseTitle.Text = "Проверка не выполнена";
            ErrorText.Text = Redaction.Mask(ex.Message);
            ErrorPanel.Visibility = Visibility.Visible;
            Log("Ошибка: " + ex.Message);
        }
        finally
        {
            _batchWatch?.Stop();
            _running.Dispose(); _running = null;
            _runClientNetwork = null;
            Busy(false);
            RenderChart();
            if (_closing) Close();
        }
    }
    private async Task<RunResult> RunSingle(ServerEntry server, TestOptions options)
    {
        int generation = _displayGeneration;
        double chartOffset = _chartOffset;
        if (options.Iperf2OfferedBitsPerSecondPerStream > 0)
            ServerDetail.Text = $"Билайн · {_session.Count(x => x.ProfileId == server.Id && x.Direction == TestDirection.Upload) + 1}/{server.Iperf2Profiles.Length} · профиль {options.ParallelStreams}({options.Iperf2OfferedBitsPerSecondPerStream / 1_000_000})";
        PhaseTitle.Text = options.Direction == TestDirection.Download ? "Измеряем загрузку…" : "Измеряем отдачу…";
        if (options.Direction == TestDirection.Download) DownloadStatus.Text = "Измеряем…";
        else UploadStatus.Text = "Измеряем…";
        var live = new Progress<MeasurementProgress>(sample =>
        {
            if (generation != _displayGeneration) return;
            _chart.Add(sample with { Seconds = sample.Seconds + chartOffset });
            if (sample.Direction == TestDirection.Download) DownloadValue.Text = sample.Mbps.ToString("F1");
            else UploadValue.Text = sample.Mbps.ToString("F1");
            TimeInfo.Text = $"{sample.Seconds:F1} с · {sample.Bytes / 1_048_576.0:F1} МиБ" + (server.Engine == EngineKind.Http ? "" : " за интервал");
            RenderChart();
        });
        var progress = new Progress<string>(message => Log(message));
        Log($"{server.Group}, {server.City}, {options.Direction}, {options.ParallelStreams} потоков, {options.DurationSeconds} с");
        var watch = Stopwatch.StartNew();
        var stage = new RunStageProgress(RunStage.Preparing);
        void UpdateClock()
        {
            double seconds = stage.ElapsedSeconds;
            string elapsed = stage.Stage == RunStage.Transferring && stage.ExpectedSeconds.HasValue
                ? $"{Math.Min(seconds, stage.ExpectedSeconds.Value):F1}/{stage.ExpectedSeconds:F0} с"
                : $"{seconds:F1} с";
            string title = options.Iperf2SenderOnly && stage.Stage == RunStage.ReceiverReport ? "Завершение отправки" : StageText(stage.Stage);
            TimeInfo.Text = $"{watch.Elapsed.TotalSeconds:F1} с всего · {title} · {elapsed}";
            TimeInfo.ToolTip = stage.ExpectedSeconds.HasValue ? $"Этап: {StageText(stage.Stage)}. Ожидаемая длительность/предел: {stage.ExpectedSeconds:F1} с.\nОбщий предел запуска: {options.TimeoutSeconds} с. {stage.Detail}" : stage.Detail;
        }
        var phases = new Progress<RunStageProgress>(value =>
        {
            if (generation != _displayGeneration) return;
            stage = value; PhaseTitle.Text = options.Iperf2SenderOnly && value.Stage == RunStage.ReceiverReport ? "Завершение UDP-отправки" : StageText(value.Stage); UpdateClock();
            if (_chart.Count == 0) ChartHint.Text = value.Stage is RunStage.Transferring or RunStage.ReceiverReport
                ? options.Iperf2SenderOnly ? "UDP-отправка" : "Ожидаем данные приёмника" : "Подготавливаем проверку";
            if (value.LocalSenderMbps.HasValue)
            {
                UploadTxDetail.Text = $"TX локально: {value.LocalSenderMbps:F1} Мбит/с";
                UploadTxDetail.Visibility = Visibility.Visible;
            }
        });
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => UpdateClock();
        timer.Start();
        RunResult result;
        try { result = await _coordinator.RunAsync(server, options, SessionKey.Password, progress, _running!.Token, live, phases, _runClientNetwork); }
        finally { timer.Stop(); }
        _displayGeneration++;
        _lastResult = result;
        _session.Add(result);
        DebugRuns.ItemsSource = _session.ToArray(); DebugRuns.SelectedItem = result;
        if (result.Status is not (RunStatus.Measured or RunStatus.Partial))
        {
            if (options.Direction is TestDirection.Download or TestDirection.Bidirectional) DownloadValue.Text = "—";
            if (options.Direction is TestDirection.Upload or TestDirection.Bidirectional) UploadValue.Text = "—";
        }
        bool unconfirmedIperf2 = server.Engine == EngineKind.Iperf2 && result.ReceiverBytes == null;
        if (unconfirmedIperf2 && options.Direction == TestDirection.Upload) UploadValue.Text = "—";
        if (options.Direction == TestDirection.Upload)
        {
            UploadTxDetail.Text = !options.Iperf2SenderOnly && unconfirmedIperf2 && result.SenderMbps.HasValue ? $"TX локально: {result.SenderMbps:F1} Мбит/с" : "";
            UploadTxDetail.Visibility = UploadTxDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (server.Engine != EngineKind.Http && options.Direction == TestDirection.Upload && result.Status is RunStatus.Measured or RunStatus.Partial
            && result.ReceiverBytes > 0 && result.ReceiverMbps is > 0)
        {
            _chart.Add(new(options.Direction, _chartOffset + options.DurationSeconds, result.ReceiverMbps.Value, result.ReceiverBytes.Value));
            RenderChart();
        }
        if (options.Direction == TestDirection.Bidirectional && result.Status is RunStatus.Measured or RunStatus.Partial)
        {
            if (result.DownloadMbps is > 0 && result.DownloadReceiverBytes > 0)
                _chart.Add(new(TestDirection.Download, _chartOffset + options.DurationSeconds, result.DownloadMbps.Value, result.DownloadReceiverBytes.Value));
            if (result.UploadMbps is > 0 && result.UploadReceiverBytes > 0)
                _chart.Add(new(TestDirection.Upload, _chartOffset + options.DurationSeconds, result.UploadMbps.Value, result.UploadReceiverBytes.Value));
            RenderChart();
        }
        if (server.Engine != EngineKind.Http && options.Direction == TestDirection.Download && !unconfirmedIperf2
            && !_chart.Any(x => x.Direction == options.Direction && x.Seconds > _chartOffset))
        {
            foreach (var interval in result.Intervals)
                _chart.Add(new(options.Direction, _chartOffset + interval.EndSeconds, interval.BitsPerSecond / 1_000_000, interval.Bytes));
            RenderChart();
        }
        ApplyResultRates(result);
        string directionStatus = result.Status == RunStatus.Measured ? "Подтверждено" : result.Engine == EngineKind.Iperf2
            && result.Status == RunStatus.Partial && result.ReceiverBytes == null ? "TX: приём не подтверждён" : StatusText(result.Status);
        if (options.Direction is TestDirection.Download or TestDirection.Bidirectional) DownloadStatus.Text = directionStatus;
        if (options.Direction is TestDirection.Upload or TestDirection.Bidirectional) UploadStatus.Text = directionStatus;
        var statusColor = QualityBrush(result.Status == RunStatus.Measured ? MetricQuality.Good
            : result.Status == RunStatus.Partial ? MetricQuality.Moderate : MetricQuality.Poor);
        if (options.Direction is TestDirection.Download or TestDirection.Bidirectional) DownloadStatus.Foreground = statusColor;
        if (options.Direction is TestDirection.Upload or TestDirection.Bidirectional) UploadStatus.Foreground = statusColor;
        UpdateQualityMetrics(result);
        _chartOffset += options.DurationSeconds;
        PhaseTitle.Text = StatusText(result.Status);
        TimeInfo.Text = $"{watch.Elapsed.TotalSeconds:F1} с · {result.StatusLabel}";
        SummaryText.Text = $"{server.Group} · {server.City} · {server.MethodLabel} · {StatusText(result.Status)}";
        if (result.Status is not (RunStatus.Measured or RunStatus.Cancelled))
        {
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = FriendlyError(result);
        }
        Log(StatusText(result.Status) + ": " + Formatters.Speed(result.Mbps));
        if (result.ErrorText != null) Log(result.ErrorText);
        RefreshHistory();
        UpdateObservations();
        return result;
    }
    private Task RunSelectedAsync() => Execute(async () =>
    {
        ResetResult();
        var server = CurrentServer();
        ServerDetail.Text = server.Region == "Пользовательская цель" ? $"{server.Host}:{server.Port} · {server.MethodLabel}"
            : $"{server.Group} · {server.City} · {server.MethodLabel}";
        bool automatic = BothCheck.IsChecked == true;
        var options = ReadOptions(automatic ? TestDirection.Download : null);
        await DirectionalTest.RunAsync(server, options, automatic, next => RunSingle(server, next), _running!.Token);
        ApplySessionSummary(server);
    });
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running != null) { Cancel_Click(sender, e); return; }
        _activeUiTask = RunSelectedAsync();
        await _activeUiTask;
    }
    private void ApplySessionSummary(ServerEntry server)
    {
        if (_session.Any(x => x.Options.Iperf2SenderOnly))
        {
            var sent = _session.Last();
            UploadTxDetail.Visibility = Visibility.Collapsed;
            bool received = DirectionObservation.Confirmed(sent);
            UploadStatus.Text = received ? "Приём сервером подтверждён" : sent.Status == RunStatus.Measured ? "Отправка завершена" : sent.StatusLabel;
            UploadUnits.Text = received ? "Мбит/с · приём сервером" : "Мбит/с · отправлено с ПК";
            if (received)
            {
                _chart.Clear(); _chart.Add(new(TestDirection.Upload, sent.Options.DurationSeconds, sent.ReceiverMbps!.Value, sent.ReceiverBytes!.Value));
                ChartTitle.Text = "Приём UDP сервером"; RenderChart();
            }
            DownloadStatus.Text = "Не проверяется"; DownloadValue.Text = "—";
            PhaseTitle.Text = sent.Status == RunStatus.Measured ? "UDP-отправка завершена" : sent.StatusLabel;
            SummaryText.Text = received ? "Исходящая UDP-скорость подтверждена отчётом сервера." : "Показана отправка с ПК. Приём сервером и потери этой проверкой не подтверждены.";
            ErrorPanel.Visibility = sent.Status is RunStatus.Measured or RunStatus.Cancelled ? Visibility.Collapsed : Visibility.Visible;
            TimeInfo.Text = $"Весь прогон: {_batchWatch?.Elapsed.TotalSeconds:F1} с · 1 проверка";
            return;
        }
        var observation = DirectionObservation.From(_session);
        if (server.Engine == EngineKind.Iperf2 && server.Iperf2Profiles.Length > 0 && _session.Any(x => x.Direction == TestDirection.Upload))
        {
            ServerDetail.Text = $"{server.Group} · {server.Host} · {_session.Count(x => x.Direction == TestDirection.Upload)} профилей";
            var uploads = _session.Where(x => x.Direction == TestDirection.Upload && DirectionObservation.Confirmed(x)).ToArray();
            UploadValue.Text = uploads.Length > 0 ? uploads.Max(x => x.UploadMbps)!.Value.ToString("F1") : "—";
            UploadStatus.Text = uploads.Length > 0 ? $"Приём сервером · {uploads.Length} профилей подтверждено"
                : _session.Any(x => x.SenderMbps > 0) ? "TX: приём не подтверждён" : "Приём не подтверждён";
            UploadStatus.Foreground = QualityBrush(uploads.Length > 0 ? MetricQuality.Good : MetricQuality.Moderate);
            if (uploads.Length > 0) UpdateQualityMetrics(uploads.MaxBy(x => x.UploadMbps)!);
            if (uploads.Length > 0) { UploadTxDetail.Text = ""; UploadTxDetail.Visibility = Visibility.Collapsed; }
        }
        PhaseTitle.Text = observation.Description;
        if (server.Engine == EngineKind.Http && server.Http is { UploadUrl: null })
        {
            UploadStatus.Text = "У точки нет приёма HTTP";
            if (observation.Verdict == DirectionVerdict.DownloadObserved) PhaseTitle.Text = "Загрузка подтверждена";
        }
        SummaryText.Text = _session.Count > 2 ? $"Проверено профилей отдачи: {_session.Count(x => x.Direction == TestDirection.Upload)} · итог по лучшему подтверждённому приёму"
            : string.Join(" · ", _session.Select(x => (x.Direction == TestDirection.Download ? "↓ " : "↑ ") + StatusText(x.Status)));
        if (server.Engine == EngineKind.Iperf2 && observation.Verdict == DirectionVerdict.Unconfirmed
            && _session.Any(x => x.Direction == TestDirection.Upload && x.SenderBytes > 0))
        {
            PhaseTitle.Text = "UDP отправлен · отчёт сервера не получен";
            SummaryText.Text = "Локальный TX показан отдельно. Скорость приёма, потери и UDP-джиттер неизвестны.";
            ErrorPanel.Visibility = Visibility.Collapsed;
        }
        // Per-direction cards preserve failures; keep the main screen compact after a usable measurement.
        if (observation.Verdict is DirectionVerdict.Both or DirectionVerdict.DownloadObserved or DirectionVerdict.UploadObserved)
            ErrorPanel.Visibility = Visibility.Collapsed;
        if (_batchWatch != null) TimeInfo.Text = $"Весь прогон: {_batchWatch.Elapsed.TotalSeconds:F1} с · {_session.Count} проверок";
    }
    private static string StageText(RunStage stage) => stage switch
    {
        RunStage.Preparing => "Подготовка и адрес сервера", RunStage.LatencyBefore => "Пинг до нагрузки",
        RunStage.ToolCheck => "Проверка компонентов", RunStage.VersionCheck => "Версия измерителя",
        RunStage.Connecting => "Соединение", RunStage.Transferring => "Передача данных",
        RunStage.ReceiverReport => "Итоговый отчёт", RunStage.LatencyAfter => "Пинг после нагрузки",
        RunStage.Saving => "Сохранение результата", RunStage.Finished => "Завершено", _ => "Соединение и передача"
    };
    private static Brush QualityBrush(MetricQuality quality) => (Brush)Application.Current.FindResource(quality switch
    {
        MetricQuality.Good => "QualityGood", MetricQuality.Moderate => "QualityModerate",
        MetricQuality.Poor => "QualityPoor", _ => "QualityUnknown"
    });
    private void UpdateQualityMetrics(RunResult result)
    {
        var baseline = _session.LastOrDefault(x => x.ProfileId == result.ProfileId && x.ResolvedIp == result.ResolvedIp
            && x.Latency.Any(sample => sample.Phase == ProbePhase.Idle));
        var idle = (baseline ?? result).Latency.Where(x => x.Phase == ProbePhase.Idle).ToArray();
        double? ping = LatencyStatistics.Median(idle, ProbePhase.Idle);
        PingValue.Text = ping?.ToString("F1") ?? "—";
        PingValue.Foreground = QualityBrush(LatencyStatistics.PingQuality(ping));
        PingValue.ToolTip = "Медиана ICMP до нагрузки. Цвет: до 50 мс / до 100 мс / выше. Это ориентиры интерфейса.";
        bool udp = result.Transport == TransportKind.Udp && result.ReceiverBytes.HasValue;
        double? jitter = udp ? result.JitterMilliseconds : LatencyStatistics.Jitter(idle, ProbePhase.Idle);
        JitterValue.Text = jitter?.ToString("F1") ?? "—";
        JitterValue.Foreground = QualityBrush(LatencyStatistics.JitterQuality(jitter));
        JitterSource.Text = udp ? "мс · UDP" : "мс · ICMP";
        JitterValue.ToolTip = (udp ? $"Из отчёта приёмника UDP · {result.DirectionLabel}." : "Среднее изменение между соседними ICMP-ответами до нагрузки.")
            + " Цвет: до 5 мс / до 20 мс / выше. Для отсутствующих данных показано —.";
        double? loss = result.Transport == TransportKind.Udp && result.ReceiverBytes.HasValue ? result.LostPercent : null;
        PacketLossValue.Text = loss.HasValue ? $"Потери UDP: {loss:F2}%" : "Потери UDP: —";
        PacketLossValue.Foreground = QualityBrush(LatencyStatistics.LossQuality(loss));
        PacketLossValue.ToolTip = "По отчёту приёмника UDP: 0% — зелёный, до 1% — жёлтый, выше — красный. Без отчёта значение неизвестно.";
        LatencyContext.Text = udp ? $"UDP · {result.DirectionLabel}" : "ICMP · до нагрузки";
        ApplySmallMetricColors();
    }
    private async void Compare_Click(object sender, RoutedEventArgs e)
    { _activeUiTask = CompareSelectedAsync(); await _activeUiTask; }
    private Task CompareSelectedAsync() => Execute(async () =>
    {
        var first = CurrentServer();
        var second = CompareCombo.SelectedItem as ServerEntry ?? throw new InvalidDataException("Выберите вторую точку");
        if (first.Id == second.Id) throw new InvalidDataException("Выберите разные серверы");
        bool automatic = BothCheck.IsChecked == true;
        var options = ReadOptions(automatic ? TestDirection.Download : null);
        if (!automatic && options.Direction == TestDirection.Bidirectional && second.Engine != EngineKind.Iperf3)
            throw new InvalidDataException("Для сравнения разных методов выберите автоматический режим или отдельное направление");
        ResetResult(); Navigate(0);
        var a = await DirectionalTest.RunAsync(first, options, automatic, next => RunSingle(first, next), _running!.Token);
        if (_running!.IsCancellationRequested) return;
        var transport = second.Engine == EngineKind.Http ? TransportKind.Tcp : second.Engine == EngineKind.Iperf2 ? TransportKind.Udp
            : first.Engine == EngineKind.Iperf3 ? options.Transport : TransportKind.Tcp;
        var secondOptions = options with { Transport = transport, Iperf2SenderOnly = false, ProbeIperf2Client = false,
            BandwidthLimitBitsPerSecond = options.Iperf2SenderOnly ? 0 : options.BandwidthLimitBitsPerSecond };
        if (BeelineTest.Applies(second)) secondOptions = ReadBeelineOptions(secondOptions);
        secondOptions = secondOptions with { TimeoutSeconds = RunTiming.RunSeconds(second, secondOptions) };
        var b = await DirectionalTest.RunAsync(second, secondOptions, automatic,
            next => RunSingle(second, next), _running.Token);
        PhaseTitle.Text = "Сравнение завершено";
        SummaryText.Text = $"{first.Group}: {DirectionObservation.From(a).Description} · {second.Group}: {DirectionObservation.From(b).Description}";
    });
    private void Cancel_Click(object sender, RoutedEventArgs e) { _running?.Cancel(); PhaseTitle.Text = "Останавливаем передачу…"; }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_running == null) return;
        e.Cancel = true; _closing = true; _running.Cancel();
    }
    private void Log(string text)
    {
        ProgressBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + Redaction.Mask(text) + Environment.NewLine);
        if (ProgressBox.Text.Length > 24_000) ProgressBox.Text = ProgressBox.Text[^18_000..];
        ProgressBox.ScrollToEnd();
    }
    private static string StatusText(RunStatus status) => status switch
    {
        RunStatus.Measured => "Проверка завершена", RunStatus.Partial => "Получены частичные данные",
        RunStatus.Cancelled => "Проверка остановлена", RunStatus.ServerBusy => "Сервер занят",
        RunStatus.Timeout => "Время ожидания истекло", RunStatus.NetworkError => "Не удалось подключиться",
        RunStatus.Unsupported => "Режим не поддерживается", RunStatus.NoData => "Данные не получены",
        _ => "Измерение не завершено"
    };
    private static string FriendlyError(RunResult run)
    {
        string message = run.Status switch
        {
            RunStatus.ServerBusy => "Сервер занят другим тестом. Повторите позже или выберите другую точку.",
            RunStatus.Timeout => "Сервер не ответил вовремя. Проверьте подключение или выберите другую точку.",
            RunStatus.NetworkError => "Соединение с сервером не установлено. Проверьте VPN, брандмауэр или выберите другую точку.",
            RunStatus.Unsupported => "Выбранный режим недоступен для этой точки.",
            RunStatus.NoData => "Сервер не передал достаточно тестовых данных.",
            _ => "Сервер не вернул полный результат измерения."
        };
        return message + (string.IsNullOrWhiteSpace(run.ErrorText) ? "" : " " + Redaction.Mask(run.ErrorText)) +
            (run.ErrorCode == null ? "" : " [" + run.ErrorCode + "]");
    }
    private void Navigate_Click(object sender, RoutedEventArgs e) => Navigate(int.Parse(((Button)sender).Tag.ToString()!));
    private void Navigate(int page)
    {
        _page = page; MainTabs.SelectedIndex = page;
        string[] titles = ["Проверка соединения", "История измерений", "Российские серверы", "Настройки и инструменты", "Отладка измерений"];
        string[] subtitles = ["Выберите точку и измерьте загрузку, отдачу и задержку", "Результаты, сохранённые на этом компьютере", "iPerf2, iPerf3, HTTP и HTTPS в одном каталоге", "Встроенные измерители, сравнение и лицензии", "Команды, сырой вывод, попытки и итог каждого направления"];
        PageTitle.Text = titles[page]; PageSubtitle.Text = subtitles[page];
    }
    private void CatalogSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        string query = CatalogSearch.Text.Trim();
        CatalogGrid.ItemsSource = _servers.Where(x => (x.Group + " " + x.City + " " + x.Host + " " + x.MethodLabel).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void Catalog_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_running != null || CatalogGrid.SelectedItem is not ServerEntry entry) return;
        CustomHost.Clear(); CustomUrl.Clear(); SelectServer(entry); Navigate(0);
    }
    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => RenderChart();
    private void ApplyResultRates(RunResult result)
    {
        if (result.Status is not (RunStatus.Measured or RunStatus.Partial)) return;
        if (result.DownloadMbps is > 0) DownloadValue.Text = result.DownloadMbps.Value.ToString("F1");
        if (result.UploadMbps is > 0) UploadValue.Text = result.UploadMbps.Value.ToString("F1");
    }
    private void RenderChart()
    {
        if (ChartCanvas == null) return;
        ChartCanvas.Children.Clear();
        double width = ChartCanvas.ActualWidth, height = ChartCanvas.ActualHeight;
        if (width < 50 || height < 50) return;
        if (_chart.Count == 0)
        {
            ChartHint.Text = _running != null ? "Ожидаем данные приёмника"
                : _lastResult != null ? "Нет подтверждённых данных для графика" : "Нажмите «Проверить скорость»";
            ChartHint.Visibility = Visibility.Visible; PeakValue.Text = "Пик —"; return;
        }
        double max = _chart.Count == 0 ? 100 : Math.Max(1, _chart.Max(x => x.Mbps)) * 1.15;
        double end = Math.Max(8, _chart.Select(x => x.Seconds).DefaultIfEmpty(0).Max());
        for (int i = 0; i < 5; i++)
        {
            double y = 10 + i * (height - 35) / 4;
            ChartCanvas.Children.Add(new Line { X1 = 38, X2 = width, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromRgb(35, 49, 68)), StrokeThickness = 1 });
            var label = new TextBlock { Text = (max * (4 - i) / 4).ToString("F0"), FontSize = 10, Foreground = (Brush)Application.Current.FindResource("LatencyTone") };
            Canvas.SetTop(label, y - 6); ChartCanvas.Children.Add(label);
        }
        foreach (var direction in new[] { TestDirection.Download, TestDirection.Upload })
        {
            var points = _chart.Where(x => x.Direction == direction).ToArray();
            if (points.Length == 0) continue;
            var color = ((SolidColorBrush)Application.Current.FindResource(direction == TestDirection.Download ? "DownloadTone" : "UploadTone")).Color;
            var line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
            foreach (var point in points)
                line.Points.Add(new Point(38 + point.Seconds / end * (width - 42), 10 + (1 - point.Mbps / max) * (height - 35)));
            ChartCanvas.Children.Add(line);
            if (points.Length == 1)
            {
                var marker = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color),
                    ToolTip = $"Отчёт приёмника: {points[0].Mbps:F1} Мбит/с" };
                Canvas.SetLeft(marker, line.Points[0].X - 3.5); Canvas.SetTop(marker, line.Points[0].Y - 3.5);
                ChartCanvas.Children.Add(marker);
            }
        }
        for (int i = 0; i < 5; i++)
        {
            var label = new TextBlock { Text = $"{end * i / 4:F0} с", FontSize = 10, Foreground = (Brush)Application.Current.FindResource("QualityModerate") };
            Canvas.SetLeft(label, Math.Min(width - 28, 38 + i * (width - 42) / 4));
            Canvas.SetTop(label, height - 13); ChartCanvas.Children.Add(label);
        }
        ChartHint.Visibility = _chart.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeakValue.Text = _chart.Count > 0 ? $"Пик {_chart.Max(x => x.Mbps):F1} Мбит/с" : "Пик —";
    }
    private async Task Trust(EngineKind engine)
    {
        var dialog = new OpenFileDialog { Filter = "Приложение Windows (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        var path = TrustedTools.CheckPath(dialog.FileName);
        string hash = await TrustedTools.HashAsync(path, CancellationToken.None);
        if (MessageBox.Show(this, $"Выбрать этот измеритель?\n{path}\nSHA-256: {hash}", "Измерительный модуль", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await _trusted.ApproveSelectedFileAsync(path, engine);
        ReadyText.Text = "Выбран собственный " + engine;
    }
    private async void TrustIperf3_Click(object sender, RoutedEventArgs e) { try { await Trust(EngineKind.Iperf3); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void TrustIperf2_Click(object sender, RoutedEventArgs e) { try { await Trust(EngineKind.Iperf2); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private void RefreshHistory() { try { HistoryList.ItemsSource = _store.Recent(); } catch (Exception ex) { Log("Ошибка истории: " + ex.Message); } }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshHistory();
    private void TechnicalExport_Click(object sender, RoutedEventArgs e) => Export();
    private async void Probe_Click(object sender, RoutedEventArgs e) => await ProbeDirectionsAsync();
    private Task ProbeDirectionsAsync() => Execute(async () =>
    {
        var server = CurrentServer();
        var options = ReadOptions(TestDirection.Upload) with { DurationSeconds = 3, MeasureLatency = false };
        ResetResult();
        var upload = await RunSingle(server, options);
        if (_running!.IsCancellationRequested) return;
        await Task.Delay(750, _running.Token);
        var download = await RunSingle(server, options with { Direction = TestDirection.Download });
        PhaseTitle.Text = "Проверка направлений завершена";
        SummaryText.Text = $"↑ {StatusText(upload.Status)} · ↓ {StatusText(download.Status)}";
        Navigate(4);
    });
    private void DebugRuns_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DebugRuns.SelectedItem is RunResult run)
            DebugBox.Text = $"{run.Engine} {run.Transport} {run.Direction}; {run.Host}:{run.Port}; {run.Status}\n{run.MetricOrigin}\n{run.ErrorCode}: {run.ErrorText}\n\n" + RunDiagnostics.Read(run.DebugDirectory);
    }
    private void HistoryDebug_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not RunResult run) { MessageBox.Show("Выберите запись в истории"); return; }
        DebugRuns.ItemsSource = new[] { run }; DebugRuns.SelectedItem = run; Navigate(4);
    }
    private void DebugExport_Click(object sender, RoutedEventArgs e)
    {
        var runs = DebugRuns.ItemsSource is IEnumerable<RunResult> source ? source.ToArray() : _session.ToArray();
        var directories = runs.Where(x => x.DebugDirectory != null).Select(x => x.DebugDirectory!).ToArray();
        if (directories.Length == 0) { MessageBox.Show("Сначала выполните измерение или выберите журнал в истории"); return; }
        var dialog = new SaveFileDialog { Filter = "Отладочный архив (*.zip)|*.zip", FileName = "SpeedForge_debug.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try { RunDiagnostics.Export(directories, dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось сохранить debug"); }
    }
    private void DebugFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DebugRuns.SelectedItem is not RunResult run || run.DebugDirectory == null) return;
        try { Process.Start(new ProcessStartInfo(run.DebugDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private void LicensesExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Лицензии и исходники (*.zip)|*.zip", FileName = "SpeedForge_components_source.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try { BundledIperf.ExportLicenses(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private void UpdateObservations()
    {
        if (ObservationText == null) return;
        try
        {
            var server = CurrentServer();
            var runs = _store.Recent(500).Where(x => x.ProfileId == server.Id && x.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase) && x.Transport == Choice<TransportKind>(TransportCombo) && x.Status != RunStatus.Cancelled).ToArray();
            string Description(TestDirection direction)
            {
                var latest = runs.FirstOrDefault(x => x.Direction == direction);
                if (latest == null) return "не проверено";
                string verdict = latest.Status == RunStatus.Measured ? "подтверждено" : latest.Engine == EngineKind.Iperf2 && latest.Status == RunStatus.Partial && latest.ReceiverBytes == null ? "TX-only, нет ACK" : latest.Status == RunStatus.Partial ? "частично" : StatusText(latest.Status);
                return $"{verdict} ({latest.Started:dd.MM HH:mm})";
            }
            ObservationText.Text = runs.Length == 0
                ? $"Контрольная проверка {Choice<TransportKind>(TransportCombo)}: " + (server.HasLocalOverride ? "адрес изменён, не проверено" : PublishedObservations.Describe(server.Id, Choice<TransportKind>(TransportCombo)))
                    + ". Доступность определяется заново при запуске."
                : $"Последние тесты {Choice<TransportKind>(TransportCombo)}:\n↓ {Description(TestDirection.Download)}\n↑ {Description(TestDirection.Upload)}";
        }
        catch (Exception ex) { ObservationText.Text = "История наблюдений недоступна: " + ex.GetType().Name; }
    }
    private void Export()
    {
        try
        {
            if (HistoryList.SelectedItem is not RunResult selected) { MessageBox.Show("Выберите измерение в истории."); return; }
            var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "SpeedForge_technical.json" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, ReportExport.TechnicalReport(selected));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Экспорт не выполнен"); }
    }
    private void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private void DocsCatalog_Click(object sender, RoutedEventArgs e) => Open(ServerCatalog.CatalogSource);
    private void DocsMts_Click(object sender, RoutedEventArgs e) => Open("https://lg.mtu.ru/list.html");
    private void DocsHostkey_Click(object sender, RoutedEventArgs e) => Open("https://hostkey.ru/documentation/technical/exist_server_using/speedtest/");
    private void DocsSelectel_Click(object sender, RoutedEventArgs e) => Open("https://selectel.ru/speedtest/");
    private void DocsRastr_Click(object sender, RoutedEventArgs e) => Open("https://speedtest.rastrnet.ru/");

    public void SavePreview(string path)
    {
        UpdateLayout();
        var visual = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    public async Task<bool> VerifyUiAsync(string directory, string serverId)
    {
        Directory.CreateDirectory(directory);
        bool rateSwitches = serverId.StartsWith("rates:", StringComparison.Ordinal);
        if (rateSwitches)
        {
            VerifyRateSwitches(directory);
            serverId = serverId[6..];
            SelectServer(_servers.First(x => x.Engine == EngineKind.Iperf2));
        }
        bool cancellation = serverId == "cancel";
        if (cancellation) serverId = "hostkey-https";
        bool probe = serverId.StartsWith("probe:", StringComparison.Ordinal);
        if (probe) serverId = serverId[6..];
        SelectServer(_servers.First(x => x.Id == serverId));
        DurationBox.Text = "3"; ParallelBox.Text = "1"; LatencyCheck.IsChecked = false;
        bool beeline = serverId == "beeline-iperf2";
        if (beeline) { RateBox.Text = "0"; BeelineTariff.SelectedItem = 500; LatencyCheck.IsChecked = true; }
        else if (!rateSwitches && _servers.First(x => x.Id == serverId).Engine == EngineKind.Iperf2) RateBox.Text = "1";
        if (rateSwitches && ReadOptions().BandwidthLimitBitsPerSecond != 0)
            throw new InvalidOperationException("После переключения метода появился скрытый лимит");
        BothCheck.IsChecked = beeline || _servers.First(x => x.Id == serverId).Http?.UploadUrl != null
            || rateSwitches && _servers.First(x => x.Id == serverId).Engine == EngineKind.Iperf3;
        SavePreview(System.IO.Path.Combine(directory, "before.png"));
        Task run;
        if (probe) run = ProbeDirectionsAsync();
        else
        {
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            run = _activeUiTask ?? throw new InvalidOperationException("Кнопка не запустила измерение");
        }
        if (cancellation)
        {
            await Task.Delay(600);
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        await run;
        await Task.Delay(100);
        SavePreview(System.IO.Path.Combine(directory, "after.png"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            LastResult = _lastResult, Download = DownloadValue.Text, Upload = UploadValue.Text,
            Runs = _session.ToArray(), Rate = RateBox.Text, RateSummary = RateSummary.Text,
            Phase = PhaseTitle.Text, Error = ErrorText.Text, ChartPoints = _chart.Count,
            HistoryCount = HistoryList.Items.Count, RunEnabled = RunButton.IsEnabled, Log = ProgressBox.Text
        }, new JsonSerializerOptions { WriteIndented = true }));
        Navigate(1); await Task.Delay(150); UpdateLayout(); SavePreview(System.IO.Path.Combine(directory, "history.png"));
        if (HistoryList.Columns.Any(x => x.ActualWidth < 40))
            throw new InvalidOperationException("Столбцы истории не успели сформироваться перед снимком");
        Navigate(2); await Task.Delay(150); UpdateLayout(); SavePreview(System.IO.Path.Combine(directory, "servers.png"));
        Navigate(3); await Task.Delay(150); UpdateLayout(); SavePreview(System.IO.Path.Combine(directory, "settings.png"));
        Navigate(4); await Task.Delay(150); UpdateLayout(); SavePreview(System.IO.Path.Combine(directory, "debug.png"));
        RunDiagnostics.Export(_session.Where(x => x.DebugDirectory != null).Select(x => x.DebugDirectory!).ToArray(), System.IO.Path.Combine(directory, "debug.zip"));
        Navigate(0);
        if (probe) return _session.Count == 2 && _session.Select(x => x.Direction).Distinct().Count() == 2
            && _session.All(x => x.DebugDirectory != null && File.Exists(System.IO.Path.Combine(x.DebugDirectory, "trace.json"))) && RunButton.IsEnabled;
        if (beeline) return _session.Count == 1 && _session[0].Options.Iperf2SenderOnly
            && _session[0].Direction == TestDirection.Upload && _session[0].Status == RunStatus.Measured
            && _session[0].EngineSha256 == BundledIperf.Iperf2LegacySha256 && RunButton.IsEnabled;
        return _lastResult?.Status == (cancellation ? RunStatus.Cancelled : RunStatus.Measured)
            && (cancellation || DownloadValue.Text != "—") && RunButton.IsEnabled && HistoryList.Items.Count > 0;
    }
    private void VerifyRateSwitches(string directory)
    {
        var checks = new List<object>();
        void Check(string scenario, string expectedRate, TransportKind expectedTransport)
        {
            var options = ReadOptions(TestDirection.Upload);
            long expectedBits = BeelineTest.Applies(CurrentServer()) ? (long)(int)BeelineTariff.SelectedItem * 1_000_000 : long.Parse(expectedRate) * 1_000_000;
            if (RateBox.Text != expectedRate || options.Transport != expectedTransport
                || options.BandwidthLimitBitsPerSecond != expectedBits)
                throw new InvalidOperationException("Ошибка переключения лимита: " + scenario);
            checks.Add(new { Scenario = scenario, Rate = RateBox.Text, options.Transport, options.BandwidthLimitBitsPerSecond, Summary = RateSummary.Text });
        }
        RateBox.Text = "0";
        MethodFilter.SelectedIndex = 2;
        Check("HTTP → Билайн", "0", TransportKind.Udp);
        MethodFilter.SelectedIndex = 1;
        Check("Билайн → iPerf3 TCP", "0", TransportKind.Tcp);
        TransportCombo.SelectedIndex = 1;
        Check("iPerf3 TCP → UDP", "0", TransportKind.Udp);
        SelectServer(_servers.Last(x => x.Engine == EngineKind.Iperf3));
        Check("Смена сервера iPerf3 UDP", "0", TransportKind.Udp);
        MethodFilter.SelectedIndex = 0;
        Check("UDP → HTTP", "0", TransportKind.Tcp);
        RateBox.Text = "37";
        MethodFilter.SelectedIndex = 2;
        Check("Ручной лимит HTTP → Билайн", "37", TransportKind.Udp);
        MethodFilter.SelectedIndex = 1;
        Check("Ручной лимит Билайн → iPerf3, выбранный UDP сохранён", "37", TransportKind.Udp);
        MethodFilter.SelectedIndex = 0;
        Check("Ручной лимит UDP → HTTP", "37", TransportKind.Tcp);
        ClearRateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("Кнопка снятия лимита", "0", TransportKind.Tcp);
        MethodFilter.SelectedIndex = 2;
        BothCheck.IsChecked = true;
        Check("Билайн: только исходящий тарифный тест", "0", TransportKind.Udp);
        MethodFilter.SelectedIndex = 1;
        TransportCombo.SelectedIndex = 0;
        Check("Возврат к TCP без лимита", "0", TransportKind.Tcp);
        File.WriteAllText(System.IO.Path.Combine(directory, "rate-settings.json"), JsonSerializer.Serialize(checks,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
