using System.Numerics;
using DS3Minimap.Core;
using Microsoft.Data.Sqlite;

namespace DS3Minimap.MapBaker;

internal static class TrailCoverage
{
    internal static void Report(MapPackage package, string database)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly };
        using var connection = new SqliteConnection(connectionString.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT map_id, x, y, z FROM trail_points ORDER BY session_id, sequence";
        using var reader = command.ExecuteReader();
        var regions = package.Regions.ToDictionary(region => region.MapId);
        var counts = new SortedDictionary<string, (int Aligned, int Total)>();
        while (reader.Read())
        {
            var mapId = reader.GetString(0);
            var count = counts.GetValueOrDefault(mapId);
            var point = new Vector3(reader.GetFloat(1), reader.GetFloat(2), reader.GetFloat(3));
            if (regions.TryGetValue(mapId, out var region))
            {
                var closest = LayerSelector.Rank(region, point).FirstOrDefault();
                if (closest is { HorizontalDistance: <= 1, VerticalDistance: <= 1.5f }) count.Aligned++;
            }
            counts[mapId] = (count.Aligned, count.Total + 1);
        }
        Console.WriteLine("Recorded positions within 1 horizontal and 1.5 vertical world units of the best matching floor:");
        foreach (var (map, count) in counts)
            Console.WriteLine($"  {map}: {count.Aligned}/{count.Total} ({(double)count.Aligned / count.Total:P1})");
    }
}
