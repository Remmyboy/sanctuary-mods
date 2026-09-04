using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace SanctuaryHud.Replays
{
    internal sealed class ReplayPlayerInfo
    {
        public string Name;
        public string Faction;
        public int ArmyId;
        public int Team;
        public string Type;     // Player, AI, Observer
        public int ClientId;    // 255 when the slot never had a network client (AI)
    }

    internal sealed class ReplayHeader
    {
        public int Format = 2;
        public string GameVersion;
        public string LuaHash;
        public string Map;
        public int RecorderClientId;
        public string RecordedAt;    // local time, ISO 8601
        public int TickCount;        // 10 ticks per second; 0 while unfinished
        public List<ReplayPlayerInfo> Players = new List<ReplayPlayerInfo>();
    }

    internal struct ReplayFrame
    {
        public const byte KindDecoded = 0;   // hash(16) typeCount dataCount types data
        public const byte KindWire = 1;      // the packet exactly as it came off the socket

        public byte Kind;
        public int Tick;
        public byte[] Data;
    }

    // A replay is the host-to-client packet stream behind a small JSON header.
    //
    // Format 2 (current):
    //   "SANREP02"  int32 jsonLen  json  byte encoding  frames
    //   frame: byte kind  int32 simTick  int32 len  bytes
    // `encoding` is 0 while the file is an unfinished `.part` (frames raw, so
    // a crash loses at most the last couple of seconds) and 1 once finalised,
    // when everything after it is one Brotli stream. The game compresses each
    // packet field separately, which leaves nothing for a second compressor
    // to find; so frames are stored with those fields *decoded*, and one
    // long-window Brotli pass over the whole file then sees how alike
    // consecutive ticks are. That is about 2.7x smaller than gzip over the
    // wire bytes.
    //
    // Format 1 (still readable): "SANREP01" header then wire-format frames
    // with a sbyte message type in place of the kind, the whole file gzipped.
    // BrotliStream lives in the game's System.IO.Compression.dll but not in
    // the .NET Framework reference assembly this project compiles against
    // (and MSBuild picks the framework one by version), so it is created by
    // reflection and driven through its Stream base class. The enums it takes
    // exist in both.
    internal static class Brotli
    {
        private static Type _type;
        private static bool _resolved;

        private static Type Resolve()
        {
            if (_resolved) return _type;
            _resolved = true;
            _type = Type.GetType("System.IO.Compression.BrotliStream, System.IO.Compression")
                    ?? Type.GetType("System.IO.Compression.BrotliStream, System.IO.Compression.Brotli");   // modern .NET, for offline tools
            if (_type == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { _type = asm.GetType("System.IO.Compression.BrotliStream"); }
                    catch { }
                    if (_type != null) break;
                }
            }
            return _type;
        }

        public static bool Available => Resolve() != null;

        /// A compressing stream over `output`, which it leaves open.
        public static Stream Compress(Stream output, CompressionLevel level)
        {
            var t = Resolve() ?? throw new NotSupportedException("BrotliStream is not available in this game build");
            return (Stream)Activator.CreateInstance(t, output, level, true);
        }

        /// A decompressing stream over `input`.
        public static Stream Decompress(Stream input)
        {
            var t = Resolve() ?? throw new NotSupportedException("BrotliStream is not available in this game build");
            return (Stream)Activator.CreateInstance(t, input, CompressionMode.Decompress);
        }
    }

    internal static class ReplayFile
    {
        public const string Extension = ".sanrep";
        public const string PartSuffix = ".part";
        private static readonly byte[] Magic1 = Encoding.ASCII.GetBytes("SANREP01");
        private static readonly byte[] Magic2 = Encoding.ASCII.GetBytes("SANREP02");

        private const byte EncodingRaw = 0;
        private const byte EncodingBrotli = 1;

        public static void WriteHeader(Stream s, ReplayHeader h, bool finalised)
        {
            var json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(h));
            s.Write(Magic2, 0, Magic2.Length);
            WriteInt(s, json.Length);
            s.Write(json, 0, json.Length);
            s.WriteByte(finalised ? EncodingBrotli : EncodingRaw);
        }

        public static void WriteFrame(Stream s, byte kind, int tick, byte[] data)
        {
            s.WriteByte(kind);
            WriteInt(s, tick);
            WriteInt(s, data.Length);
            s.Write(data, 0, data.Length);
        }

        public static ReplayHeader ReadHeaderOnly(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return Open(fs, out _, out _);
            }
        }

        /// Reads every whole frame. An unfinished `.part` simply ends at its
        /// last complete frame.
        public static List<ReplayFrame> ReadFrames(string path, out ReplayHeader header)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                header = Open(fs, out var body, out var format1);
                var frames = new List<ReplayFrame>(header.TickCount > 0 ? header.TickCount : 4096);
                try
                {
                    while (TryReadFrame(body, format1, out var f)) frames.Add(f);
                }
                catch (EndOfStreamException) { }
                catch (InvalidDataException) { }   // a compressed stream cut short mid-block
                return frames;
            }
        }

        /// Reads the header and hands back a stream positioned at the first
        /// frame, already inflated where the file is compressed.
        private static ReplayHeader Open(FileStream fs, out Stream body, out bool format1)
        {
            int b0 = fs.ReadByte(), b1 = fs.ReadByte();
            fs.Position = 0;
            if (b0 == 0x1f && b1 == 0x8b)
            {
                // Format 1, gzipped whole; also a raw format-1 part below.
                body = new GZipStream(fs, CompressionMode.Decompress);
                format1 = true;
                return ReadHeaderBody(body, Magic1);
            }

            var magic = ReadExact(fs, 8) ?? throw new InvalidDataException("not a replay (short file)");
            if (Same(magic, Magic1))
            {
                fs.Position = 0;
                body = fs;
                format1 = true;
                return ReadHeaderBody(body, Magic1);
            }
            if (!Same(magic, Magic2)) throw new InvalidDataException("not a replay (bad magic)");
            fs.Position = 0;
            var header = ReadHeaderBody(fs, Magic2);
            var encoding = fs.ReadByte();
            if (encoding < 0) throw new InvalidDataException("truncated header");
            body = encoding == EncodingBrotli ? Brotli.Decompress(fs) : fs;
            format1 = false;
            return header;
        }

        private static ReplayHeader ReadHeaderBody(Stream s, byte[] expectedMagic)
        {
            var magic = ReadExact(s, expectedMagic.Length) ?? throw new InvalidDataException("not a replay (short file)");
            if (!Same(magic, expectedMagic)) throw new InvalidDataException("not a replay (bad magic)");
            var len = ReadInt(s);
            if (len <= 0 || len > 16 * 1024 * 1024) throw new InvalidDataException("bad header length");
            var json = ReadExact(s, len) ?? throw new InvalidDataException("truncated header");
            return JsonConvert.DeserializeObject<ReplayHeader>(Encoding.UTF8.GetString(json))
                   ?? throw new InvalidDataException("empty header");
        }

        private static bool TryReadFrame(Stream s, bool format1, out ReplayFrame f)
        {
            f = default;
            int kind = s.ReadByte();
            if (kind < 0) return false;
            // Format 1 stored the network message type here; only HostData
            // (100) was ever written, and those are wire-format payloads.
            f.Kind = format1 ? ReplayFrame.KindWire : (byte)kind;
            f.Tick = ReadInt(s);
            var len = ReadInt(s);
            if (len < 0 || len > 256 * 1024 * 1024) return false;
            f.Data = ReadExact(s, len);
            return f.Data != null;
        }

        /// Turns an unfinished `.part` (raw frames) into the final file: the
        /// updated header, then every frame through one Brotli stream at the
        /// library's best quality. Removes the part afterwards.
        public static void Finalise(string partPath, string finalPath, ReplayHeader header)
        {
            using (var input = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new FileStream(finalPath, FileMode.Create, FileAccess.Write))
            {
                ReadHeaderBody(input, Magic2);
                if (input.ReadByte() != EncodingRaw) throw new InvalidDataException("part is not raw");
                WriteHeader(output, header, finalised: true);
                using (var brotli = Brotli.Compress(output, CompressionLevel.Optimal))
                {
                    input.CopyTo(brotli);
                }
            }
            File.Delete(partPath);
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void WriteInt(Stream s, int v)
        {
            s.WriteByte((byte)v);
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 24));
        }

        private static int ReadInt(Stream s)
        {
            var b = ReadExact(s, 4) ?? throw new EndOfStreamException();
            return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
        }

        private static byte[] ReadExact(Stream s, int count)
        {
            var buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return null;
                got += n;
            }
            return buf;
        }
    }
}
