# Реальные сетевые проверки 1.0.2

Все 65 записей каталога и отдельно 60 UDP-вариантов iPerf3 проверены на максимальной скорости: по 8 секунд, 4 потока, без лимита, по одному серверу. Билайн использует свои 12 профилей с исходной нагрузкой. Значения относятся к проверенному подключению и времени запуска.

Все 12 профилей Билайна также выполнены оригинальным клиентом через Python subprocess по 10 секунд. Отправка завершалась без таймаута; отчёт приёмника не приходил. Исходный Python-парсер показывал локальную скорость отправителя. Приложение сохраняет её как TX, оставляя приём неизвестным.

Автоматические проверки: 112 логики и сетевых регрессий, 33 сценария песочницы, 36 интерфейса и 3 отдельных сценария таймингов. Все прошли. Это число проверок программы, а не число успешных публичных серверов. В отдельной локальной лаборатории все 12 профилей выполнены с исходной нагрузкой по 10 секунд и подтвердили приём.

После изменения таймингов повторены 12 записей: проблемные точки и работающие TCP/HTTPS-серверы. В таблице для них показан последний запуск; остальные строки относятся к полному прогону.

| Сервер | Метод | ↓ Мбит/с | ↑ Мбит/с | Наблюдение |
|---|---|---:|---:|---|
| speedcheck.gldn.net | Iperf2 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| spd-rudp.hostkey.ru | Http / Tcp | 466,54 | — | Загрузка и отдача подтверждены |
| spd-rudp.hostkey.ru | Http / Tcp | 442,17 | — | Загрузка и отдача подтверждены |
| speedtest.rastrnet.ru | Http / Tcp | 403,07 | — | Подтверждена загрузка на ПК; отдача не подтверждена |
| speedtest.rastrnet.ru | Iperf3 / Tcp | — | — | Измерение не подтверждено — подробности в журнале |
| astrakhan1.speedtest.corbina.net | Iperf3 / Tcp | 516,15 | 497,31 | Загрузка и отдача подтверждены |
| irkst.st.mtsws.net | Iperf3 / Tcp | 368,45 | 376,61 | Загрузка и отдача подтверждены |
| kazst.st.mtsws.net | Iperf3 / Tcp | 492,37 | 495,93 | Загрузка и отдача подтверждены |
| kndst.st.mtsws.net | Iperf3 / Tcp | 516,96 | 519,83 | Загрузка и отдача подтверждены |
| mskst.st.mtsws.net | Iperf3 / Tcp | 497,07 | 502,33 | Загрузка и отдача подтверждены |
| norst.st.mtsws.net | Iperf3 / Tcp | 310,72 | 409,81 | Загрузка и отдача подтверждены |
| nskst.st.mtsws.net | Iperf3 / Tcp | 387,04 | 445,80 | Загрузка и отдача подтверждены |
| pkst.st.mtsws.net | Iperf3 / Tcp | 242,58 | 338,67 | Загрузка и отдача подтверждены |
| sahst.st.mtsws.net | Iperf3 / Tcp | 333,92 | 243,07 | Загрузка и отдача подтверждены |
| spbst.st.mtsws.net | Iperf3 / Tcp | 473,68 | 469,63 | Загрузка и отдача подтверждены |
| spd-rudp.hostkey.ru | Iperf3 / Tcp | 422,03 | 497,65 | Загрузка и отдача подтверждены |
| speed-nn.vtt.net | Iperf3 / Tcp | 252,25 | 183,49 | Загрузка и отдача подтверждены |
| speed-sar.vtt.net | Iperf3 / Tcp | 506,96 | 138,47 | Загрузка и отдача подтверждены |
| speed-vgd.vtt.net | Iperf3 / Tcp | 309,91 | 179,12 | Загрузка и отдача подтверждены |
| speed-vld.vtt.net | Iperf3 / Tcp | 475,56 | 472,82 | Загрузка и отдача подтверждены |
| speed.vtt.net | Iperf3 / Tcp | 385,21 | 115,32 | Загрузка и отдача подтверждены |
| st.barnaul.ertelecom.ru | Iperf3 / Tcp | 294,36 | 444,75 | Загрузка и отдача подтверждены |
| st.bryansk.ertelecom.ru | Iperf3 / Tcp | 436,04 | 454,22 | Загрузка и отдача подтверждены |
| st.cheb.ertelecom.ru | Iperf3 / Tcp | 439,54 | 133,82 | Загрузка и отдача подтверждены |
| st.chel.ertelecom.ru | Iperf3 / Tcp | 448,81 | 467,30 | Загрузка и отдача подтверждены |
| st.chelny.ertelecom.ru | Iperf3 / Tcp | 484,69 | 465,65 | Загрузка и отдача подтверждены |
| st.ekat.ertelecom.ru | Iperf3 / Tcp | 401,02 | 437,26 | Загрузка и отдача подтверждены |
| st.irkutsk.ertelecom.ru | Iperf3 / Tcp | 283,58 | 380,71 | Загрузка и отдача подтверждены |
| st.izhevsk.ertelecom.ru | Iperf3 / Tcp | 458,14 | 384,88 | Загрузка и отдача подтверждены |
| st.kirov.ertelecom.ru | Iperf3 / Tcp | 407,90 | 477,30 | Загрузка и отдача подтверждены |
| st.krsk.ertelecom.ru | Iperf3 / Tcp | 297,10 | 435,66 | Загрузка и отдача подтверждены |
| st.kurgan.ertelecom.ru | Iperf3 / Tcp | 427,12 | 487,55 | Загрузка и отдача подтверждены |
| st.kursk.ertelecom.ru | Iperf3 / Tcp | 480,92 | 418,96 | Загрузка и отдача подтверждены |
| st.kzn.ertelecom.ru | Iperf3 / Tcp | 476,24 | 485,86 | Загрузка и отдача подтверждены |
| st.lipetsk.ertelecom.ru | Iperf3 / Tcp | 472,70 | 379,25 | Загрузка и отдача подтверждены |
| st.mgn.ertelecom.ru | Iperf3 / Tcp | 466,69 | 399,53 | Загрузка и отдача подтверждены |
| st.nk.ertelecom.ru | Iperf3 / Tcp | 440,88 | 464,83 | Загрузка и отдача подтверждены |
| st.nn.ertelecom.ru | Iperf3 / Tcp | 361,43 | 440,58 | Загрузка и отдача подтверждены |
| st.nsk.ertelecom.ru | Iperf3 / Tcp | 392,02 | 387,00 | Загрузка и отдача подтверждены |
| st.omsk.ertelecom.ru | Iperf3 / Tcp | 397,40 | 438,48 | Загрузка и отдача подтверждены |
| st.oren.ertelecom.ru | Iperf3 / Tcp | 403,99 | 462,77 | Загрузка и отдача подтверждены |
| st.penza.ertelecom.ru | Iperf3 / Tcp | 439,00 | 467,32 | Загрузка и отдача подтверждены |
| st.perm.ertelecom.ru | Iperf3 / Tcp | 381,84 | 472,90 | Загрузка и отдача подтверждены |
| st.rostov.ertelecom.ru | Iperf3 / Tcp | 498,03 | 498,74 | Загрузка и отдача подтверждены |
| st.ryazan.ertelecom.ru | Iperf3 / Tcp | 464,07 | 506,51 | Загрузка и отдача подтверждены |
| st.samara.ertelecom.ru | Iperf3 / Tcp | 460,16 | 464,37 | Загрузка и отдача подтверждены |
| st.saratov.ertelecom.ru | Iperf3 / Tcp | 484,88 | 441,08 | Загрузка и отдача подтверждены |
| st.spb.ertelecom.ru | Iperf3 / Tcp | 467,60 | 466,55 | Загрузка и отдача подтверждены |
| st.tmn.ertelecom.ru | Iperf3 / Tcp | 430,24 | 464,02 | Загрузка и отдача подтверждены |
| st.tomsk.ertelecom.ru | Iperf3 / Tcp | 292,31 | 465,76 | Загрузка и отдача подтверждены |
| st.tula.ertelecom.ru | Iperf3 / Tcp | 257,18 | 476,75 | Загрузка и отдача подтверждены |
| st.tver.ertelecom.ru | Iperf3 / Tcp | 500,86 | 490,26 | Загрузка и отдача подтверждены |
| st.ufa.ertelecom.ru | Iperf3 / Tcp | 391,20 | 481,09 | Загрузка и отдача подтверждены |
| st.ulsk.ertelecom.ru | Iperf3 / Tcp | 455,18 | 447,40 | Загрузка и отдача подтверждены |
| st.volgograd.ertelecom.ru | Iperf3 / Tcp | — | — | Измерение не подтверждено — подробности в журнале |
| st.voronezh.ertelecom.ru | Iperf3 / Tcp | 441,47 | 465,67 | Загрузка и отдача подтверждены |
| st.yar.ertelecom.ru | Iperf3 / Tcp | 487,84 | 447,71 | Загрузка и отдача подтверждены |
| st.yola.ertelecom.ru | Iperf3 / Tcp | 465,39 | 439,68 | Загрузка и отдача подтверждены |
| tumst.st.mtsws.net | Iperf3 / Tcp | 413,97 | 453,13 | Загрузка и отдача подтверждены |
| vldvst.st.mtsws.net | Iperf3 / Tcp | — | — | Измерение не подтверждено — подробности в журнале |
| volst.st.mtsws.net | Iperf3 / Tcp | 482,64 | 489,56 | Загрузка и отдача подтверждены |
| voronezh-speedtest.corbina.net | Iperf3 / Tcp | 176,42 | 152,86 | Загрузка и отдача подтверждены |
| yarst.st.mtsws.net | Iperf3 / Tcp | — | — | Измерение не подтверждено — подробности в журнале |
| yktst.st.mtsws.net | Iperf3 / Tcp | 273,21 | 365,00 | Загрузка и отдача подтверждены |
| speedtest.selectel.ru | Http / Tcp | 484,69 | — | Подтверждена загрузка на ПК; отдача не подтверждена |
| speedtest.rastrnet.ru | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| astrakhan1.speedtest.corbina.net | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| irkst.st.mtsws.net | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| kazst.st.mtsws.net | Iperf3 / Udp | 527,69 | 539,69 | Загрузка и отдача подтверждены |
| kndst.st.mtsws.net | Iperf3 / Udp | 529,05 | 541,24 | Загрузка и отдача подтверждены |
| mskst.st.mtsws.net | Iperf3 / Udp | 530,27 | 524,66 | Загрузка и отдача подтверждены |
| norst.st.mtsws.net | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| nskst.st.mtsws.net | Iperf3 / Udp | 526,45 | 517,74 | Загрузка и отдача подтверждены |
| pkst.st.mtsws.net | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| sahst.st.mtsws.net | Iperf3 / Udp | 528,17 | 532,09 | Загрузка и отдача подтверждены |
| spbst.st.mtsws.net | Iperf3 / Udp | 527,81 | 522,67 | Загрузка и отдача подтверждены |
| spd-rudp.hostkey.ru | Iperf3 / Udp | 507,86 | 536,74 | Загрузка и отдача подтверждены |
| speed-nn.vtt.net | Iperf3 / Udp | 519,36 | 519,03 | Загрузка и отдача подтверждены |
| speed-sar.vtt.net | Iperf3 / Udp | 519,80 | 535,57 | Загрузка и отдача подтверждены |
| speed-vgd.vtt.net | Iperf3 / Udp | 431,12 | 470,88 | Загрузка и отдача подтверждены |
| speed-vld.vtt.net | Iperf3 / Udp | 523,20 | 519,58 | Загрузка и отдача подтверждены |
| speed.vtt.net | Iperf3 / Udp | 477,79 | 464,47 | Загрузка и отдача подтверждены |
| st.barnaul.ertelecom.ru | Iperf3 / Udp | 530,25 | 537,98 | Загрузка и отдача подтверждены |
| st.bryansk.ertelecom.ru | Iperf3 / Udp | 519,41 | 539,38 | Загрузка и отдача подтверждены |
| st.cheb.ertelecom.ru | Iperf3 / Udp | 526,14 | 472,89 | Загрузка и отдача подтверждены |
| st.chel.ertelecom.ru | Iperf3 / Udp | 526,24 | 513,61 | Загрузка и отдача подтверждены |
| st.chelny.ertelecom.ru | Iperf3 / Udp | 529,75 | 537,65 | Загрузка и отдача подтверждены |
| st.ekat.ertelecom.ru | Iperf3 / Udp | 520,43 | 515,12 | Загрузка и отдача подтверждены |
| st.irkutsk.ertelecom.ru | Iperf3 / Udp | 525,68 | 517,36 | Загрузка и отдача подтверждены |
| st.izhevsk.ertelecom.ru | Iperf3 / Udp | 521,75 | 521,81 | Загрузка и отдача подтверждены |
| st.kirov.ertelecom.ru | Iperf3 / Udp | 528,82 | 476,51 | Загрузка и отдача подтверждены |
| st.krsk.ertelecom.ru | Iperf3 / Udp | 528,33 | 510,35 | Загрузка и отдача подтверждены |
| st.kurgan.ertelecom.ru | Iperf3 / Udp | 528,53 | 539,80 | Загрузка и отдача подтверждены |
| st.kursk.ertelecom.ru | Iperf3 / Udp | 513,73 | 540,18 | Загрузка и отдача подтверждены |
| st.kzn.ertelecom.ru | Iperf3 / Udp | 530,23 | 539,78 | Загрузка и отдача подтверждены |
| st.lipetsk.ertelecom.ru | Iperf3 / Udp | 523,13 | 494,66 | Загрузка и отдача подтверждены |
| st.mgn.ertelecom.ru | Iperf3 / Udp | 523,97 | 483,51 | Загрузка и отдача подтверждены |
| st.nk.ertelecom.ru | Iperf3 / Udp | 520,52 | 539,95 | Загрузка и отдача подтверждены |
| st.nn.ertelecom.ru | Iperf3 / Udp | 530,21 | 530,85 | Загрузка и отдача подтверждены |
| st.nsk.ertelecom.ru | Iperf3 / Udp | 530,51 | 537,62 | Загрузка и отдача подтверждены |
| st.omsk.ertelecom.ru | Iperf3 / Udp | 530,12 | 538,15 | Загрузка и отдача подтверждены |
| st.oren.ertelecom.ru | Iperf3 / Udp | 515,35 | 468,47 | Загрузка и отдача подтверждены |
| st.penza.ertelecom.ru | Iperf3 / Udp | 523,20 | 472,67 | Загрузка и отдача подтверждены |
| st.perm.ertelecom.ru | Iperf3 / Udp | 524,24 | 538,97 | Загрузка и отдача подтверждены |
| st.rostov.ertelecom.ru | Iperf3 / Udp | 525,46 | 540,29 | Загрузка и отдача подтверждены |
| st.ryazan.ertelecom.ru | Iperf3 / Udp | 527,68 | 523,28 | Загрузка и отдача подтверждены |
| st.samara.ertelecom.ru | Iperf3 / Udp | 522,66 | 524,40 | Загрузка и отдача подтверждены |
| st.saratov.ertelecom.ru | Iperf3 / Udp | 526,81 | 523,22 | Загрузка и отдача подтверждены |
| st.spb.ertelecom.ru | Iperf3 / Udp | 530,09 | 538,74 | Загрузка и отдача подтверждены |
| st.tmn.ertelecom.ru | Iperf3 / Udp | 526,70 | 462,87 | Загрузка и отдача подтверждены |
| st.tomsk.ertelecom.ru | Iperf3 / Udp | 523,33 | 477,77 | Загрузка и отдача подтверждены |
| st.tula.ertelecom.ru | Iperf3 / Udp | 528,78 | 522,93 | Загрузка и отдача подтверждены |
| st.tver.ertelecom.ru | Iperf3 / Udp | 529,89 | 524,51 | Загрузка и отдача подтверждены |
| st.ufa.ertelecom.ru | Iperf3 / Udp | 528,06 | 513,58 | Загрузка и отдача подтверждены |
| st.ulsk.ertelecom.ru | Iperf3 / Udp | 528,16 | 523,15 | Загрузка и отдача подтверждены |
| st.volgograd.ertelecom.ru | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| st.voronezh.ertelecom.ru | Iperf3 / Udp | 520,49 | 517,74 | Загрузка и отдача подтверждены |
| st.yar.ertelecom.ru | Iperf3 / Udp | 525,68 | 511,39 | Загрузка и отдача подтверждены |
| st.yola.ertelecom.ru | Iperf3 / Udp | 529,27 | 540,12 | Загрузка и отдача подтверждены |
| tumst.st.mtsws.net | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| vldvst.st.mtsws.net | Iperf3 / Udp | 523,96 | 512,02 | Загрузка и отдача подтверждены |
| volst.st.mtsws.net | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| voronezh-speedtest.corbina.net | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| yarst.st.mtsws.net | Iperf3 / Udp | — | — | Измерение не подтверждено — подробности в журнале |
| yktst.st.mtsws.net | Iperf3 / Udp | 530,04 | 509,47 | Загрузка и отдача подтверждены |
