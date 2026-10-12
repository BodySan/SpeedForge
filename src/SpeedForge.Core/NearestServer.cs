using System.Text.Json;

namespace SpeedForge.Core;

public sealed record CityLocation(string City, double Latitude, double Longitude, long GeoNamesId);
public sealed record NearbyServer(ServerEntry Server, double Kilometres);
public static class NearestServer
{
    private static readonly Lazy<CityLocation[]> Cities = new(() =>
    {
        using var stream = typeof(NearestServer).Assembly.GetManifestResourceStream("SpeedForge.Core.city-locations.json")!;
        return JsonSerializer.Deserialize<CityLocation[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    });
    public static IReadOnlyList<CityLocation> Locations => Cities.Value;
    public static NearbyServer? Choose(IEnumerable<ServerEntry> servers, GeoPoint position)
    {
        if (!position.Valid) return null;
        return servers.Select(server => (Server: server, City: Cities.Value.FirstOrDefault(x => x.City == server.City)))
            .Where(x => x.City != null).Select(x => new NearbyServer(x.Server, Distance(position, new(x.City!.Latitude, x.City.Longitude))))
            .OrderBy(x => x.Kilometres)
            .ThenByDescending(x => PublishedObservations.All.Any(y => y.ProfileId == x.Server.Id && y.Verdict == DirectionVerdict.Both))
            .ThenBy(x => x.Server.Engine == EngineKind.Http && x.Server.Http?.UploadUrl != null ? x.Server.Http.DownloadUrl.StartsWith("https:") ? 0 : 1 : 2)
            .ThenBy(x => x.Server.Id, StringComparer.Ordinal).FirstOrDefault();
    }
    public static double Distance(GeoPoint a, GeoPoint b)
    {
        if (!a.Valid || !b.Valid) throw new ArgumentException("Некорректные координаты");
        const double rad = Math.PI / 180;
        double h = Math.Pow(Math.Sin((a.Latitude-b.Latitude)*rad/2),2)
            + Math.Cos(a.Latitude*rad)*Math.Cos(b.Latitude*rad)*Math.Pow(Math.Sin((a.Longitude-b.Longitude)*rad/2),2);
        return 6371.0088 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(h,0,1)));
    }
}
