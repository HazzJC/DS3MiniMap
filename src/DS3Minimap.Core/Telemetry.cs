using System.Numerics;

namespace DS3Minimap.Core;

public sealed record PlayerTelemetry(
    DateTimeOffset Timestamp,
    Vector3 Position,
    float Heading,
    string MapId,
    string CharacterName,
    bool IsValid = true);

public enum TelemetryState
{
    GameNotRunning,
    Attaching,
    UnsupportedVersion,
    WaitingForPlayer,
    Active,
    AccessDenied,
    InvalidData,
    Faulted
}

public sealed record TelemetrySnapshot(
    TelemetryState State,
    PlayerTelemetry? Player,
    string Diagnostic,
    string? GameVersion = null);

public interface IGameTelemetrySource : IAsyncDisposable
{
    ValueTask<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed record EventFlagSnapshot(
    bool Available,
    IReadOnlySet<int> SetFlags,
    string Diagnostic);

/// <summary>
/// Optional read-only access to a selected set of game event flags. Keeping this
/// separate from 30 Hz player telemetry lets callers poll pickup state slowly.
/// </summary>
public interface IEventFlagSource
{
    ValueTask<EventFlagSnapshot> ReadEventFlagsAsync(
        IReadOnlyCollection<int> eventFlagIds,
        CancellationToken cancellationToken = default);
}
