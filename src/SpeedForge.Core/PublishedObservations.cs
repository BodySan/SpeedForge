using System.Text.Json;

namespace SpeedForge.Core;

public sealed record PublishedObservation(string ProfileId, TransportKind Transport, DateTimeOffset Checked, DirectionVerdict Verdict,
    DirectionVerdict? RepeatVerdict, RunStatus? DownloadStatus, RunStatus? UploadStatus, string? DownloadError, string? UploadError, bool FileOnly);

public static class PublishedObservations
{
    private static readonly Lazy<IReadOnlyList<PublishedObservation>> Snapshot = new(() =>
    {
        using var resource = typeof(PublishedObservations).Assembly.GetManifestResourceStream("SpeedForge.Core.server-observations.json");
        return resource == null ? [] : JsonSerializer.Deserialize<List<PublishedObservation>>(resource, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    });
    public static IReadOnlyList<PublishedObservation> All => Snapshot.Value;
    public static string Describe(string profileId, TransportKind transport)
    {
        var item = All.FirstOrDefault(x => x.ProfileId == profileId && x.Transport == transport);
        if (item == null) return "Не проверено";
        string label = item.Verdict switch
        {
            DirectionVerdict.Both => "Оба направления подтверждены",
            DirectionVerdict.DownloadObserved when item.FileOnly => "Загрузка · HTTP-файл",
            DirectionVerdict.DownloadObserved => "Наблюдалась загрузка; отдача не подтверждена",
            DirectionVerdict.UploadObserved => "Наблюдалась отдача; загрузка не подтверждена",
            _ => "Измерение не подтверждено"
        };
        if (item.RepeatVerdict == DirectionVerdict.Unconfirmed && item.Verdict != DirectionVerdict.Unconfirmed)
            label += "; повтор не подтвердился";
        return $"{label} ({item.Checked:dd.MM})";
    }
}
