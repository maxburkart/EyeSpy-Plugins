using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using PluginUtils;

namespace Plugins
{
    /// <summary>
    /// ContinuousLowRes — records a camera's low-resolution stream 24/7 in clock-aligned segments while
    /// Agent itself keeps recording high-resolution clips on motion/alerts from the Record URL.
    /// One camera entry in Agent, two kinds of recording.
    /// </summary>
    public class Main : PluginBase, ICamera
    {
        private const int CameraOfflineSeconds = 20;   // no frames for this long = camera off / disconnected
        private const int StallSeconds = 45;           // ffmpeg running but no media for this long = restart
        private const int HealthySeconds = 30;         // a run this long resets the failure back-off

        private readonly object _tickLock = new object();
        private readonly object _recLock = new object();
        private readonly Timer _timer;
        private readonly EventLog _events = new EventLog();
        private SegmentProcessor _segments;
        private FfmpegRecorder _recorder;
        private string _appliedSignature = "";
        private volatile bool _disposed;
        private volatile bool _forceRestart;

        private long _lastFrameTicks;
        private Size _frameSize = Size.Empty;
        private int _frameChannels = 3;

        private int _failureStreak;
        private bool _failureRaised;
        private DateTime _nextStartUtc = DateTime.MinValue;
        private DateTime _lastStopUtc = DateTime.MinValue;
        private DateTime _lastRetentionUtc = DateTime.MinValue;
        private string _lastError = "";
        private string _lastStopReason = "";

        private bool _detectAttempted;
        private string _detectedUrl;
        private string _detectedField;

        public Main() : base()
        {
            _timer = new Timer(Tick, null, 3000, 2000);
        }

        public string Supports => "video";

        public override List<string> GetCustomEvents()
        {
            // Agent shows these as "ContinuousLowRes: …" under the camera's Actions, so you can e.g. get a push when recording breaks
            return new List<string> { "Recorder Stopped", "Recorder Resumed" };
        }

        public override string GetConfiguration(string languageCode)
        {
            ConfigObject.Status = StatusHtml();
            return base.GetConfiguration(languageCode);
        }

        public override void SetConfiguration(string json)
        {
            base.SetConfiguration(json);
            // the supervisor compares settings on its next tick and restarts ffmpeg only if something relevant changed
        }

        // ---------------------------------------------------------------- frames & events

        public void ProcessVideoFrame(IntPtr frame, Size sz, int channels, int stride)
        {
            Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
            _frameSize = sz;
            _frameChannels = channels;
            if (_recorder is EncodeRecorder enc)
                enc.OfferFrame(frame, sz, channels, stride);
            // the frame is left untouched
        }

        public override void ProcessAgentEvent(string ev)
        {
            switch (ev)
            {
                case "MotionAlert":
                case "ManualAlert":
                case "AudioAlert":
                    _events.Alert();
                    break;
                case "RecordingStart":
                    _events.RecordingStarted();
                    break;
                case "RecordingStop":
                    _events.RecordingStopped();
                    break;
            }
        }

        // ---------------------------------------------------------------- commands

        /// <summary>
        /// From the config dialog buttons or the API:
        /// command.cgi?cmd=plugincommand&amp;ot=2&amp;oid={id}&amp;command=status|start|stop|restart|detect|cleanup
        /// </summary>
        public override string Command(string command)
        {
            switch ((command ?? "").Trim().ToLowerInvariant())
            {
                case "status":
                    return StatusJson("OK");
                case "start":
                case "enable":
                    ConfigObject.Enabled = true;
                    _nextStartUtc = DateTime.MinValue;
                    return Msg("Continuous recording enabled");
                case "stop":
                case "disable":
                    ConfigObject.Enabled = false;
                    return Msg("Continuous recording disabled");
                case "restart":
                    _forceRestart = true;
                    _nextStartUtc = DateTime.MinValue;
                    return Msg("Restarting recorder");
                case "detect":
                    RunDetection();
                    return Msg(_detectedUrl != null
                        ? "Found " + FfmpegRecorder.MaskCredentials(_detectedUrl) + " (" + _detectedField + ")"
                        : "No stream URL found in Agent's camera settings — paste the sub-stream URL into Source URL, or use Encode mode");
                case "cleanup":
                    int n = RunRetention();
                    return Msg("Removed " + n + " old segment(s)");
            }
            return null;
        }

        private static string Msg(string m) => new JsonObject { ["msg"] = m }.ToJsonString();

        // ---------------------------------------------------------------- supervisor

        private void Tick(object state)
        {
            if (_disposed || !Monitor.TryEnter(_tickLock)) return;
            try
            {
                if (_disposed || string.IsNullOrEmpty(CameraName) && CameraID == 0) return;
                var cfg = ConfigObject;
                var now = DateTime.UtcNow;
                _segments ??= new SegmentProcessor(() => ConfigObject, () => (LocalPort, CameraID), _events);

                bool cameraLive = (now - new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc)).TotalSeconds < CameraOfflineSeconds;
                bool want = cfg.Enabled && cameraLive;

                var rec = _recorder;
                if (rec != null)
                {
                    foreach (var seg in rec.TakeCompletedSegments()) _segments.Enqueue(seg);

                    string stopReason = null;
                    if (!cfg.Enabled) stopReason = "disabled";
                    else if (!cameraLive) stopReason = "camera offline";
                    else if (_forceRestart) stopReason = "restart requested";
                    else if (Signature(cfg) != _appliedSignature) stopReason = "settings changed";
                    else if (rec is EncodeRecorder enc && enc.FrameFormatChanged) stopReason = "frame size changed";

                    if (stopReason != null)
                    {
                        _forceRestart = false;
                        StopRecorder(stopReason, graceful: true);
                    }
                    else if (rec.HasExited)
                    {
                        Fail(rec.ExitDescription);
                    }
                    else if ((now - rec.LastProgressUtc).TotalSeconds > StallSeconds)
                    {
                        Fail("no video received for " + StallSeconds + "s" + rec.LastLog(" — "));
                    }
                    else if (_failureStreak > 0 && (now - rec.StartedUtc).TotalSeconds > HealthySeconds &&
                             (now - rec.LastProgressUtc).TotalSeconds < 10)
                    {
                        if (_failureRaised) Results.Add(new ResultInfo("Recorder Resumed", "Continuous recording running again (" + rec.ModeName + ")"));
                        _failureStreak = 0;
                        _failureRaised = false;
                        _lastError = "";
                    }
                }

                if (_recorder == null && want && now >= _nextStartUtc)
                    StartRecorder(cfg);

                if ((now - _lastRetentionUtc).TotalMinutes >= 30)
                    RunRetention();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Utils.LastException = ex;
            }
            finally
            {
                Monitor.Exit(_tickLock);
            }
        }

        private void StartRecorder(configuration cfg)
        {
            string ffmpeg = FfmpegLocator.Find(cfg.FfmpegPath, AppPath);
            if (ffmpeg == null)
            {
                Fail(string.IsNullOrWhiteSpace(cfg.FfmpegPath)
                    ? "ffmpeg not found next to Agent or on PATH — install it or set FFmpeg path"
                    : "FFmpeg path not found: " + cfg.FfmpegPath, minDelaySeconds: 60);
                return;
            }

            string dir = OutputDir(cfg);
            Directory.CreateDirectory(dir);

            // an ffmpeg orphaned by an Agent crash would keep writing here; stop it before starting a new one
            KillStaleFfmpeg(dir);

            // segments left by a previous run (Agent restart, crash, camera switched off) still get handed to Agent
            foreach (var stale in Directory.EnumerateFiles(dir, ".seglist_*.csv")) { try { File.Delete(stale); } catch { } }
            if (cfg.RegisterInAgent)
                foreach (var f in SegmentNames.List(dir)) _segments.Enqueue(f.FullName);

            // decide copy vs encode
            string mode = (cfg.Mode ?? "auto").Trim().ToLowerInvariant();
            string url = (cfg.SourceUrl ?? "").Trim();
            if (url.Length == 0 && mode != "encode")
            {
                if (!_detectAttempted) RunDetection();
                url = _detectedUrl ?? "";
            }
            if (mode == "copy" && url.Length == 0)
            {
                Fail("Copy mode needs the camera's sub-stream URL in Source URL", minDelaySeconds: 60);
                return;
            }
            bool useCopy = mode == "copy" || (mode == "auto" && url.Length > 0);
            if (!useCopy && _frameSize.IsEmpty) return; // wait for the first frame

            var s = new RecorderSettings
            {
                FfmpegPath = ffmpeg,
                OutputDir = dir,
                SegmentSeconds = Math.Clamp(cfg.SegmentMinutes, 1, 240) * 60,
                Container = (cfg.Container ?? "fmp4").Trim().ToLowerInvariant(),
                ExtraInputArgs = cfg.ExtraInputArgs ?? "",
                ExtraOutputArgs = cfg.ExtraOutputArgs ?? "",
                SourceUrl = url,
                RtspTransport = (cfg.RtspTransport ?? "tcp").Trim().ToLowerInvariant(),
                Audio = (cfg.Audio ?? "none").Trim().ToLowerInvariant(),
                Height = cfg.EncodeHeight,
                Fps = Math.Clamp(cfg.EncodeFps, 1, 30),
                Quality = cfg.EncodeQuality,
                Codec = cfg.EncodeCodec,
                BitrateKbps = cfg.EncodeBitrateKbps
            };

            // two runs in the same second would produce the same file name
            var sinceStop = DateTime.UtcNow - _lastStopUtc;
            if (sinceStop < TimeSpan.FromMilliseconds(1100)) Thread.Sleep(TimeSpan.FromMilliseconds(1100) - sinceStop);

            FfmpegRecorder rec = useCopy ? new CopyRecorder(s) : new EncodeRecorder(s, _frameSize, _frameChannels);
            try
            {
                rec.Start();
            }
            catch (Exception ex)
            {
                rec.Dispose();
                Fail("could not start ffmpeg: " + ex.Message);
                return;
            }

            lock (_recLock) _recorder = rec;
            _appliedSignature = Signature(cfg);
            try { File.WriteAllText(Path.Combine(dir, PidFile), rec.ProcessId.ToString(CultureInfo.InvariantCulture)); } catch { }
        }

        private const string PidFile = ".ffmpeg.pid";

        private static void KillStaleFfmpeg(string dir)
        {
            string pidPath = Path.Combine(dir, PidFile);
            try
            {
                if (!File.Exists(pidPath)) return;
                if (int.TryParse(File.ReadAllText(pidPath).Trim(), out int pid))
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    // PIDs get reused: only kill it if it really is ffmpeg
                    if (p.ProcessName.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        p.Kill(true);
                        p.WaitForExit(3000);
                    }
                }
            }
            catch { /* not running any more */ }
            try { File.Delete(pidPath); } catch { }
        }

        private void StopRecorder(string reason, bool graceful)
        {
            FfmpegRecorder rec;
            lock (_recLock) { rec = _recorder; _recorder = null; }
            if (rec == null) return;

            if (graceful) rec.Stop(TimeSpan.FromSeconds(8));
            rec.Dispose();
            // the segment ffmpeg just closed (or whatever it left behind) goes through the normal path
            foreach (var seg in rec.TakeCompletedSegments()) _segments?.Enqueue(seg);
            rec.DeleteSegmentList();
            try { File.Delete(Path.Combine(OutputDir(ConfigObject), PidFile)); } catch { }
            if (ConfigObject.RegisterInAgent)
                foreach (var f in SegmentNames.List(OutputDir(ConfigObject))) _segments?.Enqueue(f.FullName);

            _lastStopUtc = DateTime.UtcNow;
            _lastStopReason = reason;
        }

        private void Fail(string message, int minDelaySeconds = 0)
        {
            StopRecorder("failed", graceful: false);
            _failureStreak++;
            _lastError = message;
            int backoff = (int)Math.Min(120, 5 * Math.Pow(2, Math.Min(_failureStreak - 1, 6)));
            _nextStartUtc = DateTime.UtcNow.AddSeconds(Math.Max(backoff, minDelaySeconds));

            // one blip (camera reboot, Wi-Fi hiccup) isn't worth an alert; a second failure in a row is
            if (_failureStreak >= 2 && !_failureRaised)
            {
                Results.Add(new ResultInfo("Recorder Stopped", message));
                _failureRaised = true;
            }
        }

        private void RunDetection()
        {
            _detectAttempted = true;
            if (LocalPort <= 0) return;
            var (url, field) = AgentApi.DetectLiveUrl(LocalPort, CameraID);
            _detectedUrl = url;
            _detectedField = field;
        }

        private int RunRetention()
        {
            _lastRetentionUtc = DateTime.UtcNow;
            var cfg = ConfigObject;
            string dir = OutputDir(cfg);
            if (!Directory.Exists(dir)) return 0;

            // never touch the newest file while ffmpeg may be writing it, or anything waiting to be registered
            string newest = _recorder != null
                ? SegmentNames.List(dir).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
            int days = cfg.RetentionDays;
            if (cfg.RegisterInAgent && days <= 0) days = 7; // only stragglers live here in this mode
            return Retention.Apply(dir, days, cfg.MaxStorageGB,
                p => string.Equals(p, newest, StringComparison.OrdinalIgnoreCase) || (_segments != null && _segments.IsPending(p)));
        }

        // ---------------------------------------------------------------- helpers

        private string OutputDir(configuration cfg)
        {
            string root = string.IsNullOrWhiteSpace(cfg.OutputFolder)
                ? Path.Combine(string.IsNullOrEmpty(AppDataPath) ? AppContext.BaseDirectory : AppDataPath, "ContinuousLowRes")
                : cfg.OutputFolder.Trim();
            return Path.Combine(root, Sanitize(CameraID.ToString(CultureInfo.InvariantCulture) + "_" + (CameraName ?? "camera")));
        }

        private static string Sanitize(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(bad.Contains(c) || c == ' ' ? '_' : c);
            return sb.ToString();
        }

        private static string Signature(configuration c) => string.Join("|",
            c.Mode, c.SourceUrl, c.RtspTransport, c.Audio, c.SegmentMinutes, c.Container, c.OutputFolder,
            c.EncodeHeight, c.EncodeFps, c.EncodeQuality, c.EncodeCodec, c.EncodeBitrateKbps,
            c.FfmpegPath, c.ExtraInputArgs, c.ExtraOutputArgs);

        private string StatusJson(string msg)
        {
            var rec = _recorder;
            var o = new JsonObject
            {
                ["msg"] = msg,
                ["enabled"] = ConfigObject.Enabled,
                ["running"] = rec != null && !rec.HasExited,
                ["mode"] = rec?.ModeName ?? "",
                ["source"] = rec?.SourceDescription ?? "",
                ["since"] = rec?.StartedUtc.ToString("o") ?? "",
                ["segments"] = rec?.SegmentsCompleted ?? 0,
                ["registered"] = _segments?.Registered ?? 0,
                ["registerFailures"] = _segments?.Failed ?? 0,
                ["lastResult"] = _segments?.LastResult ?? "",
                ["lastError"] = _lastError,
                ["lastStopReason"] = _lastStopReason,
                ["droppedFrames"] = (rec as EncodeRecorder)?.DroppedFrames ?? 0,
                ["folder"] = CameraID > 0 || CameraName != null ? OutputDir(ConfigObject) : ""
            };
            return o.ToJsonString();
        }

        private string StatusHtml()
        {
            var rec = _recorder;
            string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var sb = new StringBuilder();
            if (!ConfigObject.Enabled) sb.Append("<b>Disabled</b>");
            else if (rec != null && !rec.HasExited)
                sb.Append("<b>Recording</b> (").Append(E(rec.ModeName)).Append(") — ").Append(E(rec.SourceDescription))
                  .Append("<br>since ").Append(E(rec.StartedUtc.ToLocalTime().ToString("g")))
                  .Append(", ").Append(rec.SegmentsCompleted).Append(" segment(s) this run");
            else sb.Append("<b>Not recording</b>").Append(_lastStopReason.Length > 0 ? " (" + E(_lastStopReason) + ")" : "");
            if (_segments != null && (_segments.Registered > 0 || _segments.LastResult.Length > 0))
                sb.Append("<br>Added to Agent: ").Append(_segments.Registered).Append(", last: ").Append(E(_segments.LastResult));
            if (_lastError.Length > 0) sb.Append("<br><span style='color:#d9534f'>").Append(E(_lastError)).Append("</span>");
            return sb.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) { base.Dispose(disposing); return; }
            _disposed = true;
            try { _timer.Dispose(); } catch { }
            lock (_tickLock)
            {
                try { StopRecorder("plugin unloaded", graceful: true); } catch { }
                try { _segments?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

    }
}
