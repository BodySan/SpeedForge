using SpeedForge.Core;
using System.Buffers.Binary;
using System.Net;
using System.Text.Json;

internal static class GeoIpTests
{
    internal static void Register(Action<string,Action> test)
    {
        void Require(bool value) { if (!value) throw new Exception("GeoIP assertion failed"); }
        test("STUN: XOR-MAPPED-ADDRESS и transaction ID", () =>
        {
            byte[] transaction = Enumerable.Range(1,12).Select(x => (byte)x).ToArray();
            var packet = Packet("8.8.8.8",transaction);
            Require(NetworkIdentityClient.ParseStun(packet,transaction)?.ToString() == "8.8.8.8");
            Require(NetworkIdentityClient.ParseStun(packet,new byte[12]) == null);
            Require(NetworkIdentityClient.ParseStun(Packet("10.0.0.1",transaction),transaction) == null);
            packet[23] = 40; Require(NetworkIdentityClient.ParseStun(packet,transaction) == null);
        });
        test("GeoIP: координаты, ASN и публичный IP", () =>
        {
            using var json = JsonDocument.Parse("""{"ip":"8.8.8.8","success":true,"city":"Moscow","latitude":55.75,"longitude":37.61,"connection":{"asn":15169,"org":"Example"}}""");
            var identity = NetworkIdentityClient.ParseGeo(json.RootElement,"8.8.8.8");
            Require(identity.Location?.Valid == true && identity.Asn == 15169 && identity.ExternalIp == "8.8.8.8");
            Require(NetworkIdentityClient.ParseGeo(json.RootElement,"1.1.1.1").Location == null);
        });
        test("GeoIP: некорректные значения не дают ложных координат", () =>
        {
            foreach(string body in new[] { "{\"success\":false}", "{\"latitude\":\"bad\",\"longitude\":1}", "{\"latitude\":999,\"longitude\":37}", "[]" })
            { using var json = JsonDocument.Parse(body); Require(NetworkIdentityClient.ParseGeo(json.RootElement,"8.8.8.8").Location == null); }
        });
        test("Ближайший сервер: Москва, Краснодар и Владивосток", () =>
        {
            var catalog = ServerCatalog.LoadPublished();
            foreach(var city in new[] { "Москва","Краснодар","Владивосток" })
            {
                var location = NearestServer.Locations.Single(x => x.City == city);
                Require(NearestServer.Choose(catalog,new(location.Latitude,location.Longitude))?.Server.City == city);
            }
            Require(NearestServer.Choose(catalog,new(double.NaN,1)) == null);
            Require(NearestServer.Choose(catalog.Where(x => x.City == "Россия"),new(55,37)) == null);
            Require(NearestServer.Distance(new(0,0),new(0,180)) is > 20000 and < 20100);
        });
        test("Координаты каталога: 48 городских записей и атрибуция GeoNames", () =>
        { Require(NearestServer.Locations.Count == 48 && NearestServer.Locations.All(x => new GeoPoint(x.Latitude,x.Longitude).Valid && x.GeoNamesId > 0)); });
        test("Внешний IP: STUN-кворум не подменяется другим IP HTTPS", () =>
        {
            using var handler = new FakeHttp(_ => "{\"ip_addr\":\"1.1.1.1\"}");
            using var client = new NetworkIdentityClient(handler,(_,_) => Task.FromResult<IPAddress?>(IPAddress.Parse("8.8.8.8")));
            var result = client.GetAsync(false,CancellationToken.None).GetAwaiter().GetResult();
            Require(result.ExternalIp == "8.8.8.8" && result.HttpIp == "1.1.1.1" && result.Source == "STUN");
            Require(handler.Urls.All(x => !x.Contains("ipwho") && !x.Contains("ripe")));
        });
        test("Внешний IP: резервный HTTPS при недоступном STUN", () =>
        {
            using var handler = new FakeHttp(url => url.Contains("ifconfig") ? "{\"ip_addr\":\"192.168.1.1\"}" : "{\"ip\":\"8.8.8.8\"}");
            using var client = new NetworkIdentityClient(handler,(_,_) => Task.FromResult<IPAddress?>(null));
            var result = client.GetAsync(false,CancellationToken.None).GetAwaiter().GetResult();
            Require(result.ExternalIp == "8.8.8.8" && result.Source == "HTTPS" && handler.Urls.Count == 2);
        });
        test("GeoIP: RIPE ASN и кэш 15 минут для выбранного внешнего IP", () =>
        {
            using var handler = new FakeHttp(url => url.Contains("ifconfig") ? "{\"ip_addr\":\"8.8.8.8\"}"
                : url.Contains("ipwho") ? "{\"ip\":\"8.8.8.8\",\"city\":\"Moscow\",\"latitude\":55.75,\"longitude\":37.61,\"connection\":{\"asn\":1}}"
                : "{\"data\":{\"asns\":[15169]}}");
            using var client = new NetworkIdentityClient(handler,(_,_) => Task.FromResult<IPAddress?>(null));
            var first = client.GetAsync(true,CancellationToken.None).GetAwaiter().GetResult();
            var second = client.GetAsync(true,CancellationToken.None).GetAwaiter().GetResult();
            Require(first.Asn == 15169 && second.Location?.Valid == true && handler.Urls.Count(x => x.Contains("ipwho")) == 1);
            Require(handler.Urls.Any(x => x.Contains("resource=8.8.8.8")));
        });
        test("GeoIP: большой или ошибочный ответ сохраняет неизвестность", () =>
        {
            using var handler = new FakeHttp(_ => new string('x',70000));
            using var client = new NetworkIdentityClient(handler,(_,_) => Task.FromResult<IPAddress?>(null));
            Require(client.GetAsync(true,CancellationToken.None).GetAwaiter().GetResult().ExternalIp == null);
        });
    }
    private static byte[] Packet(string ip, byte[] transaction)
    {
        byte[] packet = new byte[32]; packet[0] = 1; packet[1] = 1; packet[3] = 12;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4),0x2112A442); transaction.CopyTo(packet,8);
        packet[21] = 0x20; packet[23] = 8; packet[25] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28),BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(ip).GetAddressBytes()) ^ 0x2112A442);
        return packet;
    }
    private sealed class FakeHttp(Func<string,string> response) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri; Urls.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(url),System.Text.Encoding.UTF8,"application/json") });
        }
    }
}
