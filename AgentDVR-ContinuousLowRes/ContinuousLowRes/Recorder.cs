using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Plugins
{
    /// <summary>
    /// Settings captured when a recorder starts. Changing any of them restarts ffmpeg.
    /// </summary>
    internal sealed class RecorderSettings
    {
        public string FfmpegPath;
        public string OutputDir;
        public int SegmentSeconds;
        public string Container;          // fmp4 | mp4 | mkv
        public string ExtraInputArgs = "";
        public string ExtraOutputArgs = "";

        // copy mode
        public string SourceUrl;
        public string RtspTransport;      // tcp | udp | auto
        public string Audio;              // none | copy | aac

        // encode mode
        public int Height, Fps, Quality, BitrateKbps;
        public string Codec;

        public string Extension => Container == "mkv" ? ".mkv" : ".mp4";
    }

    /// <summary>
    /// Supervises one ffmpeg process that writes clock-aligned segments into OutputDir.
    /// Progress comes from "-progress pipe:1" so stalls are detected the same way on every ffmpeg version.
    /// Finished segments are read back from ffmpeg's own segment list, so a file is only reported once it is closed.
    /// </summary>
    internal abstract class FfmpegRecorder : IDisposable
    {
        protected readonly RecorderSettings S;
        protected Process P;
        private readonly string _listFile;
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _log = new ConcurrentQueue<string>();
        private string _lastProgressValue = "";
        private long _lastProgressTicks;

        public DateTime StartedUtc { get; private set; }
        public DateTime LastProgressUtc => new DateTime(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);
        public string ModeName { get; protected set; }
        public string SourceDescription { get; protected set; }
        public int SegmentsCompleted { get; private set; }
        public int ProcessId { get { try { return P?.Id ?? 0; } catch { return 0; } } }

        protected FfmpegRecorder(RecorderSettings s)
        {
            S = s;
            _listFile = Path.Combine(s.OutputDir, ".seglist_" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + ".csv");
        }

        protected abstract string BuildInputArgs();
        protected abstract string BuildCodecArgs();

        public bool HasExited
        {
            get
            {
                try { return P == null || P.HasExited; } catch { return true; }
            }
        }

        public string ExitDescription
        {
            get
            {
                string code = "";
                try { if (P != null && P.HasExited) code = " (exit code " + P.ExitCode + ")"; } catch { }
                return "ffmpeg stopped" + code + LastLog(" — ");
            }
        }

        public string LastLog(string prefix = "")
        {
            var lines = _log.ToArray();
            if (lines.Length == 0) return "";
            // the last couple of lines are usually the useful ones
            int take = Math.Min(2, lines.Length);
            return prefix + string.Join(" | ", lines, lines.Length - take, take);
        }

        public string BuildArguments()
        {
            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel warning -nostats -progress pipe:1 ");
            sb.Append(BuildInputArgs()).Append(' ');
            sb.Append(BuildCodecArgs()).Append(' ');
            if (!string.IsNullOrWhiteSpace(S.ExtraOutputArgs)) sb.Append(S.ExtraOutputArgs.Trim()).Append(' ');

            sb.Append("-f segment -segment_time ").Append(S.SegmentSeconds.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -segment_atclocktime 1 -reset_timestamps 1 -strftime 1");
            switch (S.Container)
            {
                case "mkv":
                    sb.Append(" -segment_format matroska");
                    break;
                case "mp4":
                    sb.Append(" -segment_format mp4 -segment_format_options movflags=+faststart");
                    break;
                default: // fmp4: every segment stays playable even if Agent/ffmpeg is killed mid-segment
                    sb.Append(" -segment_format mp4 -segment_format_options movflags=+frag_keyframe+empty_moov+default_base_moof");
                    break;
            }
            sb.Append(" -segment_list ").Append(Q(_listFile)).Append(" -segment_list_type csv ");
            sb.Append(Q(Path.Combine(S.OutputDir, "%Y-%m-%d_%H-%M-%S" + S.Extension)));
            return sb.ToString();
        }

        public virtual void Start()
        {
            var psi = new ProcessStartInfo(S.FfmpegPath, BuildArguments())
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = S.OutputDir
            };
            P = new Process { StartInfo = psi, EnableRaisingEvents = true };
            P.ErrorDataReceived += (o, e) => AddLog(e.Data);
            P.OutputDataReceived += (o, e) => OnProgressLine(e.Data);
            StartedUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _lastProgressTicks, StartedUtc.Ticks);
            P.Start();
            P.BeginErrorReadLine();
            P.BeginOutputReadLine();
        }

        private void AddLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            _log.Enqueue(MaskCredentials(line.Trim()));
            while (_log.Count > 30 && _log.TryDequeue(out _)) { }
        }

        private void OnProgressLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            int eq = line.IndexOf('=');
            if (eq <= 0) return;
            string key = line.Substring(0, eq);
            string value = line.Substring(eq + 1).Trim();
            if (value.Length == 0 || value == "N/A") return;
            // out_time_us (newer ffmpeg) / out_time_ms (older, also µs): any change means media is flowing
            if (key == "out_time_us" || key == "out_time_ms")
            {
                if (value != _lastProgressValue)
                {
                    _lastProgressValue = value;
                    Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
                }
            }
        }

        /// <summary>
        /// Returns full paths of segments ffmpeg has closed since the last call.
        /// </summary>
        public List<string> TakeCompletedSegments()
        {
            var result = new List<string>();
            if (!File.Exists(_listFile)) return result;
            string[] lines;
            try
            {
                using (var fs = new FileStream(_listFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                    lines = sr.ReadToEnd().Split('\n');
            }
            catch (IOException) { return result; }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                string name = ParseCsvFirstField(line);
                if (string.IsNullOrEmpty(name)) continue;
                string full = Path.IsPathRooted(name) ? name : Path.Combine(S.OutputDir, name);
                if (_reported.Add(full))
                {
                    SegmentsCompleted++;
                    result.Add(full);
                }
            }
            return result;
        }

        private static string ParseCsvFirstField(string line)
        {
            if (line.StartsWith("\"", StringComparison.Ordinal))
            {
                var sb = new StringBuilder();
                for (int i = 1; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else break;
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }
            int comma = line.IndexOf(',');
            return comma < 0 ? line : line.Substring(0, comma);
        }

        /// <summary>
        /// Ask ffmpeg to finish the current segment and exit; kill it if it doesn't within the timeout.
        /// </summary>
        public virtual void Stop(TimeSpan timeout)
        {
            if (HasExited) return;
            try { RequestGracefulExit(); } catch { }
            try
            {
                if (!P.WaitForExit((int)timeout.TotalMilliseconds))
                    Kill();
            }
            catch { Kill(); }
        }

        protected virtual void RequestGracefulExit()
        {
            // 'q' on stdin = clean shutdown: trailer + final segment-list entry are written
            P.StandardInput.Write('q');
            P.StandardInput.Flush();
        }

        public void Kill()
        {
            try { if (P != null && !P.HasExited) P.Kill(true); } catch { }
            try { P?.WaitForExit(3000); } catch { }
        }

        public virtual void Dispose()
        {
            Kill();
            try { P?.Dispose(); } catch { }
            P = null;
        }

        public void DeleteSegmentList()
        {
            try { if (File.Exists(_listFile)) File.Delete(_listFile); } catch { }
        }

        internal static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";

        internal static string MaskCredentials(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return System.Text.RegularExpressions.Regex.Replace(s, @"(\w+://[^:/@\s]*):[^@/\s]*@", "$1:***@");
        }
    }

    /// <summary>
    /// Stream-copies the camera's low-res stream straight to disk. Near-zero CPU; opens one extra
    /// connection to the camera (not to Agent), and needs no duplicate camera in Agent.
    /// </summary>
    internal sealed class CopyRecorder : FfmpegRecorder
    {
        public CopyRecorder(RecorderSettings s) : base(s)
        {
            ModeName = "copy";
            SourceDescription = MaskCredentials(s.SourceUrl);
        }

        protected override string BuildInputArgs()
        {
            var sb = new StringBuilder();
            bool rtsp = S.SourceUrl.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase);
            if (rtsp && (S.RtspTransport == "tcp" || S.RtspTransport == "udp"))
                sb.Append("-rtsp_transport ").Append(S.RtspTransport).Append(' ');
            if (!string.IsNullOrWhiteSpace(S.ExtraInputArgs)) sb.Append(S.ExtraInputArgs.Trim()).Append(' ');
            sb.Append("-i ").Append(Q(S.SourceUrl));
            return sb.ToString();
        }

        protected override string BuildCodecArgs()
        {
            var sb = new StringBuilder("-map 0:v:0 -c:v copy");
            switch (S.Audio)
            {
                case "copy":
                    sb.Append(" -map 0:a:0? -c:a copy");
                    break;
                case "aac":
                    sb.Append(" -map 0:a:0? -c:a aac -b:a 64k");
                    break;
                default:
                    sb.Append(" -an");
                    break;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Encodes the frames Agent already decodes for this camera (normally its Live URL / sub stream).
    /// Works with any source type and needs no URL, at the cost of an H.264 encode.
    /// </summary>
    internal sealed class EncodeRecorder : FfmpegRecorder
    {
        private readonly Size _size;
        private readonly int _channels;
        private readonly int _frameBytes;
        private readonly BlockingCollection<byte[]> _queue;
        private readonly long _minIntervalTicks;
        private long _lastQueuedTicks;
        private Thread _writer;
        private volatile bool _formatChanged;
        public bool FrameFormatChanged => _formatChanged;
        public long DroppedFrames;

        public EncodeRecorder(RecorderSettings s, Size frameSize, int channels) : base(s)
        {
            ModeName = "encode";
            SourceDescription = "Agent frames " + frameSize.Width + "x" + frameSize.Height;
            _size = frameSize;
            _channels = channels;
            _frameBytes = frameSize.Width * frameSize.Height * channels;
            int fps = Math.Max(1, s.Fps);
            _queue = new BlockingCollection<byte[]>(Math.Max(4, fps * 2));
            // accept slightly faster than the target rate; ffmpeg's fps filter makes the output exact
            _minIntervalTicks = (long)(Stopwatch.Frequency / (double)fps * 0.8);
        }

        protected override string BuildInputArgs()
        {
            var sb = new StringBuilder();
            sb.Append("-f rawvideo -pix_fmt ").Append(_channels == 4 ? "bgra" : "bgr24");
            sb.Append(" -video_size ").Append(_size.Width).Append('x').Append(_size.Height);
            sb.Append(" -use_wallclock_as_timestamps 1 ");
            if (!string.IsNullOrWhiteSpace(S.ExtraInputArgs)) sb.Append(S.ExtraInputArgs.Trim()).Append(' ');
            sb.Append("-i pipe:0");
            return sb.ToString();
        }

        protected override string BuildCodecArgs()
        {
            int fps = Math.Max(1, S.Fps);
            int h = Math.Max(120, S.Height);
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            // never upscale; keep dimensions even for 4:2:0
            sb.Append("-map 0:v -vf ").Append(Q("fps=" + fps.ToString(inv) + ",scale=-2:'trunc(min(" + h.ToString(inv) + ",ih)/2)*2'"));
            string codec = string.IsNullOrWhiteSpace(S.Codec) ? "libx264" : S.Codec.Trim();
            sb.Append(" -c:v ").Append(codec);
            if (codec == "libx264" || codec == "libx265")
                sb.Append(" -preset veryfast -crf ").Append(Math.Clamp(S.Quality, 0, 51).ToString(inv));
            else
                sb.Append(" -b:v ").Append(Math.Max(64, S.BitrateKbps).ToString(inv)).Append('k');
            sb.Append(" -pix_fmt yuv420p -g ").Append((fps * 2).ToString(inv)).Append(" -an");
            return sb.ToString();
        }

        public override void Start()
        {
            base.Start();
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "ContinuousLowRes frame writer" };
            _writer.Start();
        }

        /// <summary>
        /// Called on Agent's frame thread: copy (rate-limited) and return immediately.
        /// </summary>
        public void OfferFrame(IntPtr frame, Size sz, int channels, int stride)
        {
            if (_queue.IsAddingCompleted) return;
            if (sz != _size || channels != _channels) { _formatChanged = true; return; }

            long now = Stopwatch.GetTimestamp();
            if (now - Interlocked.Read(ref _lastQueuedTicks) < _minIntervalTicks) return;
            if (_queue.Count >= _queue.BoundedCapacity) { Interlocked.Increment(ref DroppedFrames); return; }

            int rowBytes = sz.Width * channels;
            byte[] buf = ArrayPool<byte>.Shared.Rent(_frameBytes);
            try
            {
                if (stride == rowBytes)
                    Marshal.Copy(frame, buf, 0, _frameBytes);
                else
                    for (int y = 0; y < sz.Height; y++)
                        Marshal.Copy(IntPtr.Add(frame, y * stride), buf, y * rowBytes, rowBytes);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buf);
                return;
            }

            bool added = false;
            try { added = _queue.TryAdd(buf); } catch (InvalidOperationException) { }
            if (added) Interlocked.Exchange(ref _lastQueuedTicks, now);
            else { ArrayPool<byte>.Shared.Return(buf); Interlocked.Increment(ref DroppedFrames); }
        }

        private void WriteLoop()
        {
            Stream stdin;
            try { stdin = P.StandardInput.BaseStream; } catch { return; }
            try
            {
                foreach (var buf in _queue.GetConsumingEnumerable())
                {
                    try { stdin.Write(buf, 0, _frameBytes); }
                    finally { ArrayPool<byte>.Shared.Return(buf); }
                }
                stdin.Flush();
            }
            catch (IOException) { /* ffmpeg exited; the supervisor notices */ }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            finally
            {
                // drain anything left so pooled buffers go back
                while (_queue.TryTake(out var b)) ArrayPool<byte>.Shared.Return(b);
            }
        }

        protected override void RequestGracefulExit()
        {
            // EOF on the frame pipe = ffmpeg flushes the encoder and closes the segment cleanly
            _queue.CompleteAdding();
            _writer?.Join(3000);
            try { P.StandardInput.Close(); } catch { }
        }

        public override void Dispose()
        {
            if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
            base.Dispose();
            _writer?.Join(1000);
        }
    }
}
