using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SanctuaryHud
{
    // The local bridge: how the ladder site reaches the mod. The site's
    // server can't (the game sits behind home NAT), but the browser tab on
    // the same PC can, so the mod listens on 127.0.0.1 and the SanctuaryDB
    // page asks it what the game is doing (GET /status) and hands it the
    // match to act on (POST /match). The page relays the status inside the
    // polls it already makes to the site, so a game left open in the menu
    // costs the site nothing. See docs/local-bridge.md in the site repo.
    //
    // Two endpoints, plus OPTIONS preflights for both:
    //
    //   GET  /status  -> { modVersion, gameVersion, state, match | null }
    //   POST /match   <- the match object (as /api/mm/heartbeat returned it)
    //                    or null; -> { ok: true } or { ok: false, error }
    //
    // A browser sends Origin on every cross-origin request, and only an
    // origin on the allow list gets an answer: that is what stops a random
    // web page from pushing a fake match into the game. Requests without an
    // Origin (curl and friends) are refused for the same reason; a local
    // process can already do anything on the machine, so it isn't the threat.
    //
    // TcpListener with a hand-rolled HTTP/1.1 responder rather than
    // HttpListener: HttpListener needs a URL ACL reservation on Windows for
    // anyone who isn't an administrator. A loopback listener also doesn't
    // trip the Windows firewall prompt.
    //
    // Threading: accept and parse on a background thread; anything that
    // touches the game runs on Unity's main thread. /status serves a snapshot
    // the main thread refreshes every frame (CurrentState reads Unity time
    // and lobby state); /match parses off-thread and queues ApplyMatch for
    // the next Update, the way the Steam ticket callback is handled.
    public partial class LadderReporterPlugin
    {
        private const string SiteOrigin = "https://www.sanctuarydb.net";
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxBodyBytes = 64 * 1024;

        private ConfigEntry<int> _cfgMmLocalPort;
        private ConfigEntry<string> _cfgMmDevOrigins;

        private TcpListener _bridgeListener;
        private Thread _bridgeThread;
        private int _bridgePort;
        private bool _bridgeBindFailedLogged;
        private float _bridgeNextBindTry;

        // Read on the listener thread, written on the main thread; a
        // reference swap is atomic, and the fields are never mutated after
        // publication.
        private volatile BridgeSnapshot _bridgeSnapshot;
        private string _gameVersion;

        // Work for the main thread, drained in UpdateMatchmaking.
        private readonly ConcurrentQueue<Action> _bridgeMainThread = new ConcurrentQueue<Action>();

        private sealed class BridgeSnapshot
        {
            public string State;
            public string MatchId, MatchStatus, Phase;
        }

        private sealed class HttpRequest
        {
            public string Method, Path;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body;

            public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        }

        // ---- lifecycle -------------------------------------------------------

        private void AwakeBridge()
        {
            _cfgMmLocalPort = Config.Bind("Matchmaking", "LocalPort", 27555,
                "The loopback port the SanctuaryDB page talks to the mod on (127.0.0.1 only, never the network). " +
                "The site only tries the default; change it for testing and restart the game.");
            _cfgMmDevOrigins = Config.Bind("Matchmaking", "DevOrigins", "",
                "Extra web origins allowed to talk to the mod, comma-separated, e.g. http://localhost:5173 " +
                "for a site dev server. https://www.sanctuarydb.net is always allowed.");
            _gameVersion = Application.version;
            PublishBridgeSnapshot();
        }

        private void UpdateBridge()
        {
            PublishBridgeSnapshot();
            while (_bridgeMainThread.TryDequeue(out var work))
            {
                try { work(); }
                catch (Exception e) { Logger.LogError($"Matchmaking: bridge work failed: {e}"); }
            }
            var wanted = _cfgMmEnabled.Value;
            if (wanted && _bridgeListener == null && Time.realtimeSinceStartup >= _bridgeNextBindTry) StartBridge();
            else if (!wanted && _bridgeListener != null) StopBridge();
        }

        private void PublishBridgeSnapshot()
        {
            var m = _match;
            var state = CurrentState();
            var last = _bridgeSnapshot;
            if (last == null || last.State != state)
            {
                Logger.LogInfo($"Matchmaking: state {last?.State ?? "(none)"} -> {state}");
            }
            else if (last.MatchId == m?.Id && last.MatchStatus == m?.Status && last.Phase == _phase.ToString())
            {
                return;   // nothing changed since last frame; keep the old snapshot
            }
            _bridgeSnapshot = new BridgeSnapshot
            {
                State = state,
                MatchId = m?.Id,
                MatchStatus = m?.Status,
                Phase = _phase.ToString(),
            };
        }

        private void StartBridge()
        {
            var port = Mathf.Clamp(_cfgMmLocalPort.Value, 1, 65535);
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                listener.Start(8);
            }
            catch (Exception e)
            {
                // Another game instance, or something else on the port. The
                // page just won't see this mod (manual hosting), and results
                // still report; try again in a minute in case it frees up.
                if (!_bridgeBindFailedLogged)
                {
                    _bridgeBindFailedLogged = true;
                    Logger.LogWarning($"Matchmaking: could not listen on 127.0.0.1:{port} ({e.Message}). The site won't be " +
                                      "able to auto-launch this game; results still report. Another program (or a second " +
                                      "copy of the game) may hold the port: change Matchmaking.LocalPort and restart.");
                }
                _bridgeNextBindTry = Time.realtimeSinceStartup + 60f;
                return;
            }
            _bridgeListener = listener;
            _bridgePort = port;
            _bridgeBindFailedLogged = false;
            _bridgeThread = new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "LadderReporter bridge" };
            _bridgeThread.Start();
            Logger.LogInfo($"Matchmaking: listening for the site on 127.0.0.1:{port}.");
        }

        // Also runs on unload: the loader hot-reloads this DLL, and the new
        // copy must be able to take the port back.
        private void StopBridge()
        {
            var listener = _bridgeListener;
            _bridgeListener = null;
            if (listener == null) return;
            try { listener.Stop(); } catch { }
            _bridgeThread = null;
            Logger.LogInfo($"Matchmaking: stopped listening on 127.0.0.1:{_bridgePort}.");
        }

        // ---- listener thread ---------------------------------------------------

        private void AcceptLoop(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception)
                {
                    // Stop() closes the socket under us; anything else here
                    // is fatal for this listener, and the main thread will
                    // notice _bridgeListener is gone and rebind.
                    if (ReferenceEquals(_bridgeListener, listener))
                    {
                        _bridgeListener = null;
                        _bridgeNextBindTry = 0f;
                    }
                    return;
                }
                ThreadPool.QueueUserWorkItem(_ => ServeClient(client));
            }
        }

        private void ServeClient(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 3000;
                    client.SendTimeout = 3000;
                    client.NoDelay = true;
                    using (var stream = client.GetStream())
                    {
                        var request = ReadRequest(stream);
                        if (request == null)
                        {
                            WriteResponse(stream, 400, null, Error("malformed request"));
                            return;
                        }
                        Route(request, stream);
                    }
                }
            }
            catch (Exception e)
            {
                // A client that hung up mid-request is not news; anything
                // else is worth one line.
                if (!(e is IOException) && !(e is SocketException) && !(e is ObjectDisposedException))
                {
                    Logger.LogWarning($"Matchmaking: bridge request failed: {e.Message}");
                }
            }
        }

        private void Route(HttpRequest req, Stream stream)
        {
            var origin = req.Header("Origin");
            if (!OriginAllowed(origin))
            {
                WriteResponse(stream, 403, null, Error("origin not allowed"));
                return;
            }

            var known = req.Path == "/status" || req.Path == "/match";
            if (req.Method == "OPTIONS")
            {
                if (!known)
                {
                    WriteResponse(stream, 404, origin, Error("not found"));
                    return;
                }
                WriteResponse(stream, 204, origin, null, preflight: true);
                return;
            }

            if (req.Path == "/status")
            {
                if (req.Method != "GET")
                {
                    WriteResponse(stream, 405, origin, Error("use GET"));
                    return;
                }
                WriteResponse(stream, 200, origin, StatusJson());
                return;
            }

            if (req.Path == "/match")
            {
                if (req.Method != "POST")
                {
                    WriteResponse(stream, 405, origin, Error("use POST"));
                    return;
                }
                HandleMatchPost(req, stream, origin);
                return;
            }

            WriteResponse(stream, 404, origin, Error("not found"));
        }

        private string StatusJson()
        {
            var s = _bridgeSnapshot;
            var o = new JObject
            {
                ["modVersion"] = ModVersion,
                ["gameVersion"] = _gameVersion,
                ["state"] = s?.State ?? "menu",
                ["match"] = s?.MatchId == null
                    ? null
                    : new JObject { ["id"] = s.MatchId, ["status"] = s.MatchStatus, ["phase"] = s.Phase },
            };
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        private void HandleMatchPost(HttpRequest req, Stream stream, string origin)
        {
            var body = (req.Body ?? "").Trim();
            MmMatch match;
            if (body.Length == 0 || body == "null")
            {
                match = null;
            }
            else
            {
                JObject o;
                try { o = JObject.Parse(body); }
                catch (Exception e)
                {
                    WriteResponse(stream, 400, origin, Error("body is not a JSON object: " + e.Message));
                    return;
                }
                match = MmMatch.Parse(o);
                if (string.IsNullOrEmpty(match.Id) || string.IsNullOrEmpty(match.Status))
                {
                    WriteResponse(stream, 400, origin, Error("match needs an id and a status"));
                    return;
                }
            }
            if (MockMode)
            {
                // The mock file is the match source while it's set; the page
                // gets told rather than silently ignored.
                WriteResponse(stream, 409, origin, Error("Matchmaking.MockFile is set; the mock file is the match source"));
                return;
            }
            _bridgeMainThread.Enqueue(() => ApplyPushedMatch(match));
            WriteResponse(stream, 200, origin, "{\"ok\":true}");
        }

        // Main thread. The first auto match is also when the site session is
        // minted: a player who never queues never asks Steam for a ticket,
        // and a manual match never needs the token (no session id, no
        // events; the result report mints its own ticket).
        private void ApplyPushedMatch(MmMatch match)
        {
            if (match != null)
            {
                LogMatchOnce(match);
                if (_mmToken == null && NeedsSession(match) && UsingSteam) EnsureSession();
            }
            ApplyMatch(match);
        }

        private static bool NeedsSession(MmMatch m) =>
            m != null && m.Mode == "auto" && (m.Status == "countdown" || m.Status == "launch");

        // ---- origin ------------------------------------------------------------

        private bool OriginAllowed(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return false;
            origin = origin.Trim().TrimEnd('/');
            if (string.Equals(origin, SiteOrigin, StringComparison.OrdinalIgnoreCase)) return true;
            var dev = _cfgMmDevOrigins?.Value;
            if (string.IsNullOrWhiteSpace(dev)) return false;
            foreach (var raw in dev.Split(','))
            {
                var o = raw.Trim().TrimEnd('/');
                if (o.Length > 0 && string.Equals(origin, o, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---- HTTP --------------------------------------------------------------

        // Reads one request: request line, headers, and a body of exactly
        // Content-Length bytes. Anything oversized or malformed is null.
        private static HttpRequest ReadRequest(NetworkStream stream)
        {
            var buf = new byte[4096];
            var head = new MemoryStream();
            var headerEnd = -1;
            var bodyStart = new MemoryStream();
            while (headerEnd < 0)
            {
                var n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) return null;
                head.Write(buf, 0, n);
                if (head.Length > MaxHeaderBytes) return null;
                headerEnd = IndexOf(head.GetBuffer(), (int)head.Length, "\r\n\r\n");
            }
            var all = head.GetBuffer();
            var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
            var afterHeaders = headerEnd + 4;
            bodyStart.Write(all, afterHeaders, (int)head.Length - afterHeaders);

            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;
            var req = new HttpRequest { Method = parts[0].ToUpperInvariant(), Path = parts[1] };
            var q = req.Path.IndexOf('?');
            if (q >= 0) req.Path = req.Path.Substring(0, q);
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            var length = 0;
            var cl = req.Header("Content-Length");
            if (cl != null && (!int.TryParse(cl, out length) || length < 0 || length > MaxBodyBytes)) return null;
            while (bodyStart.Length < length)
            {
                var n = stream.Read(buf, 0, (int)Math.Min(buf.Length, length - bodyStart.Length));
                if (n <= 0) return null;
                bodyStart.Write(buf, 0, n);
            }
            if (length > 0) req.Body = Encoding.UTF8.GetString(bodyStart.GetBuffer(), 0, length);
            return req;
        }

        private static int IndexOf(byte[] data, int length, string needle)
        {
            var last = length - needle.Length;
            for (var i = 0; i <= last; i++)
            {
                var hit = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (data[i + j] != needle[j]) { hit = false; break; }
                }
                if (hit) return i;
            }
            return -1;
        }

        private static string Error(string message) =>
            new JObject { ["ok"] = false, ["error"] = message }.ToString(Newtonsoft.Json.Formatting.None);

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                default: return "Error";
            }
        }

        // One response, then the connection closes. `origin` is echoed as
        // Access-Control-Allow-Origin when the request passed the allow list
        // (null when it didn't: a 403 carries no CORS headers at all).
        private static void WriteResponse(Stream stream, int status, string origin, string json, bool preflight = false)
        {
            var sb = new StringBuilder(512);
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            if (origin != null)
            {
                sb.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n");
                sb.Append("Vary: Origin\r\n");
            }
            if (preflight)
            {
                sb.Append("Access-Control-Allow-Methods: GET, POST\r\n");
                sb.Append("Access-Control-Allow-Headers: content-type\r\n");
                sb.Append("Access-Control-Allow-Private-Network: true\r\n");
                sb.Append("Access-Control-Max-Age: 86400\r\n");
            }
            var body = json == null ? new byte[0] : Encoding.UTF8.GetBytes(json);
            if (json != null) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }
}
