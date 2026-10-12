# Сторонние компоненты SpeedForge

Встроенная Windows x64 сборка iPerf3 3.22 получена из проекта ar51an/iperf3-win-builds:
https://github.com/ar51an/iperf3-win-builds/releases/tag/3.22

Архив: iperf-3.22-win64.zip. SHA-256 сверён с digest в API GitHub Releases:
c9feabb3d721039508ccb81f74c2ada3ae11b9e75d33d24510573c2aa21da420

SHA-256 iperf3.exe:
bcafba4aff825fa94685b6b3d049f8b1338c69e6dc1d06e6a00ac046191e4999

Сборка содержит iPerf3 и cygwin1.dll 3.6.10. Компоненты извлекаются из встроенного архива в `%LOCALAPPDATA%/SpeedForge/tools/iperf3-3.22`; приложение проверяет их встроенные хэши перед каждым запуском. Автоматического обновления или загрузки компонентов нет.

Это сторонняя сборка Windows; она не является официальным бинарником ESnet. Исходники программы iPerf3 опубликованы ESnet: https://github.com/esnet/iperf/tree/3.22 . Лицензия включена в IPERF-LICENSE.txt; архив исходников — source/iperf-3.22.tar.gz.

Лицензии Cygwin и newlib включены в CYGWIN-LICENSE.txt, GPL-3.txt, NEWLIB-LICENSE.txt. Полный соответствующий исходный пакет Cygwin 3.6.10-1, включая cygport-файл сборки и newlib-cygwin, находится в source/cygwin-3.6.10-1-src.tar.xz.
Источник: https://cygwin.com/pub/cygwin/src/release/cygwin/cygwin-3.6.10-1-src.tar.xz
SHA-512 проверен по официальному setup.ini:
fa11f0332f97f915b81c189412d83fac8a459653d8941b609e09776a55b3c9f4f669caa0d132f5b2011e924e0ef9922a137b59af9c086d3f052f3ecc318e1e56

Лицензии и исходные архивы включены в ресурсы самого exe. Их можно извлечь через «Инструменты → Лицензии и исходники». Для запуска и переноса программы отдельная папка third-party не нужна.

Также встроена официальная native Windows x64 сборка iPerf2 2.2.2 (29 September 2026):
https://sourceforge.net/projects/iperf2/files/iperf-2.2.2-win64.exe/download
SHA-256 сверён с опубликованным SourceForge:
1030be63b604147a2a27f783e162237606059de0e7ddb125834328a58e8dc2aa

Лицензия сохранена в IPERF2-LICENSE.txt. Снимок официального исходного репозитория `https://git.code.sf.net/p/iperf2/code`, commit `fcac4245061232f1616874a04843f7ca13a41fba`, включён как source/iperf2-upstream-source.zip. Это снимок upstream на дату доработки, а не утверждение воспроизводимости бинарника из этого коммита. Бинарник скачан из опубликованного выпуска.

Для профилей Билайна встроен исходный Windows-клиент iPerf2 2.0.8 из предоставленного архива `Iperf speedcheck.gldn.net.zip`. Он использует старый формат UDP-отчёта, который современные клиенты 2.2.1/2.2.2 в контрольном консольном прогоне прочитали неверно. Обратное направление проверяется отдельным современным клиентом. На первом профиле приложение проверяет совместимость; если старый клиент не получает корректного отчёта, проверяется встроенный 2.2.2. Подтверждённый вариант используется для следующих профилей. Произвольный выбранный клиент не заменяет эту проверку.

SHA-256 исходного exe: `575691cf45a3586fb3f6953f134a1a3d9edf0d03ade09aff5a6eb422bd080726`. Бинарник не изменён; в ресурсе он хранится под именем `iperf2.exe`. Лицензия версии сохранена в `IPERF2-LEGACY-LICENSE.txt`.

Исходный пакет iPerf2 2.0.8 включён как `source/iperf-2.0.8.tar.gz`. Получен с https://iperf.fr/download/source/iperf-2.0.8-source.tar.gz; SHA-256 совпал с опубликованным SourceForge: `c98d5dca50a6602734b35dfd430e3f518e49934fe63d6eb6954d8372f6d6b4d2`. Ссылка выпуска: https://sourceforge.net/projects/iperf2/files/iperf-2.0.8.tar.gz/download . Это исходники версии; воспроизводимость предоставленного Windows-бинарника из этого пакета не утверждается.