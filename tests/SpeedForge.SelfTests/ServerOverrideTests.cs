using SpeedForge.Core;

internal static class ServerOverrideTests
{
    public static void Register(Action<string, Action> test)
    {
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void InDirectory(Action<string> action)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SpeedForge-overrides-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { action(directory); }
            finally { Directory.Delete(directory, true); }
        }
        test("Адреса: сохранение и новый запуск сохраняют порты и профили iPerf", () => InDirectory(directory =>
        {
            var original = ServerCatalog.LoadPublished();
            var store = new ServerOverrides(directory);
            foreach (var server in original.Where(x => x.Engine != EngineKind.Http)) store.Save(server.Id, "updated.example.net");
            var loaded = new ServerOverrides(directory).Load();
            foreach (var server in loaded.Where(x => x.Engine != EngineKind.Http))
            {
                var before = original.Single(x => x.Id == server.Id);
                Require(server.Host == "updated.example.net" && server.HasLocalOverride && server.Port == before.Port
                    && server.Ports.SequenceEqual(before.Ports) && server.Iperf2Profiles.SequenceEqual(before.Iperf2Profiles)
                    && server.Iperf2Legacy == before.Iperf2Legacy, "Изменены параметры, кроме адреса");
            }
            var beeline = loaded.Single(x => x.Id == "beeline-iperf2");
            Require(BeelineTest.Applies(beeline) && DirectionalTest.Plan(beeline, BeelineTest.Configure(new(), 500), true)
                .SequenceEqual(new[] { TestDirection.Upload }), "Изменённый Билайн потерял исходящий режим");
            var reset = store.Save(beeline.Id, null).Single(x => x.Id == beeline.Id);
            Require(reset.Host == "speedcheck.gldn.net" && reset.Port == 5001 && !reset.HasLocalOverride, "Сброс не восстановил исходный профиль");
        }));
        test("Адреса HTTP: меняется хост обоих URL, сохраняются схема, порт, путь и запрос", () => InDirectory(directory =>
        {
            var store = new ServerOverrides(directory);
            foreach (var before in ServerCatalog.LoadPublished().Where(x => x.Http != null))
            {
                var after = store.Save(before.Id, "2001:db8::123").Single(x => x.Id == before.Id);
                PublicHttpMeasurement.ValidateUri(after.Http!.DownloadUrl, after);
                if (after.Http.UploadUrl != null) PublicHttpMeasurement.ValidateUri(after.Http.UploadUrl, after);
                foreach (var pair in new[] { (before.Http!.DownloadUrl, after.Http!.DownloadUrl), (before.Http.UploadUrl, after.Http.UploadUrl) })
                {
                    if (pair.Item1 == null) { Require(pair.Item2 == null, "Добавлена отдача"); continue; }
                    var a = new Uri(pair.Item1); var b = new Uri(pair.Item2!);
                    Require(b.Host.Trim('[', ']') == "2001:db8::123" && a.Port == b.Port && a.Scheme == b.Scheme
                        && a.PathAndQuery == b.PathAndQuery && a.Fragment == b.Fragment, "Повреждён тестовый URL");
                }
                Require(before.Port == after.Port && before.Http.Protocol == after.Http.Protocol
                    && before.Http.UploadMethod == after.Http.UploadMethod, "Изменён протокол HTTP");
            }
        }));
        test("Адреса: неверный ввод и повреждённый файл не стирают сохранённые изменения", () => InDirectory(directory =>
        {
            var store = new ServerOverrides(directory); const string id = "beeline-iperf2";
            store.Save(id, "udp.example.net"); string path = Path.Combine(directory, "server-overrides.json");
            string before = File.ReadAllText(path);
            foreach (var invalid in new[] { "", "https://example.net", "example.net:5001", "host --reverse", "a..b" })
            {
                try { store.Save(id, invalid); throw new Exception("Принят неверный адрес"); }
                catch (InvalidDataException) { }
                Require(File.ReadAllText(path) == before, "Неверный ввод изменил файл");
            }
            File.WriteAllText(path, "{broken");
            try { store.Save(id, null); throw new Exception("Повреждённый файл перезаписан"); }
            catch (InvalidDataException) { }
            Require(File.ReadAllText(path) == "{broken", "Потерян повреждённый файл");
        }));
    }
}
