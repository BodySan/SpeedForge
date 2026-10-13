using SpeedForge.Core;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpeedForge.Desktop;

public partial class MainWindow
{
    public async Task<bool> VerifySandboxAsync(string directory, bool resetOnly = false, bool geoOnly = false, bool serversOnly = false)
    {
        Directory.CreateDirectory(directory);
        _verificationNetworkLookup = (_, _) => Task.FromResult(new NetworkIdentity("8.8.8.8", "8.8.8.8", "Sandbox"));
        var cases = new List<object>();
        int failed = 0;
        void Require(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
        async Task Test(string name, Func<Task> action)
        {
            if (serversOnly && !(name.StartsWith("Серверы:", StringComparison.Ordinal) || name.StartsWith("Билайн: закрытый список", StringComparison.Ordinal)
                || name == "На главном экране видна ровно одна кнопка действия")) return;
            if (resetOnly && name is not ("Ручная загрузка не запускает отдачу" or "Смена сервера очищает прежние скорости и график" or "На главном экране видна ровно одна кнопка действия")) return;
            if (geoOnly && !name.StartsWith("GeoIP:", StringComparison.Ordinal)) return;
            try { await action(); cases.Add(new { Name = name, Passed = true }); }
            catch (Exception ex) { failed++; cases.Add(new { Name = name, Passed = false, Error = ex.ToString() }); }
            await File.WriteAllTextAsync(Path.Combine(directory, "ui-tests.json"), JsonSerializer.Serialize(new { Failed = failed, Cases = cases }, VerificationJson));
        }
        ServerEntry Http(string scenario, bool advertisedUpload = true) => ServerCatalog.Custom("127.0.0.1", 5090, EngineKind.Http) with
        { Id = "ui-http-" + scenario, Http = new("http://127.0.0.1:5090/sandbox/" + scenario + "/download", advertisedUpload ? "http://127.0.0.1:5090/sandbox/" + scenario + "/upload" : null, "LibreSpeed") };
        void Configure(ServerEntry server, bool automatic = true)
        {
            _verificationServer = null; CustomHost.Clear(); CustomUrl.Clear();
            SelectServer(_servers.First(x => x.Engine == server.Engine));
            _verificationServer = server;
            UpdateProfile();
            DurationBox.Text = "3"; ParallelBox.Text = "1"; RateBox.Text = "10"; BeelineTariff.SelectedItem = 500;
            LatencyCheck.IsChecked = false; PrivateCheck.IsChecked = true;
            BothCheck.IsChecked = automatic;
            TransportCombo.SelectedIndex = server.Engine == EngineKind.Iperf2 ? 1 : 0;
            AdvancedPanel.IsExpanded = false;
        }
        async Task Click()
        {
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_activeUiTask != null, "Кнопка не создала задачу");
            Require((string)RunButton.Content == "Остановить" && RunButton.IsEnabled, "Одна кнопка не переключилась в остановку");
            await _activeUiTask!;
            await Task.Delay(80);
            Require(_running == null && RunButton.IsEnabled && (string)RunButton.Content == "Проверить скорость", "Не восстановлен запуск после теста");
        }
        foreach (var item in new[] { ("both", DirectionVerdict.Both), ("download-only", DirectionVerdict.DownloadObserved), ("upload-only", DirectionVerdict.UploadObserved) })
            await Test("Одна кнопка HTTP: " + item.Item1, async () =>
            {
                Configure(Http(item.Item1));
                await Click();
                Require(_session.Count == 2 && DirectionObservation.From(_session).Verdict == item.Item2, "Неверный результат направлений");
                Require((_session.Any(x => x.DownloadMbps > 0)) == (DownloadValue.Text != "—"), "Старая скорость осталась в карточке загрузки");
                Require((_session.Any(x => x.UploadMbps > 0)) == (UploadValue.Text != "—"), "Старая скорость осталась в карточке отдачи");
                SavePreview(Path.Combine(directory, "http-" + item.Item1 + ".png"));
            });
        await Test("Профиль HTTP-файла: отдача не запускается", async () =>
        {
            Configure(Http("download-only", false)); await Click();
            Require(_session.Count == 1 && _session[0].Direction == TestDirection.Download && UploadValue.Text == "—", "HTTP-файл отправил upload");
        });
        foreach (var item in new[] { (55201, "both", DirectionVerdict.Both), (55203, "upload-only", DirectionVerdict.UploadObserved), (55204, "download-only", DirectionVerdict.DownloadObserved), (55205, "neither", DirectionVerdict.Unconfirmed) })
            await Test("Одна кнопка iPerf3: " + item.Item2, async () =>
            {
                Configure(ServerCatalog.Custom("127.0.0.1", item.Item1, EngineKind.Iperf3)); await Click();
                Require(_session.Count == 2 && DirectionObservation.From(_session).Verdict == item.Item3, JsonSerializer.Serialize(_session, VerificationJson));
                SavePreview(Path.Combine(directory, "iperf3-" + item.Item2 + ".png"));
            });
        await Test("Одна кнопка запускает и отменяет; второго направления нет", async () =>
        {
            Configure(Http("stall"));
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(250);
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _activeUiTask!;
            Require(_session.Count == 1 && _session[0].Status == RunStatus.Cancelled && _running == null && RunButton.IsEnabled, "Отмена не завершилась корректно");
            SavePreview(Path.Combine(directory, "cancel.png"));
        });
        await Test("iPerf3 UDP: оба направления одной кнопкой", async () =>
        {
            Configure(ServerCatalog.Custom("127.0.0.1", 55201, EngineKind.Iperf3)); TransportCombo.SelectedIndex = 1;
            await Click(); Require(DirectionObservation.From(_session).Verdict == DirectionVerdict.Both, "Не подтверждены оба UDP направления");
        });
        await Test("iPerf3 TCP bidir: обе карточки в ручном режиме", async () =>
        {
            Configure(ServerCatalog.Custom("127.0.0.1", 55201, EngineKind.Iperf3), false); DirectionCombo.SelectedIndex = 2;
            await Click(); Require(_session.Count == 1 && _session[0].Status == RunStatus.Measured && DownloadValue.Text != "—" && UploadValue.Text != "—", "Bidir не заполнил обе карточки");
        });
        await Test("iPerf2 UDP: сначала отдача, затем обратный тест", async () =>
        {
            Configure(ServerCatalog.Custom("127.0.0.1", 55202, EngineKind.Iperf2));
            await Click(); Require(DirectionObservation.From(_session).Verdict == DirectionVerdict.Both && _session[0].Direction == TestDirection.Upload, "Неверная последовательность iPerf2");
        });
        await Test("iPerf2 без ACK: TX не превращается в подтверждённую отдачу", async () =>
        {
            Configure(ServerCatalog.Custom("127.0.0.1", 55208, EngineKind.Iperf2)); RateBox.Text = "1";
            await Click(); Require(_session.Count == 2 && DirectionObservation.From(_session).Verdict == DirectionVerdict.Unconfirmed
                && UploadStatus.Text.Contains("TX:") && UploadValue.Text == "—" && _session[0].ReceiverBytes == null, "TX ошибочно принят за приём сервером");
            SavePreview(Path.Combine(directory, "iperf2-no-ack.png"));
        });
        await Test("Неверные параметры не блокируют следующий запуск", async () =>
        {
            Configure(Http("both")); DurationBox.Text = "ошибка";
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await _activeUiTask!;
            Require(_session.Count == 0 && ErrorPanel.Visibility == Visibility.Visible && _running == null, "Ошибка параметров не обработана");
            DurationBox.Text = "3"; await Click();
            Require(DirectionObservation.From(_session).Verdict == DirectionVerdict.Both, "Повторный запуск не восстановился");
        });
        await Test("Ручная загрузка не запускает отдачу", async () =>
        {
            Configure(Http("both"), false); DirectionCombo.SelectedIndex = 0;
            await Click(); Require(_session.Count == 1 && _session[0].Direction == TestDirection.Download, "Ручной режим проигнорирован");
        });
        await Test("Собственный iPerf2 выбирает UDP независимо от каталога", async () =>
        {
            _verificationServer = null; CustomHost.Clear(); CustomUrl.Clear(); SelectServer(_servers.First(x => x.Id == "hostkey-https"));
            CustomPort.Text = "55202"; CustomEngine.SelectedIndex = 1; CustomHost.Text = "127.0.0.1";
            DurationBox.Text = "3"; ParallelBox.Text = "1"; RateBox.Text = "1"; LatencyCheck.IsChecked = false; PrivateCheck.IsChecked = true;
            Require(TransportCombo.IsEnabled && ReadOptions().Transport == TransportKind.Udp, "Протокол собственного iPerf2 остался TCP");
            string profile = CurrentServer().Id; UpdateProfile(); Require(CurrentServer().Id == profile, "ID собственного сервера нестабилен");
            await Click(); Require(DirectionObservation.From(_session).Verdict == DirectionVerdict.Both, "Собственный iPerf2 не проверил оба направления");
            CustomHost.Clear();
        });
        await Test("Собственный Endpoint проверяет загрузку и отдачу", async () =>
        {
            _verificationServer = null; CustomHost.Clear(); CustomUrl.Clear(); CustomEngine.SelectedIndex = 2;
            CustomPort.Text = "5088"; CustomHost.Text = "127.0.0.1"; BothCheck.IsChecked = true;
            await Click(); Require(DirectionObservation.From(_session).Verdict == DirectionVerdict.Both, "Upload собственного Endpoint заблокирован интерфейсом");
            CustomHost.Clear();
        });
        await Test("Сравнение iPerf2 с HTTP нормализует протокол второй точки", async () =>
        {
            Configure(ServerCatalog.Custom("127.0.0.1", 55202, EngineKind.Iperf2));
            CompareCombo.ItemsSource = new[] { Http("both") }; CompareCombo.SelectedIndex = 0;
            CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await _activeUiTask!;
            Require(_session.Count == 4 && _session.All(x => x.Status == RunStatus.Measured), "Сравнение не измерило четыре направления");
            Require(_session.Where(x => x.Engine == EngineKind.Http).All(x => x.Transport == TransportKind.Tcp), "HTTP унаследовал UDP");
            CompareCombo.ItemsSource = _servers;
        });
        await Test("Тариф Билайна не ограничивает отдельную проверку HTTP", async () =>
        {
            var server = _servers.Single(x => x.Id == "beeline-iperf2") with { Host = "127.0.0.1", Port = 55214, Ports = [55214] };
            Configure(server); BeelineTariff.SelectedItem = 100;
            CompareCombo.ItemsSource = new[] { Http("both") }; CompareCombo.SelectedIndex = 0;
            CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await _activeUiTask!;
            Require(_session.Count == 3 && _session[0].Options.Iperf2SenderOnly
                && _session.Skip(1).All(x => !x.Options.Iperf2SenderOnly && x.Options.BandwidthLimitBitsPerSecond == 0 && x.Status == RunStatus.Measured),
                "UDP-тариф или режим Билайна перенесён на внешнюю проверку");
        });
        await Test("Версия 1.1.1 и многоразмерная иконка встроены в exe", () =>
        {
            Require(typeof(MainWindow).Assembly.GetName().Version == new Version(1,1,1,0), "Неверная версия assembly");
            Require(Icon != null && Title.Contains("1.1.1", StringComparison.Ordinal), "Нет иконки или версии в окне");
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SpeedForge.ico"));
            using var stream = resource.Stream;
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Require(decoder.Frames.Count == 10 && decoder.Frames.Any(x => x.PixelWidth == 16) && decoder.Frames.Any(x => x.PixelWidth == 256), "Нет кадров иконки для малых/больших размеров");
            return Task.CompletedTask;
        });
        await Test("Тёмная системная рамка и заголовок окна", () =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))
            {
                // These color attributes are Set-only in the documented DWM API.
                Require(NativeWindowTheme.Applied.GetValueOrDefault(NativeWindowTheme.DarkMode, -1) == 0, "Системное оформление не тёмное");
                Require(NativeWindowTheme.Applied.GetValueOrDefault(NativeWindowTheme.Caption, -1) == 0, "Не применён цвет заголовка");
                Require(NativeWindowTheme.Applied.GetValueOrDefault(NativeWindowTheme.Border, -1) == 0, "Не применён цвет рамки");
            }
            Navigate(0); AdvancedPanel.IsExpanded = false; UpdateLayout();
            WindowPreview.Save(this, Path.Combine(directory, "dark-window-frame.png"));
            return Task.CompletedTask;
        });
        await Test("iPerf2: карточка показывает приём 100 Мбит/с вместо TX 1 Гбит/с", async () =>
        {
            Configure(ServerCatalog.Custom("127.0.0.1", 55209, EngineKind.Iperf2), false);
            DirectionCombo.SelectedIndex = 1; RateBox.Text = "1000";
            await Click();
            Require(_session.Count == 1 && _session[0].Status == RunStatus.Measured && _session[0].SenderMbps > 500
                && double.Parse(UploadValue.Text) < 110 && _chart.Count > 0 && _chart.All(x => x.Mbps < 110),
                "В карточку или график попала скорость отправителя");
            SavePreview(Path.Combine(directory, "iperf2-100mbit.png"));
        });
        await Test("Билайн: один тарифный UDP-тест, без reverse и ожидания приёма", async () =>
        {
            var server = _servers.Single(x => x.Id == "beeline-iperf2") with { Host = "127.0.0.1", Port = 55214, Ports = [55214] };
            Configure(server); BeelineTariff.SelectedItem = 500;
            Require(OperatorPanel.Visibility == Visibility.Collapsed && CityPanel.Visibility == Visibility.Collapsed
                && BeelineTariffPanel.Visibility == Visibility.Visible && ParallelPanel.Visibility == Visibility.Collapsed
                && Iperf2ProfilesCheck.Visibility == Visibility.Collapsed, "Не заменены поля или видны потоки");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Click();
            Require(_session.Count == 1 && _session[0].Direction == TestDirection.Upload && _session[0].Options.Iperf2SenderOnly
                && _session[0].Options.BandwidthLimitBitsPerSecond == 500_000_000 && _session[0].Status == RunStatus.Measured
                && _session[0].EngineSha256 == BundledIperf.Iperf2LegacySha256, "Не выполнен единственный тарифный тест");
            Require(clock.Elapsed.TotalSeconds < 5 && DownloadValue.Text == "—" && DownloadStatus.Text == "Не проверяется", "Ожидается входящий трафик или FIN-повторы");
            SavePreview(Path.Combine(directory, "beeline-tariff-result.png"));
            AdvancedPanel.IsExpanded = true; await Task.Delay(150); UpdateLayout();
            SavePreview(Path.Combine(directory, "beeline-tariff-settings.png"));
            AdvancedPanel.IsExpanded = false;
        });
        await Test("История iPerf2: профиль, направление и причина вместо одинакового Partial", async () =>
        {
            Configure(ServerCatalog.Custom("127.0.0.1", 55208, EngineKind.Iperf2), false);
            DirectionCombo.SelectedIndex = 1; RateBox.Text = "1";
            await Click();
            Require(_lastResult!.Status == RunStatus.Partial && _lastResult.StatusLabel == "Нет отчёта сервера"
                && UploadValue.Text == "—" && _chart.Count == 0
                && UploadTxDetail.Visibility == Visibility.Visible && UploadTxDetail.Text.StartsWith("TX локально:")
                && JitterValue.Text == "—" && PhaseTitle.Text.Contains("UDP отправлен"), "Показана скорость отправителя без ACK или потеряна завершённая отправка");
            Navigate(1); await Task.Delay(150); UpdateLayout();
            var bindings = HistoryList.Columns.OfType<DataGridTextColumn>().Select(x => (x.Binding as System.Windows.Data.Binding)?.Path.Path).ToArray();
            Require(bindings.Contains(nameof(RunResult.ProfileLabel)) && bindings.Contains(nameof(RunResult.DirectionLabel))
                && bindings.Contains(nameof(RunResult.StatusLabel)), "История не различает профили и статусы");
            Require(HistoryList.Columns.All(x => x.ActualWidth >= 40)
                && HistoryList.Columns.Sum(x => x.ActualWidth) <= HistoryList.ActualWidth + 1,
                "Столбцы истории скрыты или выходят за ширину таблицы");
            await File.WriteAllTextAsync(Path.Combine(directory, "history-layout.json"), JsonSerializer.Serialize(new
                { Width = HistoryList.ActualWidth, Columns = HistoryList.Columns.Select(x => new { x.Header, x.ActualWidth }) }, VerificationJson));
            SavePreview(Path.Combine(directory, "history-no-ack.png"));
        });
        await Test("Тайминги UI: пинг, передача и локальный TX показываются до итогового отчёта", async () =>
        {
            var server = ServerCatalog.Custom("127.0.0.1", 55214, EngineKind.Iperf2) with { Iperf2Legacy = true };
            Configure(server, false); Navigate(0);
            DirectionCombo.SelectedIndex = 1; RateBox.Text = "1"; LatencyCheck.IsChecked = true;
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try
            {
                await Task.Delay(600);
                Require(PhaseTitle.Text == "Пинг до нагрузки" && !TimeInfo.Text.Contains("отчёт", StringComparison.OrdinalIgnoreCase),
                    "До начала передачи уже показывается ожидание отчёта");
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (UploadTxDetail.Visibility != Visibility.Visible && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
                Require(_running != null && UploadValue.Text == "—" && _chart.Count == 0
                    && UploadTxDetail.Text.StartsWith("TX локально:") && PhaseTitle.Text == "Передача данных"
                    && ChartHint.Text == "Ожидаем данные приёмника",
                    "Локальный TX потерян, выдан за приём или показан неверный этап");
                SavePreview(Path.Combine(directory, "timing-transferring.png"));
                await _activeUiTask!;
                Require(TimeInfo.Text.StartsWith("Весь прогон:") && _lastResult!.Stages.Any(x => x.Stage == RunStage.Transferring),
                    "Нет общего времени прогона или истории этапов");
            }
            finally { if (_running != null) { _running.Cancel(); await _activeUiTask!; } }
        });
        await Test("Нулевые или ошибочные итоги не заполняют карточки скорости", () =>
        {
            ResetResult();
            var run = new RunResult { Direction = TestDirection.Upload };
            Iperf3Parser.Apply("""{"end":{"sum_received":{"bytes":0,"bits_per_second":1000000000}}}""", run);
            ApplyResultRates(run);
            Require(run.Status == RunStatus.NoData && UploadValue.Text == "—" && run.UploadMbps == null, "Показан гигабит без передачи байтов");
            ApplyResultRates(new RunResult { Status = RunStatus.EngineError, DownloadMbps = 1000, UploadMbps = 1000 });
            Require(UploadValue.Text == "—" && DownloadValue.Text == "—" && _chart.Count == 0, "Ошибочный итог попал в карточку или график");
            return Task.CompletedTask;
        });
        await Test("Билайн: скрытые поля не меняют тариф и не запускают загрузку", async () =>
        {
            var server = _servers.Single(x => x.Id == "beeline-iperf2") with { Host = "127.0.0.1", Port = 55208, Ports = [55208] };
            Configure(server, false); BeelineTariff.SelectedItem = 100;
            ParallelBox.Text = "ошибка"; RateBox.Text = "ошибка"; DirectionCombo.SelectedIndex = 0;
            await Click();
            Require(_session.Count == 1 && _session[0].Direction == TestDirection.Upload && _session[0].Status == RunStatus.Measured
                && _session[0].ReceiverBytes == null && !DirectionObservation.Confirmed(_session[0])
                && UploadHeading.Text.Contains("UDP") && UploadStatus.Text == "Отправка завершена"
                && SummaryText.Text.Contains("не подтверждены"), "Отправка выдана за приём или запущен reverse");
            SavePreview(Path.Combine(directory, "beeline-no-report.png"));
        });
        await Test("Пинг, джиттер и потери видны рядом и различаются цветом", async () =>
        {
            Navigate(0); await Task.Delay(150);
            _session.Clear();
            var now = DateTimeOffset.Now;
            var other = new RunResult { ProfileId = "another-server", ResolvedIp = "8.8.8.8" };
            other.Latency.Add(new(ProbePhase.Idle, now.AddSeconds(-2), 900, null));
            other.Latency.Add(new(ProbePhase.Idle, now.AddSeconds(-1), 100, null));
            _session.Add(other);
            var run = new RunResult { ProfileId = "quality-server", ResolvedIp = "1.1.1.1", Direction = TestDirection.Download, Transport = TransportKind.Udp,
                ReceiverBytes = 100000, ReceiverMbps = 100, JitterMilliseconds = 12, LostPercent = 2 };
            run.Latency.AddRange([new(ProbePhase.Idle, now, 10, null), new(ProbePhase.Idle, now.AddSeconds(1), 14, null)]);
            _session.Add(run); UpdateQualityMetrics(run); UpdateLayout();
            Require(PingValue.Text == 12.0.ToString("F1") && JitterValue.Text == 12.0.ToString("F1")
                && JitterSource.Text == "мс · UDP" && PacketLossValue.Text.Contains(2.0.ToString("F2")), "Потеряны показатели или источник джиттера");
            Require(((SolidColorBrush)PingValue.Foreground).Color == Color.FromRgb(0x82,0xC6,0xAE)
                && ((SolidColorBrush)JitterValue.Foreground).Color == Color.FromRgb(0xD8,0xB8,0x84)
                && ((SolidColorBrush)PacketLossValue.Foreground).Color == Color.FromRgb(0xD9,0x9A,0x9D), "Цвет не отражает качество показателя");
            Width = 840; await Task.Delay(150); UpdateLayout();
            Require(JitterValue.IsVisible && JitterValue.ActualWidth >= 60, "Джиттер скрыт на минимальной ширине");
            SavePreview(Path.Combine(directory, "quality-metrics.png"));
            Width = 1000; ResetResult();
            Require(JitterValue.Text == "—" && PacketLossValue.Text == "Потери: —" && UploadTxDetail.Visibility == Visibility.Collapsed,
                "При новом сервере остаются старые показатели");
        });
        await Test("Смена сервера очищает прежние скорости и график", () =>
        {
            _verificationServer = null; SelectServer(_servers.First(x => x.Engine == EngineKind.Http && x.Id != _profileId));
            Require(DownloadValue.Text == "—" && UploadValue.Text == "—" && _chart.Count == 0 && _lastResult == null, "Прежнее измерение выдано за новый сервер");
            Require(PhaseTitle.Text == "Готово" && TimeInfo.Text.Length == 0, "Статус прежнего измерения остался после смены сервера");
            return Task.CompletedTask;
        });
        await Test("TCP/UDP/HTTP не получают скрытый лимит", () =>
        { _verificationServer = null; VerifyRateSwitches(directory); return Task.CompletedTask; });
        await Test("На главном экране видна ровно одна кнопка действия", async () =>
        {
            _verificationServer = null; SelectServer(_servers.First(x => x.Id == "hostkey-https"));
            Navigate(0); AdvancedPanel.IsExpanded = false; await Task.Delay(100); UpdateLayout();
            var main = (DependencyObject)((TabItem)MainTabs.Items[0]).Content;
            var buttons = VisibleButtons(main).ToArray();
            Require(buttons.Length == 1 && buttons[0] == RunButton, "Видимых кнопок: " + buttons.Length);
            SavePreview(Path.Combine(directory, "minimal-main.png"));
            Require(MeasurementNotice.IsVisible && MeasurementNotice.Text.Contains("фактической скорости линии")
                && MainScroll.ScrollableHeight < 1, $"Приписка: visible={MeasurementNotice.IsVisible}; прокрутка={MainScroll.ScrollableHeight:F1}");
            AdvancedPanel.IsExpanded = true; UpdateLayout(); SavePreview(Path.Combine(directory, "advanced.png"));
            Require(!VisibleText(ServerCombo).Any(x => x.StartsWith("ServerEntry {", StringComparison.Ordinal)), "В списке сервера показаны внутренние поля");
            AdvancedPanel.IsExpanded = false;
        });
        await Test("История, каталог, настройки и полный debug открываются", () =>
        {
            Require(HistoryList.Items.Count > 0, "Нет истории из песочницы");
            HistoryList.SelectedIndex = 0; HistoryDebug_Click(this, new RoutedEventArgs());
            Require(DebugBox.Text.Contains("trace.json", StringComparison.Ordinal), "Полный журнал из истории не открылся");
            foreach (int page in new[] { 1, 2, 3, 4 }) { Navigate(page); UpdateLayout(); SavePreview(Path.Combine(directory, "page-" + page + ".png")); }
            Navigate(0); return Task.CompletedTask;
        });
        await Test("GeoIP: IP виден, Москва выбирается автоматически", async () =>
        {
            _verificationServer = null; CustomHost.Clear(); CustomUrl.Clear(); GeoIpCheck.IsChecked = true;
            SelectServer(_servers.Single(x => x.Id == "rastrnet-https"));
            await RefreshNetworkAsync((_,_) => Task.FromResult(new NetworkIdentity("8.8.8.8","8.8.8.8","STUN","Moscow","Example",15169,new(55.75,37.61))));
            Require(CurrentServer().City == "Москва" && ConnectionInfo.Text.Contains("8.8.8.8"), "IP скрыт или выбран неверный город");
            UpdateLayout(); await Task.Delay(100);
            Require(MainScroll.ScrollableHeight < 1, "Строка IP вытеснила главный экран в прокрутку");
            SavePreview(Path.Combine(directory, "geoip-auto.png"));
            WindowPreview.Save(this, Path.Combine(directory, "geoip-window.png"));
        });
        await Test("GeoIP: поздний результат сохраняет ручной сервер", async () =>
        {
            var response = new TaskCompletionSource<NetworkIdentity>();
            var pending = RefreshNetworkAsync((_,_) => response.Task);
            SelectServer(_servers.Single(x => x.Id == "rastrnet-https"));
            response.SetResult(new("8.8.8.8","8.8.8.8","HTTPS","Moscow",Location:new(55.75,37.61)));
            await pending;
            Require(CurrentServer().Id == "rastrnet-https", "Геолокация перезаписала ручной выбор");
        });
        await Test("GeoIP: выключение сохраняется, внешний IP остаётся виден", async () =>
        {
            GeoIpCheck.IsChecked = false; string selected = CurrentServer().Id;
            await RefreshNetworkAsync((enabled,_) => { Require(!enabled,"GeoIP не отключён"); return Task.FromResult(new NetworkIdentity("1.1.1.1",null,"HTTPS",Location:new(55.75,37.61))); });
            Require(CurrentServer().Id == selected && ConnectionInfo.Text.Contains("1.1.1.1"), "Отключённая геолокация изменила сервер или скрыла IP");
            var second = new MainWindow(_store.DirectoryPath, false);
            Require(second.GeoIpCheck.IsChecked == false, "Настройка не восстановилась после запуска"); second.Close();
        });
        await Test("GeoIP: поздний ответ сохраняет сервер начатого теста", async () =>
        {
            GeoIpCheck.IsChecked = true; Configure(Http("stall"));
            var selected = ServerCombo.SelectedItem;
            var response = new TaskCompletionSource<NetworkIdentity>();
            var pending = RefreshNetworkAsync((_,_) => response.Task);
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try
            {
                Require(_running != null, "Тест не запустился");
                var city = NearestServer.Locations.Single(x => x.City == "Краснодар");
                response.SetResult(new("8.8.8.8",null,"HTTPS",Location:new(city.Latitude,city.Longitude)));
                await pending;
                Require(ReferenceEquals(ServerCombo.SelectedItem,selected), "Геолокация изменила сервер во время передачи");
            }
            finally { _running?.Cancel(); if (_activeUiTask != null) await _activeUiTask; }
            _verificationServer = null;
        });
        await Test("GeoIP: отсутствие сети сохраняет запуск теста", async () =>
        {
            GeoIpCheck.IsChecked = true; string selected = CurrentServer().Id;
            await RefreshNetworkAsync((_,_) => Task.FromResult(new NetworkIdentity(null,null,"Unavailable")));
            Require(CurrentServer().Id == selected && RunButton.IsEnabled && ConnectionInfo.Text.Contains("недоступен"), "Ошибка геолокации заблокировала приложение");
        });
        await Test("IP: два нажатия обновляют адрес без перезапуска и сохраняют разные IP в истории", async () =>
        {
            string address = "8.8.8.8"; int lookups = 0;
            _verificationNetworkLookup = (geo, _) =>
            { Require(!geo, "GeoIP задерживает запуск нагрузки"); lookups++; return Task.FromResult(new NetworkIdentity(address, address, "Sandbox")); };
            Configure(Http("both"));
            var selected = ServerCombo.SelectedItem;
            await Click();
            Require(_session.Count == 2 && _session.All(x => x.ClientExternalIp == address && x.ClientIpObservedAt <= x.Started), "Первый IP не записан до сохранения результата");
            address = "1.1.1.1";
            await Click();
            Require(lookups == 2 && ConnectionInfo.Text.Contains(address) && _session.All(x => x.ClientExternalIp == address)
                && ReferenceEquals(selected, ServerCombo.SelectedItem), "Повторный запуск использовал старый IP или заменил сервер");
            var stored = _store.Recent(4);
            Require(stored.Take(2).All(x => x.ClientExternalIp == address) && stored.Skip(2).All(x => x.ClientExternalIp == "8.8.8.8"), "Сохранённая история изменила прежний IP");
            var reopened = new RunStore(_store.DirectoryPath).Recent(4);
            Require(reopened.Select(x => x.ClientExternalIp).SequenceEqual(stored.Select(x => x.ClientExternalIp)), "IP потерян после чтения файла истории");
            Navigate(1); await Task.Delay(100); UpdateLayout();
            var column = HistoryList.Columns.OfType<DataGridTextColumn>().SingleOrDefault(x => (x.Binding as System.Windows.Data.Binding)?.Path.Path == nameof(RunResult.ClientIpLabel));
            Require(column != null && HistoryList.Columns.Sum(x => x.ActualWidth) <= HistoryList.ActualWidth + 1,
                "Колонка IP отсутствует или вытесняет таблицу");
            SavePreview(Path.Combine(directory, "history-client-ip.png")); Navigate(0);
        });
        await Test("IP: позднее обновление шапки не подменяет IP уже начатой проверки", async () =>
        {
            _verificationNetworkLookup = (_, _) => Task.FromResult(new NetworkIdentity("8.8.8.8", "1.1.1.1", "STUN"));
            Configure(Http("both")); RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_running != null, "Проверка не началась");
            ApplyNetworkIdentity(new("9.9.9.9", null, "Late"), false, _serverSelectionGeneration, false);
            await _activeUiTask!;
            Require(_session.Count == 2 && _session.All(x => x.ClientExternalIp == "8.8.8.8" && x.ClientHttpIp == "1.1.1.1")
                && _store.Recent(2).All(x => x.ClientExternalIp == "8.8.8.8"), "В результат попал поздний IP из шапки");
        });
        await Test("IP: недоступные сервисы не блокируют скорость и не сохраняют старый адрес", async () =>
        {
            foreach (bool throws in new[] { false, true })
            {
                ApplyNetworkIdentity(new("8.8.8.8", null, "Previous"), false, _serverSelectionGeneration, false);
                _verificationNetworkLookup = (_, _) => throws ? Task.FromException<NetworkIdentity>(new System.Net.Http.HttpRequestException("sandbox"))
                    : Task.FromResult(new NetworkIdentity(null, null, "Unavailable"));
                Configure(Http("both")); await Click();
                Require(ConnectionInfo.Text.Contains("недоступен") && _session.Count == 2
                    && _session.All(x => x.Status == RunStatus.Measured && x.ClientExternalIp == null && x.ClientIpLabel == "—"),
                    "Сбой IP заблокировал нагрузку или сохранил старый адрес");
            }
        });
        await Test("IP: остановка во время обновления отменяет запуск и сохраняет следующую проверку", async () =>
        {
            Configure(Http("both")); int history = _store.Recent(500).Count;
            _verificationNetworkLookup = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(null, null, "Unavailable"); };
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_running != null && PhaseTitle.Text.Contains("IP"), "Не показана подготовка IP");
            RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await _activeUiTask!;
            Require(_session.Count == 0 && _store.Recent(500).Count == history && _running == null && RunButton.IsEnabled,
                "Отмена обновления всё равно запустила нагрузку или заблокировала кнопку");
            _verificationNetworkLookup = (_, _) => Task.FromResult(new NetworkIdentity("1.1.1.1", null, "Sandbox"));
            await Click(); Require(_session.Count == 2 && _session.All(x => x.ClientExternalIp == "1.1.1.1"), "После отмены не работает следующий запуск");
        });
        _verificationNetworkLookup = (_, _) => Task.FromResult(new NetworkIdentity("8.8.8.8", "8.8.8.8", "Sandbox"));
        await Test("Билайн: закрытый список 100–1000 и сохранение выбранного тарифа", () =>
        {
            Require(!BeelineTariff.IsEditable && BeelineTariff.Items.Cast<int>().SequenceEqual(Enumerable.Range(1,10).Select(x => x * 100)), "Неверные значения списка тарифа");
            _verificationServer = null; SelectServer(_servers.Single(x => x.Id == "beeline-iperf2"));
            foreach (int tariff in BeelineTariff.Items)
            {
                BeelineTariff.SelectedItem = tariff;
                var options = ReadOptions();
                Require(options.BandwidthLimitBitsPerSecond == (long)tariff * 1_000_000 && options.Direction == TestDirection.Upload,
                    "Выбор тарифа не применён к общей нагрузке");
            }
            var second = new MainWindow(_store.DirectoryPath, false);
            Require((int?)second.BeelineTariff.SelectedItem == 1000, "Тариф не восстановился после запуска"); second.Close();
            BeelineTariff.SelectedItem = 500;
            return Task.CompletedTask;
        });
        await Test("Серверы: сохранение адреса, новый запуск, сброс и запрет неверного ввода", async () =>
        {
            _verificationServer = null; CustomHost.Clear(); CustomUrl.Clear();
            var original = _servers.Single(x => x.Id == "beeline-iperf2"); SelectServer(original);
            Navigate(2); await Task.Delay(80); UpdateLayout(); CatalogSearch.Text = original.Host;
            CatalogGrid.SelectedItem = original; CatalogHost.Text = "corrected.example.net";
            CatalogSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(CurrentServer().Host == "corrected.example.net" && CurrentServer().Port == original.Port
                && CurrentServer().HasLocalOverride && CatalogResetButton.IsEnabled && _chart.Count == 0
                && ObservationText.Text.Contains("не проверено"), "Правка не применена или оставила старые результаты");
            var second = new MainWindow(_store.DirectoryPath, false);
            try
            {
                var loaded = second._servers.Single(x => x.Id == original.Id);
                second.SelectServer(loaded);
                Require(second.CurrentServer().Host == "corrected.example.net" && second.ReadOptions().Iperf2SenderOnly,
                    "Новый запуск потерял адрес или исходящий режим");
            }
            finally { second.Close(); }
            CatalogHost.Text = "https://bad.example.net:123";
            CatalogSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(CurrentServer().Host == "corrected.example.net" && CatalogEditStatus.Text.StartsWith("Не удалось сохранить"), "Неверный ввод принят");
            CatalogGrid.SelectedItem = _servers.Single(x => x.Id == original.Id);
            CatalogHost.Text = "corrected.example.net";
            CatalogEditStatus.Text = "Адрес сохранён. Порт остаётся прежним.";
            await Task.Delay(100); UpdateLayout();
            Require(CatalogGrid.ItemContainerGenerator.ContainerFromItem(CatalogGrid.SelectedItem) is DataGridRow { IsVisible: true }, "Сохранённая строка осталась вне видимой части списка");
            SavePreview(Path.Combine(directory, "server-editor.png"));
            CatalogResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(CurrentServer().Host == original.Host && !CurrentServer().HasLocalOverride && !CatalogResetButton.IsEnabled,
                "Сброс адреса не применён");
            CatalogSearch.Clear(); Navigate(0);
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "ui-summary.txt"), $"TOTAL={cases.Count}; PASS={cases.Count - failed}; FAIL={failed}");
        return failed == 0;
    }
    private static IEnumerable<Button> VisibleButtons(DependencyObject root)
    {
        if (root is Button { IsVisible: true } button) yield return button;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisibleButtons(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static IEnumerable<string> VisibleText(DependencyObject root)
    {
        if (root is TextBlock { IsVisible: true } text) yield return text.Text;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisibleText(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static readonly JsonSerializerOptions VerificationJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
