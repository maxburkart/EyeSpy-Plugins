using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Plugins
{
    /// <summary>
    /// Remembers when Agent alerted / recorded so continuous segments that overlap an event can be tagged.
    /// </summary>
    internal sealed class EventLog
    {
        private readonly object _lock = new object();
        private readonly List<(DateTime start, DateTime end)> _spans = new List<(DateTime, DateTime)>();
        private DateTime? _recordingSince;

        public void Alert()
        {
            var now = DateTime.UtcNow;
            lock (_lock) { _spans.Add((now, now)); Prune(now); }
        }

        public void RecordingStarted()
        {
            lock (_lock) { _recordingSince ??= DateTime.UtcNow; }
        }

        public void RecordingStopped()
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (_recordingSince.HasValue) _spans.Add((_recordingSince.Value, now));
                _recordingSince = null;
                Prune(now);
            }
        }

        public bool Overlaps(DateTime startUtc, DateTime endUtc)
        {
            var pad = TimeSpan.FromSeconds(2);
            lock (_lock)
            {
                if (_recordingSince.HasValue && _recordingSince.Value <= endUtc + pad) return true;
                foreach (var s in _spans)
                    if (s.start <= endUtc + pad && s.end >= startUtc - pad) return true;
            }
            return false;
        }

        private void Prune(DateTime now)
        {
            _spans.RemoveAll(s => now - s.end > TimeSpan.FromHours(48));
        }
    }

    /// <summary>
    /// Calls Agent's local HTTP API.
    /// </summary>
    internal static class AgentApi
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private static string Base(int port) => "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// /command/addrecording copies/moves the file into the camera's folder and registers it in Agent's
        /// database, so it shows in the timeline and falls under Agent's storage management.
        /// </summary>
        public static (bool ok, string detail) AddRecording(int port, int cameraId, string path, DateTime startUtc, string tags)
        {
            long jsMs = (long)(startUtc - DateTime.UnixEpoch).TotalMilliseconds;
            string url = Base(port) + "/command/addrecording?oid=" + cameraId.ToString(CultureInfo.InvariantCulture) +
                         "&ot=2&path=" + Uri.EscapeDataString(path) +
                         "&timestamp=" + jsMs.ToString(CultureInfo.InvariantCulture) +
                         "&move=true";
            if (!string.IsNullOrWhiteSpace(tags)) url += "&tags=" + Uri.EscapeDataString(tags);

            try
            {
                using var resp = Client.GetAsync(url).GetAwaiter().GetResult();
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return (false, "HTTP " + (int)resp.StatusCode + " " + Trim(body));
                string err = ErrorFrom(body);
                return err == null ? (true, Trim(body)) : (false, err);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static JsonNode GetObject(int port, int cameraId)
        {
            try
            {
                string url = Base(port) + "/command/getObject?oid=" + cameraId.ToString(CultureInfo.InvariantCulture) + "&ot=2";
                string body = Client.GetStringAsync(url).GetAwaiter().GetResult();
                return JsonNode.Parse(body);
            }
            catch { return null; }
        }

        private static string ErrorFrom(string body)
        {
            try
            {
                if (JsonNode.Parse(body) is JsonObject o)
                {
                    foreach (var key in new[] { "error", "err" })
                    {
                        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                            return s;
                        if (o[key] is JsonValue b && b.TryGetValue<bool>(out var flag) && flag)
                            return Trim(body);
                    }
                    if (o["status"] is JsonValue st && st.TryGetValue<string>(out var status) &&
                        status.Equals("error", StringComparison.OrdinalIgnoreCase))
                        return o["message"]?.ToString() ?? Trim(body);
                }
            }
            catch { /* non-JSON 200 response: treat as success */ }
            return null;
        }

        private static string Trim(string s) => s == null ? "" : (s.Length > 200 ? s.Substring(0, 200) + "…" : s);

        /// <summary>
        /// Best effort: look through the camera's config for its live (sub) stream URL.
        /// Agent stores credentials separately, so they're merged in when found next to the URL.
        /// Returns null when nothing convincing is found (the plugin then falls back to encode mode).
        /// </summary>
        public static (string url, string field) DetectLiveUrl(int port, int cameraId)
        {
            var root = GetObject(port, cameraId);
            if (root == null) return (null, null);
            var found = new List<(int score, string url, string path, JsonObject parent)>();
            Walk(root, "", null, found);
            if (found.Count == 0) return (null, null);
            var best = found.OrderByDescending(f => f.score).First();
            return (AddCredentials(best.url, best.parent), best.path);
        }

        private static readonly string[] RejectKeys = { "record", "main", "hd", "snapshot", "snap", "ptz", "onvif", "audio", "talk", "webhook", "cloud", "ftp", "mqtt", "image", "jpg", "photo", "action", "notify" };

        private static void Walk(JsonNode node, string path, JsonObject parent, List<(int, string, string, JsonObject)> found)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var kv in o) Walk(kv.Value, path.Length == 0 ? kv.Key : path + "." + kv.Key, o, found);
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) Walk(a[i], path + "[" + i + "]", parent, found);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s):
                    s = s.Trim();
                    if (!Regex.IsMatch(s, @"^(rtsps?|rtmps?|https?)://", RegexOptions.IgnoreCase)) break;
                    string key = path.Substring(path.LastIndexOf('.') + 1).ToLowerInvariant();
                    string lower = s.ToLowerInvariant();
                    if (RejectKeys.Any(k => key.Contains(k))) break;
                    if (lower.Contains("/onvif/") || lower.Contains("snapshot") || lower.EndsWith(".jpg") || lower.Contains("ispyconnect")) break;
                    int score = 1;
                    if (key.Contains("live") || key.Contains("sub")) score += 3;
                    if (key.Contains("source") || key.Contains("url") || key.Contains("uri") || key.Contains("stream")) score += 1;
                    if (lower.StartsWith("rtsp")) score += 2;
                    found.Add((score, s, path, parent));
                    break;
            }
        }

        private static string AddCredentials(string url, JsonObject parent)
        {
            if (parent == null || url.Contains('@')) return url;
            string user = FirstString(parent, "username", "login", "user");
            string pass = FirstString(parent, "password", "pass", "pwd");
            if (string.IsNullOrEmpty(user)) return url;
            int i = url.IndexOf("://", StringComparison.Ordinal) + 3;
            string cred = Uri.EscapeDataString(user) + (string.IsNullOrEmpty(pass) ? "" : ":" + Uri.EscapeDataString(pass)) + "@";
            return url.Substring(0, i) + cred + url.Substring(i);
        }

        private static string FirstString(JsonObject o, params string[] keys)
        {
            foreach (var kv in o)
                if (keys.Contains(kv.Key.ToLowerInvariant()) && kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    return s;
            return null;
        }
    }

    /// <summary>
    /// Handles finished segments off the timer thread: register with Agent (with retries),
    /// or, in standalone mode, rename event segments so they stand out.
    /// </summary>
    internal sealed class SegmentProcessor : IDisposable
    {
        private readonly BlockingCollection<(string path, int attempt)> _queue = new BlockingCollection<(string, int)>();
        private readonly ConcurrentDictionary<string, byte> _inFlight = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<configuration> _cfg;
        private readonly Func<(int port, int cameraId)> _camera;
        private readonly EventLog _events;
        private readonly Thread _thread;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public int Registered, Failed;
        public string LastResult = "";

        public SegmentProcessor(Func<configuration> cfg, Func<(int, int)> camera, EventLog events)
        {
            _cfg = cfg;
            _camera = camera;
            _events = events;
            _thread = new Thread(Run) { IsBackground = true, Name = "ContinuousLowRes segments" };
            _thread.Start();
        }

        public bool IsPending(string path) => _inFlight.ContainsKey(path);

        public void Enqueue(string path)
        {
            if (_inFlight.TryAdd(path, 0))
            {
                try { _queue.Add((path, 0)); } catch (InvalidOperationException) { _inFlight.TryRemove(path, out _); }
            }
        }

        private void Run()
        {
            try
            {
                foreach (var job in _queue.GetConsumingEnumerable(_cts.Token))
                    Process(job.path, job.attempt);
            }
            catch (OperationCanceledException) { }
        }

        private void Process(string path, int attempt)
        {
            try
            {
                if (!File.Exists(path)) { _inFlight.TryRemove(path, out _); return; }
                var cfg = _cfg();
                var fi = new FileInfo(path);
                if (fi.Length < 1024)
                {
                    // ffmpeg occasionally leaves an empty file when a run dies at once; nothing to keep
                    TryDelete(path);
                    _inFlight.TryRemove(path, out _);
                    return;
                }

                DateTime startUtc = SegmentNames.StartUtc(path) ?? fi.CreationTimeUtc;
                DateTime endUtc = fi.LastWriteTimeUtc;
                bool hasEvent = cfg.TagEventSegments && _events.Overlaps(startUtc, endUtc);

                if (cfg.RegisterInAgent)
                {
                    var (port, cameraId) = _camera();
                    string tags = (cfg.Tags ?? "").Trim().Trim(',');
                    if (hasEvent) tags = tags.Length == 0 ? "event" : tags + ",event";
                    var (ok, detail) = AgentApi.AddRecording(port, cameraId, path, startUtc, tags);
                    if (ok)
                    {
                        Interlocked.Increment(ref Registered);
                        LastResult = "registered " + Path.GetFileName(path);
                        _inFlight.TryRemove(path, out _);
                        return;
                    }

                    LastResult = "Agent rejected " + Path.GetFileName(path) + ": " + detail;
                    if (attempt < 5)
                    {
                        // Agent may be busy or restarting; try again with a growing delay
                        var delay = TimeSpan.FromSeconds(30 * (attempt + 1));
                        Task.Delay(delay, _cts.Token).ContinueWith(t =>
                        {
                            if (!t.IsCanceled) { try { _queue.Add((path, attempt + 1)); } catch { } }
                        });
                        return;
                    }
                    Interlocked.Increment(ref Failed);
                    _inFlight.TryRemove(path, out _); // stays in the folder; retention cleans it up
                    return;
                }

                // standalone storage: just mark event segments in the file name
                if (hasEvent && !Path.GetFileNameWithoutExtension(path).EndsWith("_event", StringComparison.OrdinalIgnoreCase))
                {
                    string target = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_event" + Path.GetExtension(path));
                    try { File.Move(path, target); } catch { }
                }
                LastResult = "saved " + Path.GetFileName(path);
                _inFlight.TryRemove(path, out _);
            }
            catch (Exception ex)
            {
                LastResult = ex.Message;
                _inFlight.TryRemove(path, out _);
            }
        }

        private static void TryDelete(string p) { try { File.Delete(p); } catch { } }

        public void Dispose()
        {
            _queue.CompleteAdding();
            // let a registration in progress finish briefly, then stop
            if (!_thread.Join(5000)) _cts.Cancel();
        }
    }

    internal static class SegmentNames
    {
        // matches what ffmpeg writes: 2026-10-10_15-30-00.mp4 (optionally _event)
        private static readonly Regex Pattern = new Regex(@"^(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(_event)?\.(mp4|mkv)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsSegment(string path) => Pattern.IsMatch(Path.GetFileName(path));

        /// <summary>ffmpeg's strftime uses local time, so parse as local and convert.</summary>
        public static DateTime? StartUtc(string path)
        {
            var m = Pattern.Match(Path.GetFileName(path));
            if (!m.Success) return null;
            if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
                return local.ToUniversalTime();
            return null;
        }

        public static IEnumerable<FileInfo> List(string dir)
        {
            if (!Directory.Exists(dir)) return Enumerable.Empty<FileInfo>();
            return new DirectoryInfo(dir).EnumerateFiles().Where(f => IsSegment(f.Name));
        }
    }

    internal static class Retention
    {
        /// <summary>
        /// Deletes only files this plugin wrote (by name pattern) in its own folder.
        /// </summary>
        public static int Apply(string dir, int days, int maxGB, Func<string, bool> keep)
        {
            int deleted = 0;
            var files = SegmentNames.List(dir).Where(f => !keep(f.FullName)).OrderBy(f => f.LastWriteTimeUtc).ToList();
            if (days > 0)
            {
                var cutoff = DateTime.UtcNow.AddDays(-days);
                foreach (var f in files.Where(f => f.LastWriteTimeUtc < cutoff).ToList())
                    if (TryDelete(f)) { files.Remove(f); deleted++; }
            }
            if (maxGB > 0)
            {
                long limit = (long)maxGB * 1024L * 1024L * 1024L;
                long total = SegmentNames.List(dir).Sum(f => f.Length);
                foreach (var f in files.ToList())
                {
                    if (total <= limit) break;
                    long len = f.Length;
                    if (TryDelete(f)) { total -= len; deleted++; }
                }
            }
            return deleted;
        }

        private static bool TryDelete(FileInfo f)
        {
            try { f.Delete(); return true; } catch { return false; }
        }
    }

    internal static class FfmpegLocator
    {
        public static string Find(string configured, string appPath)
        {
            string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

            if (!string.IsNullOrWhiteSpace(configured))
            {
                var c = configured.Trim().Trim('"');
                if (Directory.Exists(c)) c = Path.Combine(c, exe);
                return File.Exists(c) ? c : null;
            }

            if (!string.IsNullOrEmpty(appPath))
            {
                foreach (var sub in new[] { "", "ffmpeg", "ffmpeg/bin", "bin", "x64", "lib" })
                {
                    var p = Path.Combine(appPath, sub, exe);
                    if (File.Exists(p)) return p;
                }
            }

            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = Path.Combine(dir.Trim().Trim('"'), exe);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }

            foreach (var p in new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg", "/opt/local/bin/ffmpeg" })
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(p)) return p;

            return null;
        }
    }
}
