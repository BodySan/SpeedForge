# Проверка серверов для 1.0.1

Проверены все 65 записей каталога, включая HTTP/HTTPS и профили Билайна. Все 60 серверов iPerf3 отдельно проверены в UDP. Измерения по 3 секунды с общим лимитом 10 Мбит/с проверяют передачу данных, а не максимум тарифа. При необходимости проверены другие порты и повторены односторонние результаты.

Результаты относятся к этому подключению и моменту проверки. Ошибка или занятость не означает, что сервер всегда недоступен.

| Сервер | Метод | Протокол | Результат | На ПК | С ПК |
|---|---|---|---|---|---|
| speedcheck.gldn.net | Iperf2 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Partial |
| spd-rudp.hostkey.ru | Http | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| spd-rudp.hostkey.ru | Http | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| speedtest.rastrnet.ru | Http | Tcp | Подтверждена загрузка на ПК; отдача не подтверждена | Measured | У точки нет отдачи |
| speedtest.rastrnet.ru | Iperf3 | Tcp | Измерение не подтверждено — подробности в журнале | NetworkError | NetworkError |
| speedtest.rastrnet.ru | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | NetworkError | NetworkError |
| astrakhan1.speedtest.corbina.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| astrakhan1.speedtest.corbina.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| irkst.st.mtsws.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| irkst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| kazst.st.mtsws.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| kazst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | NetworkError | NetworkError |
| kndst.st.mtsws.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| kndst.st.mtsws.net | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| mskst.st.mtsws.net | Iperf3 | Tcp | Измерение не подтверждено — подробности в журнале | ServerBusy | NetworkError |
| mskst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | NetworkError | NetworkError |
| norst.st.mtsws.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| norst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| nskst.st.mtsws.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| nskst.st.mtsws.net | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| pkst.st.mtsws.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| pkst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| sahst.st.mtsws.net | Iperf3 | Tcp | Измерение не подтверждено — подробности в журнале | NetworkError | ServerBusy |
| sahst.st.mtsws.net | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| spbst.st.mtsws.net | Iperf3 | Tcp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| spbst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| spd-rudp.hostkey.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| spd-rudp.hostkey.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| speed-nn.vtt.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| speed-nn.vtt.net | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| speed-sar.vtt.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| speed-sar.vtt.net | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| speed-vgd.vtt.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| speed-vgd.vtt.net | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| speed-vld.vtt.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| speed-vld.vtt.net | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| speed.vtt.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| speed.vtt.net | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.barnaul.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.barnaul.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.bryansk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.bryansk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.cheb.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.cheb.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.chel.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.chel.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.chelny.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.chelny.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.ekat.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.ekat.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.irkutsk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.irkutsk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.izhevsk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.izhevsk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.kirov.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.kirov.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.krsk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.krsk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.kurgan.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.kurgan.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.kursk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.kursk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.kzn.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.kzn.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.lipetsk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.lipetsk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.mgn.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.mgn.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.nk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.nk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.nn.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.nn.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.nsk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.nsk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.omsk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.omsk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.oren.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.oren.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.penza.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.penza.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.perm.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.perm.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.rostov.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.rostov.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.ryazan.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.ryazan.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.samara.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.samara.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.saratov.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.saratov.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.spb.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.spb.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.tmn.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.tmn.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.tomsk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.tomsk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.tula.ertelecom.ru | Iperf3 | Tcp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| st.tula.ertelecom.ru | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| st.tver.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.tver.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.ufa.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.ufa.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.ulsk.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.ulsk.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.volgograd.ertelecom.ru | Iperf3 | Tcp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| st.volgograd.ertelecom.ru | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| st.voronezh.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.voronezh.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.yar.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.yar.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| st.yola.ertelecom.ru | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| st.yola.ertelecom.ru | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| tumst.st.mtsws.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| tumst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| vldvst.st.mtsws.net | Iperf3 | Tcp | Измерение не подтверждено — подробности в журнале | NetworkError | NetworkError |
| vldvst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | NetworkError | ServerBusy |
| volst.st.mtsws.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| volst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| voronezh-speedtest.corbina.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| voronezh-speedtest.corbina.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | Timeout | Timeout |
| yarst.st.mtsws.net | Iperf3 | Tcp | Измерение не подтверждено — подробности в журнале | NetworkError | NetworkError |
| yarst.st.mtsws.net | Iperf3 | Udp | Измерение не подтверждено — подробности в журнале | NetworkError | NetworkError |
| yktst.st.mtsws.net | Iperf3 | Tcp | Загрузка и отдача подтверждены | Measured | Measured |
| yktst.st.mtsws.net | Iperf3 | Udp | Загрузка и отдача подтверждены | Measured | Measured |
| speedtest.selectel.ru | Http | Tcp | Подтверждена загрузка на ПК; отдача не подтверждена | Measured | У точки нет отдачи |
