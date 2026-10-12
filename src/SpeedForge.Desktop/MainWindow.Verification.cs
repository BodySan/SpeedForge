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
    public async Task<bool> VerifySandboxAsync(string directory, bool resetOnly = false, bool geoOnly = false)
    {
        Directory.CreateDirectory(directory);
        var cases = new List<object>();
        int failed = 0;
        void Require(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
        async Task Test(string name, Func<Task> action)
        {
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
            DurationBox.Text = "3"; ParallelBox.Text = "1"; RateBox.Text = "10";
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
        await Test("Версия 1.0.1 и многоразмерная иконка встроены в exe", () =>
        {
            Require(typeof(MainWindow).Assembly.GetName().Version == new Version(1,0,1,0), "Неверная версия assembly");
            Require(Icon != null && Title.Contains("1.0", StringComparison.Ordinal), "Нет иконки или версии в окне");
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
        await Test("Билайн: одна кнопка проходит 8 профилей и проверяет reverse", async () =>
        {
            var server = _servers.Single(x => x.Id == "beeline-iperf2") with { Host = "127.0.0.1", Port = 55214, Ports = [55214] };
            Configure(server);
            Require(Iperf2ProfilesCheck.Visibility == Visibility.Visible && !ParallelBox.IsEnabled, "Профили не включились автоматически");
            await Click();
            Require(_session.Count == 9 && _session.Take(8).All(x => x.Status == RunStatus.Measured
                && x.EngineSha256 == BundledIperf.Iperf2LegacySha256) && _session[^1].Status == RunStatus.Timeout, "Не пройдены все профили совместимым клиентом");
            Require(double.Parse(UploadValue.Text) == Math.Round(_session.Take(8).Max(x => x.ReceiverMbps)!.Value,1), "Итог не использует лучший приёмный результат");
            SavePreview(Path.Combine(directory, "beeline-profiles.png"));
        });
        await Test("История iPerf2: профиль, направление и причина вместо одинакового Partial", async () =>
        {
            Configure(ServerCatalog.Custom("127.0.0.1", 55208, EngineKind.Iperf2), false);
            DirectionCombo.SelectedIndex = 1; RateBox.Text = "1";
            await Click();
            Require(_lastResult!.Status == RunStatus.Partial && _lastResult.StatusLabel == "Нет отчёта сервера"
                && UploadValue.Text == "—" && _chart.Count == 0, "Показана скорость отправителя без ACK");
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
        await Test("Билайн: ручная загрузка не меняет непроверенную карточку отдачи", async () =>
        {
            var server = _servers.Single(x => x.Id == "beeline-iperf2") with { Host = "127.0.0.1", Port = 55210, Ports = [55210] };
            Configure(server, false);
            Iperf2ProfilesCheck.IsChecked = false;
            ParallelBox.Text = "4";
            Iperf2ProfilesCheck.IsChecked = true;
            Require(ParallelBox.Text == "1" && !ParallelBox.IsEnabled, "Ручная загрузка наследует скрытые четыре потока");
            DirectionCombo.SelectedIndex = 0;
            await Click();
            Require(_session.Count == 1 && _session[0].Direction == TestDirection.Download && _session[0].Status == RunStatus.Measured
                && UploadValue.Text == "—" && UploadStatus.Text == "Не проверено", "Отдача отмечена неуспешной без запуска");
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
        await Test("На главном экране видна ровно одна кнопка действия", () =>
        {
            _verificationServer = null; SelectServer(_servers.First(x => x.Id == "hostkey-https"));
            Navigate(0); AdvancedPanel.IsExpanded = false; UpdateLayout();
            var main = (DependencyObject)((TabItem)MainTabs.Items[0]).Content;
            var buttons = VisibleButtons(main).ToArray();
            Require(buttons.Length == 1 && buttons[0] == RunButton, "Видимых кнопок: " + buttons.Length);
            SavePreview(Path.Combine(directory, "minimal-main.png"));
            AdvancedPanel.IsExpanded = true; UpdateLayout(); SavePreview(Path.Combine(directory, "advanced.png"));
            Require(!VisibleText(ServerCombo).Any(x => x.StartsWith("ServerEntry {", StringComparison.Ordinal)), "В списке сервера показаны внутренние поля");
            AdvancedPanel.IsExpanded = false;
            return Task.CompletedTask;
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
