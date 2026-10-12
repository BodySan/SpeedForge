# Источники проекта

Каталог встроен в `src/SpeedForge.Core/servers.json`; у каждой записи указаны источник, происхождение, адрес и параметры. Результаты контрольных подключений находятся в `server-observations.json` и не запрещают повторный тест.

- Российский iPerf3-каталог: https://github.com/itdoginfo/russian-iperf3-servers
- Официальный список МТС: https://lg.mtu.ru/list.html
- HOSTKEY: https://hostkey.ru/documentation/technical/exist_server_using/speedtest/
- Selectel: https://selectel.ru/speedtest/
- RastrNet: https://speedtest.rastrnet.ru/
- Билайн iPerf2: предоставленные пользователем Python-каталог и архив `Iperf speedcheck.gldn.net.zip`. Архив содержит один адрес и восемь профилей нагрузки; подробности в [BEELINE_PROFILES.md](BEELINE_PROFILES.md).

Параметры iPerf2: https://iperf2.sourceforge.io/iperf-manpage.html
Документация iPerf3: https://software.es.net/iperf/
Источники и хэши встроенных Windows-бинарников, лицензии и соответствующие исходные пакеты перечислены в [third-party/NOTICE.md](../third-party/NOTICE.md). Сборка Windows iPerf3 использует Cygwin; исходные архивы включены в поставку и в ресурсы portable exe.

Оформление системного заголовка: https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute