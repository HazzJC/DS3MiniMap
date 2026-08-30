using System.Numerics;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DS3Minimap.Core;

public sealed record TrailPoint(
    long Sequence,
    DateTimeOffset Timestamp,
    Vector3 Position,
    string MapId,
    string LayerId,
    int Segment);

public sealed class TrailRecorder
{
    private readonly List<TrailPoint> _points = [];
    private int _segment;
    private long _sequence;

    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public float MinimumDistance { get; init; } = 1.25f;
    public float TeleportDistance { get; init; } = 75f;
    public int MaximumPoints { get; init; } = 20_000;
    public IReadOnlyList<TrailPoint> Points => _points;

    public TrailPoint? Observe(PlayerTelemetry telemetry, string layerId)
    {
        var previous = _points.LastOrDefault();
        if (previous is not null)
        {
            var elapsed = telemetry.Timestamp - previous.Timestamp;
            var distance = Vector3.Distance(previous.Position, telemetry.Position);
            var contextChanged = previous.MapId != telemetry.MapId || previous.LayerId != layerId;
            if (contextChanged || distance >= TeleportDistance) _segment++;
            else if (elapsed < MinimumInterval || distance < MinimumDistance) return null;
        }

        var point = new TrailPoint(++_sequence, telemetry.Timestamp, telemetry.Position, telemetry.MapId, layerId, _segment);
        _points.Add(point);
        if (_points.Count > MaximumPoints) _points.RemoveRange(0, _points.Count - MaximumPoints);
        return point;
    }

    public void BreakSegment() => _segment++;

    public void Clear()
    {
        _points.Clear();
        _segment++;
    }
}

public sealed class TrailStore(string databasePath)
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS trail_points (
                session_id TEXT NOT NULL,
                character_name TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                timestamp TEXT NOT NULL,
                x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL,
                map_id TEXT NOT NULL, layer_id TEXT NOT NULL, segment INTEGER NOT NULL,
                PRIMARY KEY(session_id, sequence)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAsync(string sessionId, string characterName, TrailPoint point, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO trail_points
            (session_id, character_name, sequence, timestamp, x, y, z, map_id, layer_id, segment)
            VALUES ($session, $character, $sequence, $timestamp, $x, $y, $z, $map, $layer, $segment)
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$character", characterName);
        command.Parameters.AddWithValue("$sequence", point.Sequence);
        command.Parameters.AddWithValue("$timestamp", point.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$x", point.Position.X);
        command.Parameters.AddWithValue("$y", point.Position.Y);
        command.Parameters.AddWithValue("$z", point.Position.Z);
        command.Parameters.AddWithValue("$map", point.MapId);
        command.Parameters.AddWithValue("$layer", point.LayerId);
        command.Parameters.AddWithValue("$segment", point.Segment);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public static class TrailExporter
{
    public static async Task ExportJsonAsync(IEnumerable<TrailPoint> points, string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, points, cancellationToken: cancellationToken);
    }

    public static async Task ExportCsvAsync(IEnumerable<TrailPoint> points, string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var stream = new StreamWriter(path, false, new UTF8Encoding(false));
        await stream.WriteLineAsync("sequence,timestamp,x,y,z,mapId,layerId,segment".AsMemory(), cancellationToken);
        foreach (var point in points)
        {
            var line = string.Join(',', point.Sequence, point.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                point.Position.X.ToString(CultureInfo.InvariantCulture), point.Position.Y.ToString(CultureInfo.InvariantCulture),
                point.Position.Z.ToString(CultureInfo.InvariantCulture), Escape(point.MapId), Escape(point.LayerId), point.Segment);
            await stream.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    private static string Escape(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
