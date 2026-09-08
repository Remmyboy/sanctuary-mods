# Post-game economy graphs from `.sanreplay` files

Findings from 2026-09-07. Nothing here is built into a mod yet; the
prototype extractor is reproduced in full at the bottom so it survives.

## The short version

The game's replay **does** contain every player's economy for the whole
match, at 10 Hz. Every simulation tick the host loops over `Economies` and
sends `UpdateEconomyTotals(armyId, totals)` to every client
(`host/hostMain.lua`, inside `OnSimulationTickUpdate`). The replay is the
host-to-client packet stream written verbatim, so those commands are in the
file. ReplayManager's per-army economy panel is already reading them during
playback; the news is that they can be read **offline**, without running the
game, in well under a second per replay.

Same stream, same technique, also available: `CreateUnit` / `DestroyUnit` /
`DeleteUnit` (carry `armyID`, so unit counts and losses per player),
`ExecuteClientFunction` with `InitClient` (the lobby roster: names to army
ids), and chat.

## Where the files are

`%USERPROFILE%\AppData\LocalLow\Enhearten Media PTY\Sanctuary\Replays\*.sanreplay`
(note: `Sanctuary`, not `Sanctuary Shattered Sun` as the README's
ReplayManager section says). The recording client writes the file during
the match (`ReplayFile.RecordFrame` on every host packet, flushed each
time), so at game end the file is complete on disk. Only the client that
records has it; every client records its own.

## File format (game format version 1)

All integers little-endian. "brotli(x)" is a raw Brotli stream with no
length prefix; consecutive streams are decoded one after another with
`BrotliDecoder.Decompress` until `OperationStatus.Done`, which reports the
bytes consumed (`NetworkUtils.DecompressData` does exactly this).

```
int32 headerLen
header:
  string  gameName      = int32 utf8Len, brotli(bytes)   "Sanctuary Shattered Sun"
  int32   formatVersion = brotli(4 bytes)                1
  string  gameVersion   = e.g. "0.0.1.15#<lua hash>"
  string  mapPath       = e.g. "Maps/The_Forge/The_Forge.sanmap"
  byte    recordingClientID = brotli(1 byte)
frames, until EOF:
  byte  kind = 100 (HostData)
  int32 len
  payload[len]:
    int32 simTick        = brotli(4 bytes)
    int32 typeCount                       (raw)
    int32 dataCount                       (raw)
    bytes types[typeCount]  = brotli(...) one HostOrderedCommandType byte per command
    bytes data[dataCount]   = brotli(...) the commands' data, concatenated
    uint4 hash           = brotli(16 bytes)
```

Source: `EM.Network.Replay.ReplayFile`, `ReplayClientSockets.TryReadFrame`,
`HostToClientCommunicatorDataSingleton.SerializeNetworkData`,
`NetworkUtils.Pack/Deserialize` in `Trebuchet.dll` (decompile with
`ilspycmd`, which is installed as a dotnet tool).

One frame per tick, ten ticks per second. Frame count = game length.

## The command stream

`data` is a `NativeCommandQueue`: no per-command length, each type has an
implicit size and the reader (`ClientEngine.GetNextHostCommand`, a big
switch) knows it. Walking the stream exactly means mirroring that switch
for all ~67 `HostOrderedCommandType` values. **Not needed** for economy:
the custom (Lua) commands have a header with their length, and the economy
payload has an unmistakable signature, so the prototype just scans `data`
for it.

Custom command layout in `data` (`HostOrderedCommandType.CustomCommand` = 66):

```
ushort customCommandType   -- registry id, see below
2 bytes padding            -- CustomCommandData is {ushort; int} = 8 bytes
int32  commandLength
bytes  payload[commandLength]   -- Lua BinarySerialization of the command layout
```

`CustomCommandSingle` (67) is the same behind a `clientID` field.

**Command ids** are assigned in `common/commands/registry.lua`: every
HostToClient command name sorted (byte order), numbered from 0, unless a
command forces an id (none do today). `UpdateEconomyTotals` is currently
**53**. Any added or renamed command shifts it, so match on the payload,
not the id.

**Payload** (`common/commands/definitions/economy.lua`, encoded by
`common/commands/binarySerialization.lua`; floats are 4-byte IEEE, strings
are int32 length + bytes, dictionaries are int32 count + key/value pairs,
order from Lua `pairs` so `alloys`/`energy` may come either way round):

```
int32 armyId
int32 2                              -- dictionary count
  string "alloys"  then 8 floats
  string "energy"  then 8 floats
```

The eight floats, in layout order: `current, storage, satisfaction,
income, harvest, outcome, request, balance`. Income/outcome are per tick;
balance looks like per second. `commandLength` is therefore always 92.
Signature to scan for: `02 00 00 00` followed by `06 00 00 00 "alloys"` or
`06 00 00 00 "energy"`, with the army id 4 bytes before and the 8-byte
command header before that.

## What came out

Tropical 1v1, 2026-09-06 11:56, recorded by client 1:

| | |
| --- | --- |
| Frames / ticks | 17,690 (29.5 min) |
| Economy records | 35,380 = 2 armies x every tick, 0 malformed |
| Command id seen | 53 only |
| Parse time | 0.6 s (.NET 8, Debug) |

FFA maps record all eight seats, including empty ones (storage 0 for the
whole game): filter on the roster or on non-zero storage.

## How to turn it into graphs

Recommended: at game end LadderReporter runs the extractor on the file the
game just wrote, downsamples to one point per second per army (~1,800
points x 16 fields for a 30-minute game), and posts the series alongside
the result. The site renders the graphs on the match page. No game hooks,
no watching the replay back. Watching it back at 16x and sampling
ReplayManager's hook would also work but costs minutes per game.

Series worth plotting first: alloy and energy income, spend (`outcome`),
stall (`request - outcome`), and stored amount; later unit count and
losses from the unit events.

Caveats: a game update that changes the totals layout needs a matching
change here (the manifest hash in `gameVersion` after `#` is a cheap
"format may have changed" signal); the extractor should refuse a frame
whose decoded sizes disagree with `typeCount`/`dataCount` rather than
guess. The mods target net472 where `BrotliDecoder` is not in the
reference assemblies; ReplayManager's archived `ReplayFile.cs` shows how to
reach the game's `System.IO.Compression` Brotli by reflection.

## Prototype extractor

.NET 8 console app: `dotnet new console`, replace `Program.cs` with this.
Usage: `dotnet run -- <replay.sanreplay> [out.csv]`. Verified on the
replays above.

```csharp
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// Offline reader for Sanctuary .sanreplay files: pulls every army's
// UpdateEconomyTotals out of the host-to-client stream, one per tick.
static class Program
{
    static byte[] Brotli(byte[] src, ref int off)
    {
        using var dec = new BrotliDecoder();
        var dst = new byte[256];
        int written = 0;
        while (true)
        {
            var status = dec.Decompress(src.AsSpan(off), dst.AsSpan(written), out int consumed, out int w);
            off += consumed; written += w;
            if (status == OperationStatus.Done) { Array.Resize(ref dst, written); return dst; }
            if (status == OperationStatus.DestinationTooSmall) { Array.Resize(ref dst, dst.Length * 2); continue; }
            throw new InvalidDataException("brotli " + status + " at " + off);
        }
    }
    static int I32(byte[] b, int o) => BitConverter.ToInt32(b, o);
    static string PackedString(byte[] b, ref int off)
    {
        int len = I32(b, off); off += 4;
        var raw = Brotli(b, ref off);
        if (raw.Length != len) throw new InvalidDataException("string len");
        return Encoding.UTF8.GetString(raw);
    }

    static void Main(string[] args)
    {
        var path = args[0];
        var all = File.ReadAllBytes(path);
        int p = 0;
        int hlen = I32(all, p); p += 4;
        var h = all.AsSpan(p, hlen).ToArray(); p += hlen;
        int ho = 0;
        var gameName = PackedString(h, ref ho);
        var fv = I32(Brotli(h, ref ho), 0);
        var gameVersion = PackedString(h, ref ho);
        var mapPath = PackedString(h, ref ho);
        var rec = Brotli(h, ref ho)[0];
        Console.Error.WriteLine($"{gameName} format {fv} version {gameVersion} map {mapPath} recorder {rec}");

        var alloysKey = new byte[] { 6, 0, 0, 0, (byte)'a', (byte)'l', (byte)'l', (byte)'o', (byte)'y', (byte)'s' };
        var energyKey = new byte[] { 6, 0, 0, 0, (byte)'e', (byte)'n', (byte)'e', (byte)'r', (byte)'g', (byte)'y' };
        var fields = new[] { "current", "storage", "satisfaction", "income", "harvest", "outcome", "request", "balance" };
        var sb = new StringBuilder();
        sb.Append("tick,army");
        foreach (var r in new[] { "a", "e" }) foreach (var f in fields) sb.Append(',').Append(r).Append('_').Append(f);
        sb.Append('\n');

        int frames = 0, hits = 0, badLen = 0; var types = new HashSet<int>(); var armies = new SortedSet<int>();
        int firstTick = -1, lastTick = -1; var perTick = new Dictionary<int, int>();
        long dataBytes = 0;
        while (p + 5 <= all.Length)
        {
            byte kind = all[p]; int len = I32(all, p + 1); p += 5;
            if (kind != 100 || len < 0 || p + len > all.Length) { Console.Error.WriteLine($"stopping at frame {frames}: kind {kind} len {len}"); break; }
            var f = all.AsSpan(p, len).ToArray(); p += len;
            frames++;
            int o = 0;
            int tick = I32(Brotli(f, ref o), 0);
            int typeCount = I32(f, o); o += 4;
            int dataCount = I32(f, o); o += 4;
            var ty = Brotli(f, ref o);
            var data = Brotli(f, ref o);
            var hash = Brotli(f, ref o);
            if (ty.Length != typeCount || data.Length != dataCount || hash.Length != 16 || o != f.Length)
                throw new InvalidDataException($"frame {frames} tick {tick}: sizes {ty.Length}/{typeCount} {data.Length}/{dataCount} {hash.Length} end {o}/{f.Length}");
            dataBytes += data.Length;
            if (firstTick < 0) firstTick = tick; lastTick = tick;

            // Scan for the totals dictionary: int(2) then a key entry.
            int found = 0;
            for (int i = 4; i + 10 <= data.Length; i++)
            {
                if (I32(data, i - 4) != 2) continue;
                bool a = Match(data, i, alloysKey), e = !a && Match(data, i, energyKey);
                if (!a && !e) continue;
                int dictStart = i - 4;
                int armyOff = dictStart - 4;
                int hdr = armyOff - 8;
                if (hdr < 0) continue;
                int ctype = BitConverter.ToUInt16(data, hdr);
                int clen = I32(data, hdr + 4);
                int army = I32(data, armyOff);
                // payload: armyId(4) count(4) 2*(4+6+32)
                if (clen != 4 + 4 + 2 * 42) { badLen++; continue; }
                var vals = new Dictionary<string, float[]>();
                int q = dictStart + 4;
                for (int k = 0; k < 2; k++)
                {
                    int sl = I32(data, q); q += 4;
                    var key = Encoding.ASCII.GetString(data, q, sl); q += sl;
                    var v = new float[8];
                    for (int m = 0; m < 8; m++) { v[m] = BitConverter.ToSingle(data, q); q += 4; }
                    vals[key] = v;
                }
                if (!vals.ContainsKey("alloys") || !vals.ContainsKey("energy")) continue;
                types.Add(ctype); armies.Add(army); hits++; found++;
                sb.Append(tick).Append(',').Append(army);
                foreach (var r in new[] { "alloys", "energy" }) foreach (var x in vals[r]) sb.Append(',').Append(x.ToString("R", CultureInfo.InvariantCulture));
                sb.Append('\n');
                i = q - 1;
            }
            perTick[found] = perTick.GetValueOrDefault(found) + 1;
        }
        File.WriteAllText(args.Length > 1 ? args[1] : Path.ChangeExtension(path, ".eco.csv"), sb.ToString());
        Console.Error.WriteLine($"frames {frames} ticks {firstTick}..{lastTick} ({(lastTick - firstTick + 1) / 10.0 / 60.0:F1} min) data {dataBytes / 1024 / 1024} MB uncompressed");
        Console.Error.WriteLine($"economy records {hits}; command type ids {{{string.Join(",", types)}}}; armies {{{string.Join(",", armies)}}}; bad lengths {badLen}");
        Console.Error.WriteLine("records per frame: " + string.Join(" ", perTick.Select(kv => $"{kv.Key}x{kv.Value}")));
    }
    static bool Match(byte[] d, int at, byte[] pat)
    {
        if (at + pat.Length > d.Length) return false;
        for (int i = 0; i < pat.Length; i++) if (d[at + i] != pat[i]) return false;
        return true;
    }
}
```
