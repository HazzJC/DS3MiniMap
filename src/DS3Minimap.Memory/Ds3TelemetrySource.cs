using System.Diagnostics;
using System.ComponentModel;
using System.Numerics;
using System.Text;
using DS3Minimap.Core;
using Microsoft.Win32.SafeHandles;

namespace DS3Minimap.Memory;

public sealed class Ds3TelemetrySource(Ds3Layout? layout = null) : IGameTelemetrySource, IEventFlagSource
{
    private readonly Ds3Layout _layout = layout ?? Ds3Layout.Version1152;
    private Process? _process;
    private SafeProcessHandle? _handle;
    private MemoryReader? _memory;
    private nint _worldChrManGlobal;
    private nint _gameDataManGlobal;
    private nint _eventFlagManGlobal;
    private nint _fieldAreaGlobal;
    private string? _version;

    public ValueTask DisposeAsync()
    {
        Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!IsAttached()) return ValueTask.FromResult(Attach());
            return ValueTask.FromResult(ReadPlayer());
        }
        catch (UnauthorizedAccessException exception)
        {
            Reset();
            return ValueTask.FromResult(new TelemetrySnapshot(TelemetryState.AccessDenied, null, exception.Message, _version));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            Reset();
            return ValueTask.FromResult(new TelemetrySnapshot(TelemetryState.AccessDenied, null, "Windows denied read-only process access. Run DS3 and the minimap at the same privilege level.", _version));
        }
        catch (Win32Exception)
        {
            Reset();
            return ValueTask.FromResult(new TelemetrySnapshot(TelemetryState.GameNotRunning, null, "The game process closed; waiting for DarkSoulsIII.exe.", _version));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Reset();
            return ValueTask.FromResult(new TelemetrySnapshot(TelemetryState.InvalidData, null, exception.Message, _version));
        }
        catch (Exception exception)
        {
            Reset();
            return ValueTask.FromResult(new TelemetrySnapshot(TelemetryState.Faulted, null, $"Telemetry paused: {exception.Message}", _version));
        }
    }

    public ValueTask<EventFlagSnapshot> ReadEventFlagsAsync(IReadOnlyCollection<int> eventFlagIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (eventFlagIds.Count == 0)
            return ValueTask.FromResult(new EventFlagSnapshot(true, new HashSet<int>(), "No pickup flags are required for this map."));
        if (!IsAttached() || _memory is null || _eventFlagManGlobal == 0)
            return ValueTask.FromResult(new EventFlagSnapshot(false, new HashSet<int>(), "Event-flag telemetry is not attached."));

        try
        {
            var setFlags = new HashSet<int>();
            var requested = eventFlagIds.Where(id => id > 0).Distinct()
                .Select(id => (Id: id, Bit: Ds3EventFlagBit.Decode(id))).ToArray();
            var memory = _memory;
            if (memory is null) throw new InvalidOperationException("Process memory is unavailable.");
            var manager = memory.ReadPointer(_eventFlagManGlobal);
            if (manager == 0 || memory.ReadByte(manager + 0x228) == 0)
                return ValueTask.FromResult(new EventFlagSnapshot(false, new HashSet<int>(), "DS3 event flags are not ready."));
            var directory = memory.ReadPointer(manager + 0x218);
            if (directory == 0)
                return ValueTask.FromResult(new EventFlagSnapshot(false, new HashSet<int>(), "DS3 event-flag storage is unavailable."));

            var categories = new Dictionary<(int Area, int Block), int>();
            var storage = new Dictionary<(int Major, int Category, int Thousands), nint>();
            var words = new Dictionary<(nint Block, int Offset), uint>();
            foreach (var item in requested)
            {
                var categoryKey = (item.Bit.Area, item.Bit.AreaBlock);
                if (!categories.TryGetValue(categoryKey, out var category))
                {
                    category = ResolveFlagCategory(categoryKey.Area, categoryKey.AreaBlock);
                    categories.Add(categoryKey, category);
                }
                if (category < 0)
                    return ValueTask.FromResult(new EventFlagSnapshot(false, new HashSet<int>(), $"Event flag {item.Id} could not be resolved safely."));

                var storageKey = (item.Bit.MajorGroup, category, item.Bit.ThousandsGroup);
                if (!storage.TryGetValue(storageKey, out var block))
                {
                    var group = memory.ReadPointer(directory + item.Bit.MajorGroup * 0x18);
                    block = group == 0 ? 0 : memory.ReadPointer(group + category * 0xA8 + item.Bit.ThousandsGroup * 0x10);
                    storage.Add(storageKey, block);
                }
                if (block == 0)
                    return ValueTask.FromResult(new EventFlagSnapshot(false, new HashSet<int>(), $"Event flag {item.Id} storage is unavailable."));
                var wordKey = (block, item.Bit.WordOffset);
                if (!words.TryGetValue(wordKey, out var word))
                {
                    word = memory.ReadUInt32(block + item.Bit.WordOffset);
                    words.Add(wordKey, word);
                }
                if ((word & item.Bit.Mask) != 0) setFlags.Add(item.Id);
            }
            return ValueTask.FromResult(new EventFlagSnapshot(true, setFlags, $"Read {eventFlagIds.Count} pickup flags."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return ValueTask.FromResult(new EventFlagSnapshot(false, new HashSet<int>(), $"Event-flag telemetry paused: {exception.Message}"));
        }
    }

    private bool IsAttached() => _process is { HasExited: false } && _handle is { IsInvalid: false, IsClosed: false } && _memory is not null;

    private TelemetrySnapshot Attach()
    {
        Reset();
        _process = Process.GetProcessesByName("DarkSoulsIII").FirstOrDefault();
        if (_process is null) return new TelemetrySnapshot(TelemetryState.GameNotRunning, null, "Waiting for DarkSoulsIII.exe.");

        _version = _process.MainModule?.FileVersionInfo.FileVersion;
        if (!VersionMatches(_version, _layout.SupportedFileVersion))
            return new TelemetrySnapshot(TelemetryState.UnsupportedVersion, null, $"Unsupported game version {_version ?? "unknown"}; expected {_layout.SupportedFileVersion}.", _version);

        _handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessVmRead, false, (uint)_process.Id);
        NativeMethods.EnsureValid(_handle);
        _memory = new MemoryReader(_handle);

        var module = _process.MainModule ?? throw new InvalidOperationException("The game module could not be inspected.");
        // DS3 1.15.2 maps a roughly 87 MiB runtime image. It is not byte-identical
        // to the installed executable, so signatures must be resolved from the
        // loaded image rather than the on-disk file.
        var moduleBytes = _memory.ReadBytes(module.BaseAddress, module.ModuleMemorySize);
        _worldChrManGlobal = ResolveRipGlobal(moduleBytes, module.BaseAddress, _layout.WorldChrManPattern, _layout.WorldChrManDisplacementOffset, _layout.WorldChrManInstructionLength);
        _gameDataManGlobal = ResolveRipGlobal(moduleBytes, module.BaseAddress, _layout.GameDataManPattern, _layout.GameDataManDisplacementOffset, _layout.GameDataManInstructionLength);
        _eventFlagManGlobal = ResolveRipGlobal(moduleBytes, module.BaseAddress, _layout.EventFlagManPattern, _layout.EventFlagManDisplacementOffset, _layout.EventFlagManInstructionLength);
        _fieldAreaGlobal = ResolveRipGlobal(moduleBytes, module.BaseAddress, _layout.FieldAreaPattern, _layout.FieldAreaDisplacementOffset, _layout.FieldAreaInstructionLength);
        if (_worldChrManGlobal == 0 || _gameDataManGlobal == 0)
        {
            Reset();
            return new TelemetrySnapshot(TelemetryState.UnsupportedVersion, null, "The 1.15.2 memory signatures were not found.", _version);
        }
        return ReadPlayer();
    }

    private TelemetrySnapshot ReadPlayer()
    {
        var memory = _memory ?? throw new InvalidOperationException("Not attached.");
        var world = memory.ReadPointer(_worldChrManGlobal);
        if (world == 0) return new TelemetrySnapshot(TelemetryState.WaitingForPlayer, null, "Waiting for player world data.", _version);
        var player = memory.ReadPointer(world + 0x40);
        if (player == 0) return new TelemetrySnapshot(TelemetryState.WaitingForPlayer, null, "Waiting for player world data.", _version);
        var physics = memory.ReadPointer(player + 0x28);
        if (physics == 0)
            return new TelemetrySnapshot(TelemetryState.WaitingForPlayer, null, "Waiting for player world data.", _version);

        var position = new Vector3(memory.ReadSingle(physics + 0x80), memory.ReadSingle(physics + 0x84), memory.ReadSingle(physics + 0x88));
        var heading = memory.ReadSingle(physics + 0x74);
        if (!IsFinite(position) || !float.IsFinite(heading) || Math.Abs(heading) > MathF.PI * 8)
            return new TelemetrySnapshot(TelemetryState.InvalidData, null, "Player coordinates failed validation.", _version);

        var mapOwner = memory.ReadPointer(world + 0x80);
        if (mapOwner == 0) return new TelemetrySnapshot(TelemetryState.WaitingForPlayer, null, "Waiting for map data.", _version);
        var mapBytes = memory.ReadBytes(mapOwner + 0x1FE0, 4);
        var mapId = DecodeMapId(mapBytes);

        var gameData = memory.ReadPointer(_gameDataManGlobal);
        if (gameData == 0) return new TelemetrySnapshot(TelemetryState.WaitingForPlayer, null, "Waiting for character data.", _version);
        var character = memory.ReadPointer(gameData + 0x10);
        if (character == 0) return new TelemetrySnapshot(TelemetryState.WaitingForPlayer, null, "Waiting for character data.", _version);
        var nameBytes = memory.ReadBytes(character + 0x88, 48 * 2);
        var name = Encoding.Unicode.GetString(nameBytes).Split('\0')[0].Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "Unknown character";

        var telemetry = new PlayerTelemetry(DateTimeOffset.UtcNow, position, heading, mapId, name);
        return new TelemetrySnapshot(TelemetryState.Active, telemetry, "Read-only telemetry active.", _version);
    }

    private static nint ResolveRipGlobal(byte[] module, nint moduleBase, string signature, int displacementOffset, int instructionLength)
    {
        var match = SignatureScanner.Find(module, new BytePattern(signature));
        if (match < 0) return 0;
        var displacement = BitConverter.ToInt32(module, match + displacementOffset);
        return moduleBase + match + instructionLength + displacement;
    }

    private static bool VersionMatches(string? actual, string expected) =>
        actual is not null && (actual == expected || actual.StartsWith("1.15.2", StringComparison.Ordinal));

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && Math.Abs(value.X) < 1_000_000 && Math.Abs(value.Y) < 1_000_000 && Math.Abs(value.Z) < 1_000_000;

    public static string DecodeMapId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) throw new ArgumentException("A DS3 map identifier requires four bytes.", nameof(bytes));
        
        ReadOnlySpan<(int Area, int Block)> candidates = [(0, 1), (1, 0), (2, 3), (3, 2)];
        foreach (var candidate in candidates)
        {
            var area = bytes[candidate.Area];
            var block = bytes[candidate.Block];
            var id = $"m{area:D2}_{block:D2}";
            if (MapCatalog.Maps.Any(map => map.Id == id)) return id;
        }
        return $"unknown-{Convert.ToHexString(bytes)}";
    }

    private int ResolveFlagCategory(int area, int areaBlock)
    {
        if (area >= 90 || area + areaBlock == 0) return 0;
        var memory = _memory;
        if (memory is null || _fieldAreaGlobal == 0) return -1;
        var fieldArea = memory.ReadPointer(_fieldAreaGlobal);
        if (fieldArea == 0) return -1;
        var worldInfoOwner = memory.ReadPointer(fieldArea + 0x10);
        if (worldInfoOwner == 0) return -1;
        var count = memory.ReadInt32(worldInfoOwner + 0x8);
        if (count is < 0 or > 256) return -1;
        var worldInfos = memory.ReadPointer(worldInfoOwner + 0x10);
        if (worldInfos == 0) return -1;

        for (var worldIndex = 0; worldIndex < count; worldIndex++)
        {
            var worldInfo = worldInfos + worldIndex * 0x38;
            if (memory.ReadByte(worldInfo + 0x0B) != area) continue;
            var blockCount = memory.ReadByte(worldInfo + 0x20);
            var blocks = memory.ReadPointer(worldInfo + 0x28);
            if (blocks == 0) return -1;
            for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
            {
                var block = blocks + blockIndex * 0x70;
                var descriptor = memory.ReadInt32(block + 0x8);
                if (((descriptor >> 16) & 0xFF) != areaBlock || ((descriptor >> 24) & 0xFF) != area) continue;
                var category = memory.ReadInt32(block + 0x20);
                return category is >= 0 and < 1024 ? category + 1 : -1;
            }
        }
        return -1;
    }

    private void Reset()
    {
        _memory = null;
        _handle?.Dispose();
        _handle = null;
        _process?.Dispose();
        _process = null;
        _worldChrManGlobal = 0;
        _gameDataManGlobal = 0;
        _eventFlagManGlobal = 0;
        _fieldAreaGlobal = 0;
    }
}

public readonly record struct Ds3EventFlagBit(
    int MajorGroup, int Area, int AreaBlock, int ThousandsGroup, int WordOffset, uint Mask)
{
    public static Ds3EventFlagBit Decode(int eventFlagId)
    {
        if (eventFlagId <= 0) throw new ArgumentOutOfRangeException(nameof(eventFlagId));
        var remainder = eventFlagId % 1000;
        return new Ds3EventFlagBit(
            eventFlagId / 10_000_000 % 10,
            eventFlagId / 100_000 % 100,
            eventFlagId / 10_000 % 10,
            eventFlagId / 1_000 % 10,
            (remainder >> 5) * 4,
            1u << (31 - (remainder & 31)));
    }
}
