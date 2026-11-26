// Visual trim editor + settings dialog for compress-video-to-100mb-ffmpeg.ps1 (Windows version of CompressVideoGUI.swift)
// Usage: CompressVideo.exe <video file>
// Build: gui\build.ps1 (uses the C# compiler that ships with Windows' .NET Framework; nothing to install)
// Written for C# 5, the language version that compiler supports.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CompressVideo
{
    // MARK: - Helpers

    static class Util
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string FmtTime(double t)
        {
            t = Math.Max(0, t);
            int m = (int)(t / 60);
            double s = t - m * 60;
            return m.ToString(Inv) + ":" + s.ToString("00.000", Inv);
        }

        /// Accepts seconds or [[HH:]MM:]SS[.ms], like the script.
        public static double? ParseTime(string text)
        {
            string[] parts = (text ?? "").Trim().Split(':');
            if (parts.Length < 1 || parts.Length > 3) return null;
            double total = 0;
            foreach (string p in parts)
            {
                double v;
                if (!double.TryParse(p, NumberStyles.AllowDecimalPoint, Inv, out v) || double.IsInfinity(v)) return null;
                total = total * 60 + v;
            }
            return total;
        }

        /// Quotes one argument for a Windows command line (CommandLineToArgvW rules).
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); }
                else sb.Append('\\', bs);
                bs = 0;
                sb.Append(c);
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }

        public static string Args(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(Quote));
        }

        /// Finds an executable on PATH.
        public static string Which(string name)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try
                {
                    string p = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// Explorer may have been started before ffmpeg was installed, so its (inherited) PATH can be stale:
        /// rebuild PATH from the registry, plus the usual winget/scoop/chocolatey locations.
        public static void RefreshPath()
        {
            var parts = new List<string>();
            foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
            {
                string v = Environment.GetEnvironmentVariable("PATH", target);
                if (!string.IsNullOrEmpty(v)) parts.AddRange(v.Split(';').Select(Environment.ExpandEnvironmentVariables));
            }
            parts.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            parts.Add(Path.Combine(local, @"Microsoft\WinGet\Links"));
            parts.Add(Path.Combine(home, @"scoop\shims"));
            parts.Add(@"C:\ProgramData\chocolatey\bin");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clean = parts.Where(p => p.Trim().Length > 0 && seen.Add(p.Trim())).Select(p => p.Trim());
            Environment.SetEnvironmentVariable("PATH", string.Join(";", clean));
        }

        public static ProcessStartInfo Hidden(string exe, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo(exe, Args(args));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            return psi;
        }

        /// Runs a tool and returns its stdout (UTF-8), or null if it could not be started.
        public static string Capture(string exe, IEnumerable<string> args)
        {
            try
            {
                var psi = Hidden(exe, args);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (var p = Process.Start(psi))
                {
                    Job.Add(p);
                    p.StandardInput.Close();
                    p.ErrorDataReceived += delegate { };
                    p.BeginErrorReadLine();
                    string s = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        public static void KillTree(Process p)
        {
            try
            {
                var k = Process.Start(Hidden("taskkill", new[] { "/T", "/F", "/PID", p.Id.ToString(Inv) }));
                k.WaitForExit(5000);
            }
            catch (Exception) { }
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
        }

        /// Diagnostics: %TEMP%\CompressVideo.log (startup, load and playback timings), cleared on each start.
        public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "CompressVideo.log");
        static readonly Stopwatch logClock = Stopwatch.StartNew();
        static readonly object logLock = new object();

        public static void Log(string msg)
        {
            try
            {
                lock (logLock) File.AppendAllText(LogPath, logClock.Elapsed.TotalSeconds.ToString("0.000", Inv) + "  " + msg + Environment.NewLine);
            }
            catch (Exception) { }
        }

        static readonly string RegKey = @"Software\CompressVideo";

        public static string GetSetting(string name, string fallback)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegKey))
            {
                object v = k == null ? null : k.GetValue(name);
                return v == null ? fallback : v.ToString();
            }
        }

        public static void SetSetting(string name, string value)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue(name, value);
        }
    }

    /// Kill-on-close job: ffmpeg/PowerShell children die with this app, even if it crashes.
    static class Job
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtendedLimits info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static IntPtr handle;
        static readonly object gate = new object();

        public static void Add(Process p)
        {
            try
            {
                lock (gate)
                {
                    if (handle == IntPtr.Zero)
                    {
                        handle = CreateJobObject(IntPtr.Zero, null);
                        var info = new ExtendedLimits();
                        info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        SetInformationJobObject(handle, 9, ref info, Marshal.SizeOf(typeof(ExtendedLimits)));
                    }
                    AssignProcessToJobObject(handle, p.Handle);
                }
            }
            catch (Exception) { }
        }
    }

    enum Phase { Editing, Encoding, Done, Failed }

    // MARK: - Video info

    class VideoInfo
    {
        public double Duration;
        public int Width, Height;          // as displayed (rotation metadata applied)
        public double Fps = 30;
        public int Rotate;                 // clockwise degrees ffmpeg would auto-rotate by (0/90/180/270)
        public string Codec = "", PixFmt = "", ColorTrc = "", ColorPrimaries = "", ColorSpace = "";
        public readonly List<string> AudioTracks = new List<string>();   // one entry (its title, or "") per audio track
        public bool HasAudio { get { return AudioTracks.Count > 0; } }
        public bool HasZscale;

        public bool IsHDR { get { return ColorTrc == "arib-std-b67" || ColorTrc == "smpte2084"; } }

        public static VideoInfo Probe(string ffprobe, string ffmpeg, string file)
        {
            string text = Util.Capture(ffprobe, new[] {
                "-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=codec_name,pix_fmt,width,height,r_frame_rate,avg_frame_rate,color_transfer,color_primaries,color_space:stream_side_data=rotation:stream_tags=rotate:format=duration",
                "-of", "default=noprint_wrappers=1", file });
            if (text == null) return null;
            var kv = new Dictionary<string, string>();
            foreach (string line in text.Split('\n'))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                if (k.StartsWith("TAG:")) k = k.Substring(4);
                if (!kv.ContainsKey(k)) kv[k] = v;
            }
            var info = new VideoInfo();
            string s;
            if (kv.TryGetValue("duration", out s)) double.TryParse(s, NumberStyles.Float, Util.Inv, out info.Duration);
            if (kv.TryGetValue("width", out s)) int.TryParse(s, out info.Width);
            if (kv.TryGetValue("height", out s)) int.TryParse(s, out info.Height);
            // Display-matrix "rotation" is counterclockwise; the old "rotate" tag is clockwise
            double rot = 0;
            if (kv.TryGetValue("rotation", out s)) { double.TryParse(s, NumberStyles.Float, Util.Inv, out rot); rot = -rot; }
            else if (kv.TryGetValue("rotate", out s)) double.TryParse(s, NumberStyles.Float, Util.Inv, out rot);
            info.Rotate = (((int)Math.Round(rot / 90) * 90) % 360 + 360) % 360;
            if (info.Rotate == 90 || info.Rotate == 270) { int t = info.Width; info.Width = info.Height; info.Height = t; }
            double fps = Rate(kv, "avg_frame_rate");
            if (fps < 1 || fps > 240) fps = Rate(kv, "r_frame_rate");
            if (fps >= 1 && fps <= 240) info.Fps = fps;
            kv.TryGetValue("codec_name", out info.Codec);
            info.Codec = info.Codec ?? "";
            kv.TryGetValue("pix_fmt", out info.PixFmt);
            info.PixFmt = info.PixFmt ?? "";
            kv.TryGetValue("color_transfer", out info.ColorTrc);
            kv.TryGetValue("color_primaries", out info.ColorPrimaries);
            kv.TryGetValue("color_space", out info.ColorSpace);
            info.ColorTrc = info.ColorTrc ?? "";
            if (string.IsNullOrEmpty(info.ColorPrimaries) || info.ColorPrimaries == "unknown") info.ColorPrimaries = "bt2020";
            if (string.IsNullOrEmpty(info.ColorSpace) || info.ColorSpace == "unknown") info.ColorSpace = "bt2020nc";

            string audio = Util.Capture(ffprobe, new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream=index:stream_tags=title", "-of", "csv=p=0", file }) ?? "";
            foreach (string line in audio.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                string[] parts = line.Trim().Split(new[] { ',' }, 2);
                info.AudioTracks.Add(parts.Length > 1 ? parts[1].Trim().Trim('"') : "");
            }
            if (info.IsHDR)
            {
                string filters = Util.Capture(ffmpeg, new[] { "-hide_banner", "-filters" }) ?? "";
                info.HasZscale = filters.Contains(" zscale ");
            }
            return info;
        }

        static double Rate(Dictionary<string, string> kv, string key)
        {
            string s;
            if (!kv.TryGetValue(key, out s)) return 0;
            string[] p = s.Split('/');
            double n, d = 1;
            if (!double.TryParse(p[0], NumberStyles.Float, Util.Inv, out n)) return 0;
            if (p.Length > 1 && (!double.TryParse(p[1], NumberStyles.Float, Util.Inv, out d) || d == 0)) return 0;
            return n / d;
        }
    }

    // MARK: - Frame decoding

    /// Decodes frames with ffmpeg into BGRA at a fixed preview size. HDR (HLG/PQ) is tone-mapped to SDR
    /// for display (like the macOS preview and VLC); the compressed file still keeps its HDR.
    /// Keeps one ffmpeg process open and reads forward from it, so playback and stepping forward are cheap;
    /// seeking backwards (or far ahead) restarts it at the new time.
    class FrameReader : IDisposable
    {
        readonly string ffmpeg, file;
        readonly VideoInfo info;
        public readonly int W, H;
        public readonly double Fps;
        public int FrameBytes { get { return W * H * 4; } }

        Process proc;
        Stream stream;
        double start;
        int index;

        /// Thumbnail mode: low priority, so thumbnails don't starve the preview.
        public bool Light;
        /// Decode on the GPU (d3d11va). Turned off for good if it produces nothing (codec not supported by the GPU).
        public bool Hw = true;
        double openedAt;
        int openedMax;

        public FrameReader(string ffmpeg, string file, VideoInfo info, int w, int h, double fps)
        {
            this.ffmpeg = ffmpeg; this.file = file; this.info = info;
            W = w; H = h; Fps = fps;
        }

        /// Time of the frame the next Read() returns (NaN when closed).
        public double NextTime { get { return proc == null ? double.NaN : start + index / Fps; } }

        public string Filter()
        {
            // fps=0 (thumbnails): no fps filter, which drops everything when under half a frame of video is left.
            // On the GPU path, fps runs before hwdownload so dropped frames are never copied back from the GPU.
            // Rotation is applied here, after scaling (ffmpeg's automatic rotation is off: it can't run on GPU frames)
            bool swap = info.Rotate == 90 || info.Rotate == 270;
            string f = (Fps > 0 ? "fps=" + Fps.ToString("0.###", Util.Inv) + "," : "") +
                       (Hw ? "hwdownload,format=" + (Regex.IsMatch(info.PixFmt, "1[02]") ? "p010le" : "nv12") + "," : "") +
                       "scale=" + (swap ? H : W) + ":" + (swap ? W : H) + ":flags=bilinear" +
                       (info.Rotate == 90 ? ",transpose=clock" : info.Rotate == 180 ? ",hflip,vflip" : info.Rotate == 270 ? ",transpose=cclock" : "");
            if (info.IsHDR && info.HasZscale)
                f += ",zscale=tin=" + info.ColorTrc + ":pin=" + info.ColorPrimaries + ":min=" + info.ColorSpace +
                     ":t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709,format=gbrp";
            return f + ",format=bgra";
        }

        public void Open(double t, int maxFrames)
        {
            Close();
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-noautorotate" };
            if (Hw)
            {
                args.AddRange(new[] { "-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11" });
                // ffmpeg's own AV1 decoder is the one that can use the GPU (builds may default to libaom/libdav1d)
                if (info.Codec == "av1") args.AddRange(new[] { "-c:v", "av1" });
            }
            args.AddRange(new[] { "-ss", Math.Max(0, t).ToString("0.######", Util.Inv), "-i", file, "-an", "-sn", "-dn", "-vf", Filter() });
            openedAt = t; openedMax = maxFrames;
            if (maxFrames > 0) { args.Add("-frames:v"); args.Add(maxFrames.ToString(Util.Inv)); }
            args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" });
            var psi = Util.Hidden(ffmpeg, args);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardInput = false;
            try
            {
                proc = Process.Start(psi);
                Job.Add(proc);
                if (Light) { try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { } }
                stream = proc.StandardOutput.BaseStream;
                start = Math.Max(0, t);
                index = 0;
            }
            catch (Exception) { proc = null; stream = null; }
        }

        public bool Read(byte[] buf)
        {
            if (stream == null) return false;
            int got = 0;
            try
            {
                while (got < FrameBytes)
                {
                    int n = stream.Read(buf, got, FrameBytes - got);
                    if (n <= 0) break;
                    got += n;
                }
            }
            catch (Exception) { }
            if (got < FrameBytes)
            {
                Close();
                if (Hw && index == 0 && openedAt < info.Duration - 1)   // (near the end, "nothing" is just the end)
                {
                    // Nothing at all from the GPU decoder: fall back to software decoding
                    Hw = false;
                    Util.Log("GPU decoding failed for " + info.Codec + "; using software decoding");
                    Open(openedAt, openedMax);
                    return Read(buf);
                }
                return false;
            }
            index++;
            return true;
        }

        /// Reads the frame at time t into buf, reusing the open decoder when t is just ahead of it.
        public bool ReadAt(double t, byte[] buf)
        {
            double half = 0.5 / Fps;
            double next = NextTime;
            if (double.IsNaN(next) || t < next - half || t > next + 2.0) Open(t, 0);
            bool any = false;
            while (proc != null && NextTime + half <= t)
            {
                if (!Read(buf)) break;
                any = true;
            }
            if (proc != null && Read(buf)) return true;
            if (any) return true;   // ran out right at the end: buf holds the last frame
            // Nothing decodes at t (the very end of the file): back up a little and keep the last frame
            Open(Math.Max(0, t - 0.5), (int)(0.5 * Fps) + 2);
            while (proc != null && Read(buf)) any = true;
            return any;
        }

        public void Close()
        {
            // Close our end of the pipe too: with a launcher shim (e.g. Chocolatey's ffmpeg.exe) the real
            // ffmpeg is a child process that only stops once its output pipe breaks
            if (stream != null) { try { stream.Dispose(); } catch (Exception) { } }
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(); } catch (Exception) { }
                try { proc.Dispose(); } catch (Exception) { }
            }
            proc = null;
            stream = null;
        }

        public void Dispose() { Close(); }
    }

    // MARK: - Audio

    /// Minimal waveOut player: queue PCM chunks, or reset to cut everything that is queued.
    class WaveOut : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WaveFormat { public short Tag, Channels; public int Rate, BytesPerSec; public short BlockAlign, Bits, Extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr { public IntPtr Data; public int Length, Recorded; public IntPtr User; public int Flags, Loops; public IntPtr Next, Reserved; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int device, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        public const int Rate = 48000, Channels = 2;
        IntPtr dev;
        readonly List<IntPtr> queued = new List<IntPtr>();
        static readonly int HdrSize = Marshal.SizeOf(typeof(WaveHdr));
        static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(WaveHdr), "Flags");

        public bool Open()
        {
            var f = new WaveFormat { Tag = 1, Channels = Channels, Rate = Rate, Bits = 16, BlockAlign = Channels * 2, BytesPerSec = Rate * Channels * 2 };
            return waveOutOpen(out dev, -1, ref f, IntPtr.Zero, IntPtr.Zero, 0) == 0;
        }

        public void Write(short[] samples, int offset, int count)
        {
            if (dev == IntPtr.Zero || count <= 0) return;
            Cleanup(false);
            IntPtr data = Marshal.AllocHGlobal(count * 2);
            Marshal.Copy(samples, offset, data, count);
            var h = new WaveHdr { Data = data, Length = count * 2 };
            IntPtr hp = Marshal.AllocHGlobal(HdrSize);
            Marshal.StructureToPtr(h, hp, false);
            waveOutPrepareHeader(dev, hp, HdrSize);
            if (waveOutWrite(dev, hp, HdrSize) != 0) { Free(hp); return; }
            queued.Add(hp);
        }

        public void Reset()
        {
            if (dev == IntPtr.Zero) return;
            waveOutReset(dev);
            Cleanup(true);
        }

        /// Number of buffers queued and not yet played.
        public int Pending { get { Cleanup(false); return queued.Count; } }

        void Cleanup(bool all)
        {
            for (int i = queued.Count - 1; i >= 0; i--)
            {
                if (all || (Marshal.ReadInt32(queued[i], FlagsOffset) & 1) != 0)   // WHDR_DONE
                {
                    Free(queued[i]);
                    queued.RemoveAt(i);
                }
            }
        }

        void Free(IntPtr hp)
        {
            waveOutUnprepareHeader(dev, hp, HdrSize);
            var h = (WaveHdr)Marshal.PtrToStructure(hp, typeof(WaveHdr));
            Marshal.FreeHGlobal(h.Data);
            Marshal.FreeHGlobal(hp);
        }

        public void Dispose()
        {
            if (dev == IntPtr.Zero) return;
            Reset();
            waveOutClose(dev);
            dev = IntPtr.Zero;
        }
    }

    /// Multi-track audio. Every audio track (e.g. OBS game + mic) is decoded to 48 kHz stereo PCM up front and
    /// mixed live at the mixer's per-track Volumes: playback is fed in 50 ms chunks, so volume changes are heard
    /// within ~0.2 s. Also NLE-style scrub audio: speed and direction follow the drag, and a short snippet plays
    /// on each frame step. (Unlike the macOS version, fast scrubbing changes pitch, like tape.)
    class Audio : IDisposable
    {
        readonly short[][] tracks;   // interleaved stereo, one array per audio track
        public readonly float[] Volumes;   // per track, 1 = unchanged; set by the mixer (UI thread)
        readonly int frames;         // length of the longest track
        readonly WaveOut wave = new WaveOut();
        readonly object gate = new object();   // guards wave and the playback position
        readonly bool ok;
        double? lastT;
        double lastWall;
        readonly Stopwatch clock = Stopwatch.StartNew();
        const double MaxSpeed = 4.0, MinSpeed = 0.25;
        const int Chunk = 2400, Ahead = 4;   // 50 ms chunks, ~200 ms queued ahead
        int playPos, playEnd;
        bool feeding, disposed;
        readonly AutoResetEvent feed = new AutoResetEvent(false);

        public int TrackCount { get { return tracks.Length; } }

        Audio(short[][] tracks)
        {
            this.tracks = tracks;
            Volumes = Enumerable.Repeat(1f, tracks.Length).ToArray();
            frames = tracks.Max(t => t.Length / 2);
            ok = wave.Open();
            if (ok) new Thread(Feeder) { IsBackground = true, Name = "audio" }.Start();
        }

        public static Audio Load(string ffmpeg, string file, int count, double duration)
        {
            if (count < 1 || duration > 30 * 60) return null;   // keep memory sane (~11 MB per minute per track)
            var tasks = Enumerable.Range(0, count).Select(i => Task.Run(() => LoadTrack(ffmpeg, file, i))).ToArray();
            Task.WaitAll(tasks);
            var got = tasks.Select(t => t.Result ?? new short[0]).ToArray();
            if (got.All(t => t.Length < 4)) return null;
            return new Audio(got);
        }

        static short[] LoadTrack(string ffmpeg, string file, int index)
        {
            try
            {
                var psi = Util.Hidden(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", file,
                    "-map", "0:a:" + index.ToString(Util.Inv), "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1" });
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardInput = false;
                using (var p = Process.Start(psi))
                using (var ms = new MemoryStream())
                {
                    Job.Add(p);
                    p.StandardOutput.BaseStream.CopyTo(ms);
                    p.WaitForExit();
                    var samples = new short[ms.Length / 2];
                    Buffer.BlockCopy(ms.GetBuffer(), 0, samples, 0, samples.Length * 2);
                    return samples;
                }
            }
            catch (Exception) { return null; }
        }

        double Now { get { return clock.Elapsed.TotalSeconds; } }
        int FrameOf(double t) { return (int)Math.Max(0, Math.Min(frames, t * WaveOut.Rate)); }

        /// Sum of all tracks at their volumes for n frames from frame a (unclipped, interleaved stereo).
        float[] MixFloat(int a, int n)
        {
            var acc = new float[n * 2];
            for (int k = 0; k < tracks.Length; k++)
            {
                float v = Volumes[k];
                if (v <= 0) continue;
                var t = tracks[k];
                int end = Math.Min(n * 2, t.Length - a * 2);   // shorter tracks are silent past their end
                for (int i = 0; i < end; i++) acc[i] += t[a * 2 + i] * v;
            }
            return acc;
        }

        short[] Mix(int a, int n)
        {
            var acc = MixFloat(a, n);
            var o = new short[acc.Length];
            for (int i = 0; i < o.Length; i++) o[i] = (short)Math.Max(-32768f, Math.Min(32767f, acc[i]));
            return o;
        }

        /// Peak level (1 = full scale) over `window` seconds ending at t, after volume: of track k, or of the
        /// mix when k is -1 (can exceed 1, meaning the mix clips).
        public double Peak(int k, double t, double window)
        {
            int b = FrameOf(t), a = FrameOf(t - window);
            if (b <= a) return 0;
            float peak = 0;
            if (k < 0)
            {
                foreach (float s in MixFloat(a, b - a)) peak = Math.Max(peak, Math.Abs(s));
                return peak / 32768;
            }
            var tr = tracks[k];
            int end = Math.Min(b * 2, tr.Length);
            for (int i = a * 2; i < end; i++) peak = Math.Max(peak, Math.Abs((float)tr[i]));
            return peak * Volumes[k] / 32768;
        }

        void Feeder()
        {
            while (true)
            {
                lock (gate)
                {
                    if (disposed) return;
                    while (feeding && playPos < playEnd && wave.Pending < Ahead)
                    {
                        int n = Math.Min(Chunk, playEnd - playPos);
                        var m = Mix(playPos, n);
                        wave.Write(m, 0, m.Length);
                        playPos += n;
                    }
                }
                feed.WaitOne(10);
            }
        }

        public void Play(double from, double to)
        {
            if (!ok) return;
            lock (gate)
            {
                wave.Reset();
                playPos = FrameOf(from);
                playEnd = FrameOf(to);
                feeding = true;
            }
            feed.Set();
        }

        public void Stop()
        {
            if (!ok) return;
            lock (gate) { feeding = false; wave.Reset(); }
        }

        public void ResetScrub() { lastT = null; }

        /// Marks t as the current scrub position so the next Scrub() continues from it.
        public void Prime(double t) { lastT = t; lastWall = Now; }

        /// Plays `length` seconds of the mix at normal speed from t (backwards when reversed).
        public void Snippet(double t, double length, bool reversed)
        {
            if (!ok) return;
            int a = FrameOf(t), n = FrameOf(t + length) - a;
            if (n < 2) return;
            var src = Mix(a, n);
            var o = new short[n * 2];
            int ramp = Math.Min(240, n / 4);   // ~5 ms fades
            for (int i = 0; i < n; i++)
            {
                int j = reversed ? n - 1 - i : i;
                double g = 1;
                if (ramp > 0) { if (i < ramp) g = (double)i / ramp; else if (i >= n - ramp) g = (double)(n - 1 - i) / ramp; }
                o[2 * i] = (short)(src[2 * j] * g);
                o[2 * i + 1] = (short)(src[2 * j + 1] * g);
            }
            lock (gate)
            {
                feeding = false;
                wave.Reset();
                wave.Write(o, 0, o.Length);
            }
        }

        /// Call on every scrub update with the new playhead time.
        public void Scrub(double t)
        {
            if (!ok) return;
            double now = Now;
            if (lastT == null)
            {
                lastT = t; lastWall = now;
                Snippet(t, 0.06, false);
                return;
            }
            double wall = now - lastWall;
            if (wall < 0.008) return;   // accumulate tiny updates
            double delta = t - lastT.Value;
            lastT = t; lastWall = now;
            if (Math.Abs(delta) <= 1e-4) return;
            double w = Math.Min(wall, 0.1);
            double speed = Math.Min(Math.Max(Math.Abs(delta) / w, MinSpeed), MaxSpeed);
            double len = speed * w;
            double start = delta > 0 ? t - len : t;
            int a = FrameOf(start), n = FrameOf(start + len) - a;
            int m = (int)(w * WaveOut.Rate);   // output frames: the chunk plays in w seconds
            if (n < 2 || m < 2) return;
            var src = Mix(a, n);
            var o = new short[m * 2];
            for (int i = 0; i < m; i++)
            {
                double pos = (double)i * (n - 1) / (m - 1);
                if (delta < 0) pos = n - 1 - pos;
                int i0 = (int)pos, i1 = Math.Min(i0 + 1, n - 1);
                double f = pos - i0;
                for (int c = 0; c < 2; c++)
                    o[2 * i + c] = (short)(src[2 * i0 + c] * (1 - f) + src[2 * i1 + c] * f);
            }
            lock (gate)
            {
                feeding = false;
                wave.Write(o, 0, o.Length);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                feeding = false;
                wave.Dispose();
            }
            feed.Set();
        }
    }

    // MARK: - VU meter

    /// Horizontal peak meter from -60 to 0 dBFS: green, yellow above -12 dB, red above -3 dB, with a peak-hold
    /// line. The right end lights up red when the level reaches full scale (the mix would clip).
    class VuMeter : FrameworkElement
    {
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static readonly Brush Back = new SolidColorBrush(Color.FromRgb(40, 40, 40)), Green = new SolidColorBrush(Color.FromRgb(60, 200, 90)),
            YellowB = new SolidColorBrush(Color.FromRgb(240, 200, 40)), Red = new SolidColorBrush(Color.FromRgb(235, 60, 50)),
            Hold = new SolidColorBrush(Color.FromRgb(230, 230, 230));
        double db = -90, holdDb = -90, holdAt, lastUpdate;

        public VuMeter() { Height = 10; VerticalAlignment = VerticalAlignment.Center; MinWidth = 80; }

        /// Feed the latest peak (1 = full scale) once per frame; the bar falls back smoothly.
        public void SetPeak(double peak)
        {
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(now - lastUpdate, 0.1);
            lastUpdate = now;
            double d = peak > 1e-5 ? 20 * Math.Log10(peak) : -90;
            double before = db, beforeHold = holdDb;
            db = Math.Max(d, db - 30 * dt);   // falls 30 dB per second
            if (d >= holdDb) { holdDb = d; holdAt = now; }
            else if (now - holdAt > 1.0) holdDb = Math.Max(d, holdDb - 30 * dt);
            if (before > -61 || db > -61 || beforeHold > -61) InvalidateVisual();
        }

        static double Frac(double d) { return Math.Max(0, Math.Min(1, (d + 60) / 60)); }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight, x = Frac(db) * W;
            dc.DrawRectangle(Back, null, new Rect(0, 0, W, H));
            double y1 = Frac(-12) * W, y2 = Frac(-3) * W;
            if (x > 0) dc.DrawRectangle(Green, null, new Rect(0, 0, Math.Min(x, y1), H));
            if (x > y1) dc.DrawRectangle(YellowB, null, new Rect(y1, 0, Math.Min(x, y2) - y1, H));
            if (x > y2) dc.DrawRectangle(Red, null, new Rect(y2, 0, x - y2, H));
            if (holdDb > -60) dc.DrawRectangle(Hold, null, new Rect(Math.Min(Frac(holdDb) * W, W - 2), 0, 2, H));
            if (holdDb >= -0.01) dc.DrawRectangle(Red, null, new Rect(W - 5, 0, 5, H));   // clipping
        }
    }

    // MARK: - Trim timeline

    class Timeline : FrameworkElement
    {
        readonly MainWindow e;
        const double HandleW = 12;
        enum Drag { None, Playhead, In, Out }
        Drag drag = Drag.None;
        double grabOffset;
        static readonly Brush Yellow = Brushes.Gold, Dim = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0));

        public Timeline(MainWindow e) { this.e = e; Height = 60; ClipToBounds = false; Cursor = Cursors.Arrow; }

        double TrackW { get { return Math.Max(ActualWidth - 2 * HandleW, 1); } }
        double Dur { get { return Math.Max(e.Duration, 0.001); } }
        double XOf(double t) { return HandleW + t / Dur * TrackW; }
        double TOf(double x) { return (x - HandleW) / TrackW * Dur; }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight, trackW = TrackW;
            double xIn = XOf(e.InT), xOut = XOf(e.OutT);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));   // hit-testable everywhere

            // Filmstrip
            var thumbs = e.Thumbs;
            int count = thumbs.Length;
            if (thumbs.All(t => t == null)) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(77, 128, 128, 128)), null, new Rect(HandleW, 0, trackW, H));
            for (int i = 0; i < count; i++)
            {
                var img = thumbs[i];
                var cell = new Rect(HandleW + trackW * i / count, 0, trackW / count, H);
                if (img == null) { dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(77, 128, 128, 128)), null, cell); continue; }
                double s = Math.Max(cell.Width / img.PixelWidth, cell.Height / img.PixelHeight);   // fill
                double iw = img.PixelWidth * s, ih = img.PixelHeight * s;
                dc.PushClip(new RectangleGeometry(cell));
                dc.DrawImage(img, new Rect(cell.X + (cell.Width - iw) / 2, cell.Y + (cell.Height - ih) / 2, iw, ih));
                dc.Pop();
            }

            // Dim outside the trimmed zone
            dc.DrawRectangle(Dim, null, new Rect(HandleW, 0, Math.Max(xIn - HandleW, 0), H));
            dc.DrawRectangle(Dim, null, new Rect(xOut, 0, Math.Max(W - HandleW - xOut, 0), H));

            // Zone border
            dc.DrawRectangle(null, new Pen(Yellow, 3), new Rect(xIn, 1.5, Math.Max(xOut - xIn, 1), Math.Max(H - 3, 1)));

            // Playhead
            double xp = XOf(e.Playhead);
            var shadow = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
            dc.DrawRectangle(shadow, null, new Rect(xp - 2, 0, 4, H));
            dc.DrawRectangle(Brushes.White, null, new Rect(xp - 1, 0, 2, H));
            dc.DrawEllipse(Brushes.White, new Pen(shadow, 1), new Point(xp, 2), 5, 5);

            // Handles
            DrawHandle(dc, new Rect(xIn - HandleW, 0, HandleW, H));
            DrawHandle(dc, new Rect(xOut, 0, HandleW, H));
        }

        static void DrawHandle(DrawingContext dc, Rect r)
        {
            dc.DrawRoundedRectangle(Yellow, null, r, 3, 3);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)), null,
                new Rect(r.X + r.Width / 2 - 1, r.Y + r.Height / 2 - 10, 2, 20), 1, 1);
        }

        Drag HitTest(double x)
        {
            double xIn = XOf(e.InT), xOut = XOf(e.OutT);
            if (x >= xIn - HandleW && x <= xIn) return Drag.In;
            if (x >= xOut && x <= xOut + HandleW) return Drag.Out;
            return Drag.Playhead;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs a)
        {
            if (!e.CanEdit) return;
            double x = a.GetPosition(this).X;
            drag = HitTest(x);
            grabOffset = drag == Drag.In ? XOf(e.InT) - x : drag == Drag.Out ? XOf(e.OutT) - x : 0;
            CaptureMouse();
            e.BeginDrag(drag == Drag.Playhead);
            Apply(x);
            a.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs a)
        {
            double x = a.GetPosition(this).X;
            if (drag == Drag.None)
            {
                var h = HitTest(x);
                Cursor = h == Drag.Playhead ? Cursors.Arrow : Cursors.SizeWE;
                return;
            }
            Apply(x);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs a)
        {
            if (drag == Drag.None) return;
            drag = Drag.None;
            ReleaseMouseCapture();
            e.EndDrag();
        }

        protected override void OnLostMouseCapture(MouseEventArgs a)
        {
            if (drag != Drag.None) { drag = Drag.None; e.EndDrag(); }
        }

        void Apply(double x)
        {
            switch (drag)
            {
                case Drag.Playhead: e.SetPlayhead(TOf(x)); break;
                case Drag.In: e.SetIn(TOf(x + grabOffset)); break;
                case Drag.Out: e.SetOut(TOf(x + grabOffset)); break;
            }
        }
    }

    // MARK: - Main window (model + view)

    class MainWindow : Window
    {
        public readonly string InputPath;
        readonly string ffmpeg, ffprobe;
        const double MinGap = 0.1;

        // Model
        public double Duration, InT, OutT, Playhead;
        public bool IsPlaying;
        public BitmapSource[] Thumbs = new BitmapSource[16];
        VideoInfo info;
        int rotation;
        double frameStep = 1.0 / 30;
        bool dragging;
        Phase phase = Phase.Editing;
        double progress;
        string resultMessage = "";
        Audio audio;
        double lastStepWall = -1;
        readonly Stopwatch wallClock = Stopwatch.StartNew();
        string lastDefaultW = "", lastDefaultH = "";

        // Video worker. Decoded frames go into a single "latest frame" slot that the UI picks up once per screen
        // refresh, so a fast decoder can never flood the UI thread and block clicks and keys.
        FrameReader reader;
        WriteableBitmap bitmap;
        readonly BlockingCollection<byte[]> pool = new BlockingCollection<byte[]>();
        readonly object workLock = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        double? pendingSeek;
        volatile bool playRequested;
        double playFrom;
        volatile bool closing;
        Thread worker;
        readonly object frameLock = new object();
        byte[] latestFrame;
        // Playback clock: the playhead (and audio) follow wall time, whatever the video decoder is doing
        Stopwatch playClock;
        double playClockFrom;
        int framesShown, framesDecoded;

        // Encoding
        Process encodeProc;
        bool cancelled;
        readonly StringBuilder stdoutLog = new StringBuilder();
        string stderrTail = "";

        // View
        readonly Border videoBox;
        readonly Image videoImage;
        readonly TextBlock videoMessage;
        readonly Timeline timeline;
        readonly Panel transport, settings;
        readonly Button playButton;
        readonly TextBlock timeLabel, estimateLabel;
        readonly TextBox inBox, outBox, sizeBox, widthBox, heightBox, outputBox;
        readonly ToggleButton[] rotateButtons = new ToggleButton[4];
        readonly CheckBox toneMapBox;
        readonly StackPanel mixerPanel;
        readonly List<Slider> mixerSliders = new List<Slider>();
        readonly List<VuMeter> meters = new List<VuMeter>();
        VuMeter mixMeter;
        readonly ContentControl bottom;

        public MainWindow(string input, string ffmpeg, string ffprobe)
        {
            InputPath = input;
            this.ffmpeg = ffmpeg;
            this.ffprobe = ffprobe;
            Title = Path.GetFileName(input);
            Width = 980; Height = 820; MinWidth = 760; MinHeight = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var root = new Grid { Margin = new Thickness(16) };
            for (int i = 0; i < 9; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            // Video
            videoImage = new Image { Stretch = Stretch.Uniform };
            // Linear is GPU-filtered; HighQuality (Fant) rescales on the CPU every time the frame changes
            RenderOptions.SetBitmapScalingMode(videoImage, BitmapScalingMode.Linear);
            videoMessage = new TextBlock { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20), Text = "Loading…" };
            var videoGrid = new Grid { Background = Brushes.Black, ClipToBounds = true };
            videoGrid.Children.Add(videoImage);
            videoGrid.Children.Add(videoMessage);
            // Click the video to focus it; the keyboard shortcuts then work (text fields lose focus).
            videoBox = new Border { Child = videoGrid, Background = Brushes.Black, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(6), MinHeight = 260, Focusable = true, FocusVisualStyle = null };
            videoBox.GotKeyboardFocus += delegate { videoBox.BorderBrush = SystemColors.HighlightBrush; };
            videoBox.LostKeyboardFocus += delegate { videoBox.BorderBrush = Brushes.Transparent; };
            videoBox.MouseLeftButtonDown += delegate { videoBox.Focus(); };
            Add(root, videoBox, 0);

            // Timeline
            timeline = new Timeline(this) { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
            Add(root, timeline, 1);

            // Transport
            var tr = new DockPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed, LastChildFill = false };
            playButton = Btn("▶", delegate { TogglePlay(); }, "Play/pause (Space)");
            playButton.MinWidth = 36;
            tr.Children.Add(playButton);
            tr.Children.Add(Btn("|◀", delegate { Step(-1); }, "Previous frame (←)"));
            tr.Children.Add(Btn("▶|", delegate { Step(1); }, "Next frame (→)"));
            timeLabel = new TextBlock { Width = 80, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
            tr.Children.Add(timeLabel);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            inBox = Field(85, "0:00.000");
            outBox = Field(85, "");
            inBox.PreviewKeyDown += (s, a) => { if (a.Key == Key.Enter) { CommitTime(true); a.Handled = true; } };
            outBox.PreviewKeyDown += (s, a) => { if (a.Key == Key.Enter) { CommitTime(false); a.Handled = true; } };
            inBox.LostKeyboardFocus += delegate { CommitTime(true); };
            outBox.LostKeyboardFocus += delegate { CommitTime(false); };
            right.Children.Add(Label("In"));
            right.Children.Add(inBox);
            right.Children.Add(Btn("Set In (I)", delegate { SetIn(Playhead); }, "Set in point at playhead (I)"));
            right.Children.Add(Label("Out"));
            right.Children.Add(outBox);
            right.Children.Add(Btn("Set Out (O)", delegate { SetOut(Playhead); }, "Set out point at playhead (O)"));
            tr.Children.Add(right);
            transport = tr;
            Add(root, tr, 2);

            // Settings
            var st = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var row1 = new WrapPanel();
            sizeBox = Field(60, Util.GetSetting("sizeText", "100"));
            widthBox = Field(70, "");
            heightBox = Field(70, "");
            foreach (var tb in new[] { sizeBox, widthBox, heightBox }) tb.TextChanged += delegate { UpdateEstimate(); };
            row1.Children.Add(Label("Target size"));
            row1.Children.Add(sizeBox);
            row1.Children.Add(Label("MB"));
            row1.Children.Add(Label("Width", 16));
            row1.Children.Add(widthBox);
            row1.Children.Add(Label("Height"));
            row1.Children.Add(heightBox);
            row1.Children.Add(Label("Rotate", 16));
            var seg = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 4; i++)
            {
                int deg = i * 90;
                var b = new ToggleButton { Content = deg + "°", Width = 50, Padding = new Thickness(0, 3, 0, 3), IsChecked = deg == 0, Focusable = false };
                b.Click += delegate { SetRotation(deg); };
                rotateButtons[i] = b;
                seg.Children.Add(b);
            }
            row1.Children.Add(seg);
            st.Children.Add(row1);
            mixerPanel = new StackPanel { Visibility = Visibility.Collapsed };
            st.Children.Add(mixerPanel);
            toneMapBox = new CheckBox { Content = "Convert HDR to SDR", Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
                ToolTip = "Off keeps HDR (10-bit HEVC). On makes SDR H.264 that looks right everywhere. Needs ffmpeg with zscale" };
            st.Children.Add(toneMapBox);
            var row3 = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            row3.Children.Add(Label("Output"));
            var choose = Btn("Choose…", delegate { ChooseOutput(); }, null);
            choose.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(choose, Dock.Right);
            row3.Children.Add(choose);
            outputBox = Field(0, Path.Combine(Path.GetDirectoryName(input), Path.GetFileNameWithoutExtension(input) + "_compressed.mp4"));
            outputBox.Width = double.NaN;
            row3.Children.Add(outputBox);
            st.Children.Add(row3);
            settings = st;
            Add(root, st, 3);

            estimateLabel = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
            Add(root, estimateLabel, 4);
            Add(root, new Separator { Margin = new Thickness(0, 12, 0, 12) }, 5);
            bottom = new ContentControl { Focusable = false };
            Add(root, bottom, 6);

            Content = root;
            UpdateBottom();
            PreviewKeyDown += OnKey;
            Loaded += delegate { videoBox.Focus(); Load(); };
            CompositionTarget.Rendering += OnRendering;
            Closing += OnClosing;
        }

        // Small view helpers
        static void Add(Grid g, UIElement el, int row) { Grid.SetRow(el, row); g.Children.Add(el); }
        static TextBlock Label(string text, double leftGap = 0)
        {
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6 + leftGap, 0, 6, 0) };
        }
        static TextBox Field(double width, string text)
        {
            var t = new TextBox { Text = text, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(2, 3, 2, 3) };
            if (width > 0) t.Width = width;
            return t;
        }
        static Button Btn(string text, RoutedEventHandler click, string tip)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), MinWidth = 32 };
            b.Click += click;
            if (tip != null) b.ToolTip = tip;
            return b;
        }

        public bool CanEdit { get { return phase == Phase.Editing && Duration > 0; } }

        // MARK: Loading

        async void Load()
        {
            string path = InputPath;
            info = await Task.Run(() => VideoInfo.Probe(ffprobe, ffmpeg, path));
            if (info == null || info.Duration <= 0 || info.Width <= 0 || info.Height <= 0)
            {
                videoMessage.Text = "Can't read this video for preview. It will still be compressed (whole clip).";
                Duration = 0;
                UpdateEstimate();
                return;
            }
            Util.Log(string.Format(Util.Inv, "{0}: {1:0.00}s {2}x{3} {4:0.##}fps trc={5} audio={6} zscale={7}",
                path, info.Duration, info.Width, info.Height, info.Fps, info.ColorTrc, info.HasAudio, info.HasZscale));
            Duration = info.Duration;
            InT = 0; OutT = Duration;
            frameStep = 1.0 / Math.Min(info.Fps, 60);

            // Preview frames: the displayed size scaled to fit 1280x1280, even dimensions
            double s = Math.Min(1.0, 1280.0 / Math.Max(info.Width, info.Height));
            int pw = Math.Max(2, (int)(info.Width * s / 2) * 2), ph = Math.Max(2, (int)(info.Height * s / 2) * 2);
            reader = new FrameReader(ffmpeg, path, info, pw, ph, Math.Min(info.Fps, 60));
            bitmap = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgr32, null);
            videoImage.Source = bitmap;
            for (int i = 0; i < 3; i++) pool.Add(new byte[reader.FrameBytes]);
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "video" };
            worker.Start();

            var dims = DefaultDims(0);
            widthBox.Text = dims[0]; heightBox.Text = dims[1];
            lastDefaultW = dims[0]; lastDefaultH = dims[1];
            if (info.IsHDR) toneMapBox.Visibility = Visibility.Visible;
            if (info.HasAudio) BuildMixer(info.AudioTracks);
            videoMessage.Text = info.IsHDR && !info.HasZscale ? "HDR preview is not tone-mapped (this ffmpeg has no zscale), so colours look washed out here." : "";
            videoMessage.VerticalAlignment = info.IsHDR && !info.HasZscale ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            timeline.Visibility = Visibility.Visible;
            transport.Visibility = Visibility.Visible;
            inBox.Text = Util.FmtTime(0);
            outBox.Text = Util.FmtTime(Duration);
            SetPlayhead(0);
            Refresh();
            Util.Log("preview ready");
            SelfTest();

            if (info.HasAudio)
            {
                double d = Duration;
                int count = info.AudioTracks.Count;
                audio = await Task.Run(() => Audio.Load(ffmpeg, path, count, d));
                if (audio != null) for (int i = 0; i < audio.TrackCount && i < mixerSliders.Count; i++) audio.Volumes[i] = (float)mixerSliders[i].Value;
                if (closing && audio != null) audio.Dispose();
            }
            GenerateThumbs(pw, ph);
        }

        /// Diagnostics: COMPRESSVIDEO_SELFTEST=<seconds> plays from the start while thumbnails load, logs how
        /// smoothly the UI ran, then closes. No input is simulated.
        async void SelfTest()
        {
            double secs;
            if (!double.TryParse(Environment.GetEnvironmentVariable("COMPRESSVIDEO_SELFTEST") ?? "", NumberStyles.Float, Util.Inv, out secs)) return;
            await Task.Delay(300);
            TogglePlay();
            var sw = Stopwatch.StartNew();
            double worstGap = 0, last = 0;
            int ticks = 0;
            EventHandler tick = (s, a) => { double t = sw.Elapsed.TotalSeconds; worstGap = Math.Max(worstGap, t - last); last = t; ticks++; };
            CompositionTarget.Rendering += tick;
            await Task.Delay(TimeSpan.FromSeconds(secs));
            CompositionTarget.Rendering -= tick;
            Util.Log(string.Format(Util.Inv, "selftest: playhead {0:0.00}s after {1:0.00}s, UI refreshes {2} ({3:0} per s), longest UI stall {4:0} ms",
                Playhead, sw.Elapsed.TotalSeconds, ticks, ticks / sw.Elapsed.TotalSeconds, worstGap * 1000));
            Pause();
            await Task.Delay(500);
            Close();
        }

        void GenerateThumbs(int pw, int ph)
        {
            int n = Thumbs.Length;
            double s = 90.0 / ph;
            int tw = Math.Max(2, (int)(pw * s / 2) * 2), th = 90;
            string path = InputPath;
            var vi = info;
            var gate = new SemaphoreSlim(2);   // with Light readers: leaves the CPU to the preview and the UI
            var sw = Stopwatch.StartNew();
            int remaining = n;
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                double t = Duration * (i + 0.5) / n;
                Task.Run(() =>
                {
                    gate.Wait();
                    try
                    {
                        if (closing) return;
                        using (var r = new FrameReader(ffmpeg, path, vi, tw, th, 0) { Light = true })
                        {
                            r.Open(t, 1);
                            var buf = new byte[r.FrameBytes];
                            if (!r.Read(buf)) return;
                            var bmp = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgr32, null, buf, tw * 4);
                            bmp.Freeze();
                            Dispatcher.BeginInvoke(new Action(() => { Thumbs[idx] = bmp; timeline.InvalidateVisual(); }));
                        }
                    }
                    finally
                    {
                        gate.Release();
                        if (Interlocked.Decrement(ref remaining) == 0) Util.Log(string.Format(Util.Inv, "thumbnails done in {0:0.00}s", sw.Elapsed.TotalSeconds));
                    }
                });
            }
        }

        /// Audio mixer: per track a volume slider (0-200%, remembered per track number) and a VU meter, plus a
        /// meter for the mix when there are several tracks. The output gets one audio track with this mix.
        void BuildMixer(List<string> titles)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int rows = titles.Count + (titles.Count > 1 ? 1 : 0);
            for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            for (int i = 0; i < titles.Count; i++)
            {
                int k = i;
                double saved;
                if (!double.TryParse(Util.GetSetting("volume" + k, "1"), NumberStyles.Float, Util.Inv, out saved)) saved = 1;
                var name = Label("Track " + (k + 1) + (titles[k].Length > 0 ? ": " + titles[k] : ""));
                var slider = new Slider { Minimum = 0, Maximum = 2, Value = Math.Max(0, Math.Min(2, saved)), SmallChange = 0.05, LargeChange = 0.1,
                    IsMoveToPointEnabled = true, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Volume of this track in the mix (double-click: 100%)" };
                var pct = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
                var meter = new VuMeter();
                pct.Text = Math.Round(slider.Value * 100) + "%";
                slider.ValueChanged += (s, a) =>
                {
                    pct.Text = Math.Round(a.NewValue * 100) + "%";
                    if (audio != null && k < audio.TrackCount) audio.Volumes[k] = (float)a.NewValue;
                };
                slider.MouseDoubleClick += delegate { slider.Value = 1; };
                Place(g, name, k, 0); Place(g, slider, k, 1); Place(g, pct, k, 2); Place(g, meter, k, 3);
                mixerSliders.Add(slider);
                meters.Add(meter);
            }
            if (titles.Count > 1)
            {
                var name = Label("Mix");
                name.FontWeight = FontWeights.SemiBold;
                mixMeter = new VuMeter { ToolTip = "Level of the mixed audio that goes into the output. Red at the right end: it clips" };
                Place(g, name, titles.Count, 0);
                Place(g, mixMeter, titles.Count, 3);
            }
            mixerPanel.Children.Add(g);
            mixerPanel.Visibility = Visibility.Visible;
            // Make room for the mixer rows without squeezing the video
            double extra = rows * 26 + 6;
            MinHeight += extra;
            double h = Math.Min(Height + extra, SystemParameters.WorkArea.Height);
            Top = Math.Max(SystemParameters.WorkArea.Top, Top - (h - Height) / 2);
            Height = h;
        }

        static void Place(Grid g, UIElement el, int row, int col) { Grid.SetRow(el, row); Grid.SetColumn(el, col); g.Children.Add(el); }

        /// 1080p for the output orientation: the short side is 1080 and the long side keeps the video's aspect
        /// ratio (1920 for 16:9). Empty (= auto) when the video size is unknown.
        string[] DefaultDims(int rot)
        {
            if (info == null || info.Width <= 0 || info.Height <= 0) return new[] { "", "" };
            double w = info.Width, h = info.Height;
            if (rot == 90 || rot == 270) { double t = w; w = h; h = t; }
            int longSide = (int)Math.Round(1080 * Math.Max(w, h) / Math.Min(w, h) / 2) * 2;
            return h > w ? new[] { "1080", longSide.ToString(Util.Inv) } : new[] { longSide.ToString(Util.Inv), "1080" };
        }

        void SetRotation(int deg)
        {
            rotation = deg;
            for (int i = 0; i < 4; i++) rotateButtons[i].IsChecked = i * 90 == deg;
            videoImage.LayoutTransform = new RotateTransform(deg);   // positive = clockwise, like the script
            // Follow the rotation with the prefilled size unless the user has edited the fields
            if (widthBox.Text == lastDefaultW && heightBox.Text == lastDefaultH)
            {
                var d = DefaultDims(deg);
                widthBox.Text = d[0]; heightBox.Text = d[1];
                lastDefaultW = d[0]; lastDefaultH = d[1];
            }
        }

        // MARK: Video worker thread

        void WorkerLoop()
        {
            while (!closing)
            {
                wake.WaitOne();
                while (!closing)
                {
                    double? seekT = null;
                    bool play = false;
                    lock (workLock)
                    {
                        if (playRequested) play = true;
                        else if (pendingSeek.HasValue) { seekT = pendingSeek; pendingSeek = null; }
                    }
                    if (play) PlayLoop();
                    else if (seekT.HasValue) ShowFrameAt(seekT.Value);
                    else break;
                }
            }
            if (reader != null) reader.Dispose();
        }

        void ShowFrameAt(double t)
        {
            byte[] buf;
            if (!pool.TryTake(out buf, 1000)) return;
            if (reader.ReadAt(t, buf)) Show(buf);
            else pool.Add(buf);
        }

        /// Decodes from `from` and shows frames on time. The playhead and audio run on the UI's clock
        /// (OnRendering), so they keep going even if the decoder is slow; frames that come in late are still
        /// shown (the picture lags rather than freezing), and if it falls well behind it restarts further ahead.
        void PlayLoop()
        {
            double from, outT;
            lock (workLock) { from = playFrom; outT = OutT; pendingSeek = null; }
            var opened = Stopwatch.StartNew();
            if (double.IsNaN(reader.NextTime) || Math.Abs(reader.NextTime - from) > 0.5 / reader.Fps) reader.Open(from, 0);
            double startup = 0.5;   // seconds from starting ffmpeg to its first frame; measured below
            bool first = true;
            double lastResync = 0;
            int late = 0;
            while (playRequested && !closing)
            {
                double ft = reader.NextTime;
                if (double.IsNaN(ft) || ft >= outT) break;   // out of video: the clock ends playback at the out point
                byte[] buf;
                if (!pool.TryTake(out buf, 100)) continue;
                if (!reader.Read(buf)) { pool.Add(buf); break; }
                framesDecoded++;
                if (first)
                {
                    // First frame is ready: start the clock and the audio together, so they line up with the video
                    first = false;
                    startup = Math.Max(startup, opened.Elapsed.TotalSeconds);
                    Show(buf);
                    Dispatcher.Invoke(new Action(() => StartClock(from)));
                    continue;
                }
                double now = PlayTime();
                if (double.IsNaN(now)) { pool.Add(buf); break; }
                if (ft > now + 0.002) Thread.Sleep((int)Math.Min(250, (ft - now) * 1000));
                Show(buf);
                if (now - ft > 1.0 && now - lastResync > 3)
                {
                    // Well behind: restart ahead of the clock by the decoder's startup time
                    late++;
                    lastResync = now;
                    opened.Restart();
                    reader.Open(Math.Min(now + startup * 1.5, outT), 0);
                    byte[] b2;
                    if (pool.TryTake(out b2, 1000))
                    {
                        if (reader.Read(b2)) { Show(b2); startup = Math.Max(startup, opened.Elapsed.TotalSeconds); }
                        else pool.Add(b2);
                    }
                }
            }
            Util.Log(string.Format(Util.Inv, "play from {0:0.00}: decoded {1}, shown {2}, resyncs {3}, decoder startup {4:0.00}s",
                from, framesDecoded, framesShown, late, startup));
            framesDecoded = 0; framesShown = 0;
            // Wait here until paused, so the worker doesn't start playing again by itself
            while (playRequested && !closing) wake.WaitOne(100);
        }

        /// Current playback time, or NaN when not playing (thread-safe snapshot).
        double PlayTime()
        {
            var c = playClock;
            return c == null ? double.NaN : playClockFrom + c.Elapsed.TotalSeconds;
        }

        /// Puts a decoded frame in the "latest frame" slot; the UI copies it to the screen on its next refresh.
        void Show(byte[] buf)
        {
            byte[] old;
            lock (frameLock) { old = latestFrame; latestFrame = buf; }
            if (old != null) pool.Add(old);   // replaced before it was drawn
        }

        /// Runs once per screen refresh on the UI thread: draws the newest frame and moves the playhead.
        void OnRendering(object sender, EventArgs a)
        {
            byte[] buf;
            lock (frameLock) { buf = latestFrame; latestFrame = null; }
            if (buf != null)
            {
                bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), buf, bitmap.PixelWidth * 4, 0);
                pool.Add(buf);
                if (IsPlaying) framesShown++;
            }
            // VU meters follow the playback clock (the same clock the audio was started on)
            if (meters.Count > 0)
            {
                double now = IsPlaying ? PlayTime() : double.NaN;
                bool live = audio != null && !double.IsNaN(now);
                for (int i = 0; i < meters.Count; i++) meters[i].SetPeak(live && i < audio.TrackCount ? audio.Peak(i, now, 0.05) : 0);
                if (mixMeter != null) mixMeter.SetPeak(live ? audio.Peak(-1, now, 0.05) : 0);
            }
            if (IsPlaying && playClock != null)
            {
                double t = PlayTime();
                if (t >= OutT) { Pause(); SetPlayhead(OutT); return; }
                if (!dragging && Math.Abs(t - Playhead) > 0.004) { Playhead = Math.Max(t, InT); Refresh(); }
            }
        }

        void StartClock(double from)
        {
            if (!IsPlaying || !playRequested) return;
            if (audio != null) audio.Play(from, OutT);
            playClockFrom = from;
            playClock = Stopwatch.StartNew();
        }

        // MARK: Playback / seeking

        void Seek(double t)
        {
            lock (workLock) { pendingSeek = t; }
            wake.Set();
        }

        public void Pause()
        {
            bool wasPlaying = IsPlaying;
            playRequested = false;
            IsPlaying = false;
            playClock = null;
            if (audio != null) audio.Stop();
            wake.Set();
            // Show exactly the frame under the playhead (the last one shown may have been a little behind)
            if (wasPlaying && reader != null) Seek(Playhead);
            Refresh();
        }

        public void SetPlayhead(double t)
        {
            Playhead = Math.Min(Math.Max(t, InT), OutT);
            if (reader != null) Seek(Playhead);
            if (dragging && audio != null) audio.Scrub(Playhead);   // audible scrubbing while dragging
            Refresh();
        }

        public void SetIn(double t)
        {
            InT = Math.Min(Math.Max(0, t), OutT - MinGap);
            inBox.Text = Util.FmtTime(InT);
            SetPlayhead(InT);
        }

        public void SetOut(double t)
        {
            OutT = Math.Max(Math.Min(Duration, t), InT + MinGap);
            outBox.Text = Util.FmtTime(OutT);
            SetPlayhead(OutT);
        }

        bool resumeAfterDrag;

        /// Moving the playhead while playing pauses during the click/drag (for scrubbing) and resumes from the
        /// new point on release. Dragging an in/out handle just pauses.
        public void BeginDrag(bool playhead)
        {
            resumeAfterDrag = playhead && IsPlaying;
            if (IsPlaying) Pause();
            dragging = true;
            videoBox.Focus();   // text fields lose focus, keys work
        }

        public void EndDrag()
        {
            dragging = false;
            if (audio != null) audio.ResetScrub();
            if (resumeAfterDrag) { resumeAfterDrag = false; TogglePlay(); }
        }

        void TogglePlay()
        {
            if (!CanEdit) return;
            if (IsPlaying) { Pause(); return; }
            if (Playhead >= OutT - 0.03 || Playhead < InT) SetPlayhead(InT);
            lock (workLock) { playFrom = Playhead; pendingSeek = null; }
            playRequested = true;
            IsPlaying = true;
            wake.Set();
            Refresh();
        }

        void Step(int frames)
        {
            if (!CanEdit) return;
            Pause();
            double before = Playhead;
            SetPlayhead(Playhead + frames * frameStep);
            if (Playhead == before || audio == null) return;
            double now = wallClock.Elapsed.TotalSeconds;
            if (now - lastStepWall < 0.25)
            {
                // Held key (key repeat): continuous scrub audio instead of restarting a snippet each time,
                // which would cut off the previous one and sound choppy
                audio.Scrub(Playhead);
            }
            else
            {
                // Single step: short audio snippet for the frame we landed on (reversed when stepping back)
                audio.Snippet(Playhead, Math.Max(frameStep * 4, 0.12), frames < 0);
                audio.Prime(Playhead);
            }
            lastStepWall = now;
        }

        void CommitTime(bool isIn)
        {
            if (Duration <= 0) return;
            double? t = Util.ParseTime(isIn ? inBox.Text : outBox.Text);
            if (t.HasValue && Math.Abs(t.Value - (isIn ? InT : OutT)) > 0.0005) { if (isIn) SetIn(t.Value); else SetOut(t.Value); }
            inBox.Text = Util.FmtTime(InT);
            outBox.Text = Util.FmtTime(OutT);
        }

        void OnKey(object sender, KeyEventArgs a)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            if (!CanEdit) return;
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
            switch (a.Key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Left: Step(-1); break;
                case Key.Right: Step(1); break;
                case Key.I: SetIn(Playhead); break;
                case Key.O: SetOut(Playhead); break;
                default: return;
            }
            a.Handled = true;
        }

        void Refresh()
        {
            timeLabel.Text = Util.FmtTime(Playhead);
            playButton.Content = IsPlaying ? "❚❚" : "▶";
            timeline.InvalidateVisual();
            UpdateEstimate();
        }

        void UpdateEstimate()
        {
            if (estimateLabel == null) return;
            double mb;
            if (Duration <= 0 || !double.TryParse(sizeBox.Text, NumberStyles.Float, Util.Inv, out mb) || mb <= 0) { estimateLabel.Text = ""; return; }
            double clip = Math.Max(OutT - InT, 0.001);
            double kbps = (mb * 0.95 * 8 * 1024 * 1024 / clip - 128000) / 1000;
            string s = "Clip length " + Util.FmtTime(clip) + " — video bitrate ≈ " + Math.Max(kbps, 0).ToString("0", Util.Inv) + " kbps";
            if (kbps < 500) s += " (below 500 kbps: output will exceed the target size)";
            estimateLabel.Text = s;
        }

        // MARK: Compression

        string ScriptPath()
        {
            // Walk up from the executable (gui\CompressVideo.exe) to find the script
            const string name = "compress-video-to-100mb-ffmpeg.ps1";
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string c = Path.Combine(dir, name);
                if (File.Exists(c)) return c;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }
            return name;
        }

        void ShowError(string msg) { formError = msg; UpdateBottom(); }
        string formError;

        void Compress()
        {
            formError = null;
            double mb;
            if (!double.TryParse(sizeBox.Text, NumberStyles.Float, Util.Inv, out mb) || mb <= 0) { ShowError("Target size must be a positive number."); return; }
            foreach (var pair in new[] { Tuple.Create("Width", widthBox.Text.Trim()), Tuple.Create("Height", heightBox.Text.Trim()) })
            {
                int v;
                if (pair.Item2.Length > 0 && (!int.TryParse(pair.Item2, NumberStyles.None, Util.Inv, out v) || v <= 0))
                {
                    ShowError(pair.Item1 + " must be a positive whole number (or empty).");
                    return;
                }
            }
            string output = outputBox.Text.Trim();
            string fullOut;
            try { fullOut = Path.GetFullPath(output); } catch (Exception) { ShowError("The output path is not valid."); return; }
            if (output.Length == 0 || string.Equals(fullOut, Path.GetFullPath(InputPath), StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Choose an output file different from the input.");
                return;
            }
            if (File.Exists(fullOut))
            {
                if (MessageBox.Show(this, fullOut, "Replace existing file?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                try { File.Delete(fullOut); } catch (Exception ex) { ShowError("Can't replace the file: " + ex.Message); return; }
            }
            outputBox.Text = fullOut;
            Util.SetSetting("sizeText", sizeBox.Text.Trim());

            var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
                "-InputFile", InputPath, "-TargetSizeMB", mb.ToString(Util.Inv), "-Rotation", rotation.ToString(Util.Inv), "-OutputFile", fullOut };
            if (widthBox.Text.Trim().Length > 0) { args.Add("-OutputWidth"); args.Add(widthBox.Text.Trim()); }
            if (heightBox.Text.Trim().Length > 0) { args.Add("-OutputHeight"); args.Add(heightBox.Text.Trim()); }
            if (info != null && info.IsHDR && toneMapBox.IsChecked == true) args.Add("-ToneMapToSDR");
            if (mixerSliders.Count > 0)
            {
                args.Add("-AudioVolumes");
                args.Add(string.Join(",", mixerSliders.Select(s => s.Value.ToString("0.###", Util.Inv))));
                for (int i = 0; i < mixerSliders.Count; i++) Util.SetSetting("volume" + i, mixerSliders[i].Value.ToString("0.###", Util.Inv));
            }
            double clipLen = Duration;
            if (Duration > 0)
            {
                if (InT > 0.001) { args.Add("-StartTime"); args.Add(InT.ToString("0.000", Util.Inv)); }
                if (OutT < Duration - 0.001) { args.Add("-EndTime"); args.Add(OutT.ToString("0.000", Util.Inv)); }
                clipLen = OutT - InT;
            }

            Pause();
            cancelled = false;
            stdoutLog.Clear();
            stderrTail = "";
            progress = 0;
            SetPhase(Phase.Encoding);

            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = Util.Hidden(File.Exists(ps) ? ps : "powershell.exe", args);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            Process p;
            try
            {
                p = Process.Start(psi);
                Job.Add(p);
                p.StandardInput.Close();
            }
            catch (Exception ex)
            {
                resultMessage = "Could not start the compression script: " + ex.Message;
                SetPhase(Phase.Failed);
                return;
            }
            encodeProc = p;
            var outDone = new ManualResetEvent(false);
            var errDone = new ManualResetEvent(false);
            StartReader(p.StandardOutput, false, clipLen, outDone);
            StartReader(p.StandardError, true, clipLen, errDone);
            new Thread(() =>
            {
                p.WaitForExit();
                outDone.WaitOne(3000);
                errDone.WaitOne(3000);
                int code = p.ExitCode;
                Dispatcher.BeginInvoke(new Action(() => Finished(code, fullOut)));
            }) { IsBackground = true }.Start();
        }

        void StartReader(StreamReader r, bool isErr, double clipLen, ManualResetEvent done)
        {
            new Thread(() =>
            {
                var buf = new char[4096];
                try
                {
                    int n;
                    while ((n = r.Read(buf, 0, buf.Length)) > 0)
                    {
                        string text = new string(buf, 0, n);
                        Dispatcher.BeginInvoke(new Action(() => Consume(text, isErr, clipLen)));
                    }
                }
                catch (Exception) { }
                done.Set();
            }) { IsBackground = true }.Start();
        }

        static readonly Regex TimeRe = new Regex(@"time=(\d+):(\d\d):(\d\d(?:\.\d+)?)");

        void Consume(string text, bool isErr, double clipLen)
        {
            if (isErr)
            {
                stderrTail = stderrTail + text;
                if (stderrTail.Length > 4000) stderrTail = stderrTail.Substring(stderrTail.Length - 4000);
                var ms = TimeRe.Matches(stderrTail);
                if (clipLen > 0 && ms.Count > 0)
                {
                    var m = ms[ms.Count - 1];
                    double h = double.Parse(m.Groups[1].Value, Util.Inv), mi = double.Parse(m.Groups[2].Value, Util.Inv), s = double.Parse(m.Groups[3].Value, Util.Inv);
                    progress = Math.Min(1, (h * 3600 + mi * 60 + s) / clipLen);
                    UpdateProgress();
                }
            }
            else stdoutLog.Append(text);
        }

        void Finished(int status, string output)
        {
            encodeProc = null;
            if (cancelled)
            {
                TryDelete(output);
                if (!closing) SetPhase(Phase.Editing);
                return;
            }
            var lines = stdoutLog.ToString().Replace("\r", "").Split('\n');
            if (status == 0 && File.Exists(output))
            {
                long bytes = new FileInfo(output).Length;
                string msg = "Saved " + (bytes / 1024.0 / 1024).ToString("0.00", Util.Inv) + " MB to " + output;
                var warnings = lines.Where(l => l.StartsWith("Warning")).ToArray();
                if (warnings.Length > 0) msg += "\n" + string.Join("\n", warnings);
                progress = 1;
                resultMessage = msg;
                SetPhase(Phase.Done);
            }
            else
            {
                TryDelete(output);
                var errors = lines.Where(l => l.StartsWith("Error")).ToArray();
                string detail = errors.Length == 0
                    ? string.Join("\n", stderrTail.Replace("\r", "\n").Split('\n').Where(l => l.Trim().Length > 0).Reverse().Take(6).Reverse())
                    : string.Join("\n", errors);
                resultMessage = detail.Length == 0 ? "Compression failed (exit code " + status + ")." : detail;
                SetPhase(Phase.Failed);
            }
        }

        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { } }

        void CancelEncoding()
        {
            if (encodeProc == null) return;
            cancelled = true;
            Util.KillTree(encodeProc);
        }

        void OnClosing(object sender, System.ComponentModel.CancelEventArgs a)
        {
            closing = true;
            playRequested = false;
            wake.Set();
            var p = encodeProc;
            if (p != null)
            {
                // The app exits right after this, so clean up the partial output here
                CancelEncoding();
                try { p.WaitForExit(3000); } catch (Exception) { }
                TryDelete(outputBox.Text);
            }
            if (audio != null) audio.Dispose();
        }

        // MARK: Bottom bar

        void SetPhase(Phase p)
        {
            phase = p;
            bool busy = p != Phase.Editing;
            transport.IsEnabled = !busy;
            settings.IsEnabled = !busy;
            timeline.IsHitTestVisible = !busy;
            UpdateBottom();
        }

        ProgressBar progressBar;
        TextBlock progressLabel;

        void UpdateProgress()
        {
            if (progressBar == null) return;
            progressBar.Value = progress;
            progressLabel.Text = (progress * 100).ToString("0", Util.Inv) + "%";
        }

        void UpdateBottom()
        {
            var dp = new DockPanel { LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(buttons, Dock.Right);
            dp.Children.Add(buttons);
            progressBar = null;
            switch (phase)
            {
                case Phase.Editing:
                    {
                        var cancel = Btn("Cancel", delegate { Close(); }, null);
                        cancel.IsCancel = true;
                        var go = Btn("Compress", delegate { Compress(); }, null);
                        go.IsDefault = true;
                        go.Margin = new Thickness(0);
                        buttons.Children.Add(cancel);
                        buttons.Children.Add(go);
                        if (formError != null) dp.Children.Add(new TextBlock { Text = formError, Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                        else dp.Children.Add(new TextBlock());
                        break;
                    }
                case Phase.Encoding:
                    {
                        var stop = Btn("Stop", delegate { CancelEncoding(); }, null);
                        stop.Margin = new Thickness(0);
                        buttons.Children.Add(stop);
                        progressLabel = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                        DockPanel.SetDock(progressLabel, Dock.Right);
                        dp.Children.Add(progressLabel);
                        progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 14, VerticalAlignment = VerticalAlignment.Center };
                        dp.Children.Add(progressBar);
                        UpdateProgress();
                        break;
                    }
                case Phase.Done:
                    {
                        string output = outputBox.Text;
                        buttons.Children.Add(Btn("Show in Explorer", delegate {
                            try { Process.Start("explorer.exe", "/select,\"" + output + "\""); } catch (Exception) { }
                        }, null));
                        // Back to the editor (trim/settings kept) to compress the same video again
                        buttons.Children.Add(Btn("Back", delegate { SetPhase(Phase.Editing); videoBox.Focus(); }, "Change settings and compress again"));
                        var done = Btn("Done", delegate { Close(); }, null);
                        done.IsDefault = true;
                        done.Margin = new Thickness(0);
                        buttons.Children.Add(done);
                        dp.Children.Add(Selectable(resultMessage, null));
                        Dispatcher.BeginInvoke(new Action(() => done.Focus()));
                        break;
                    }
                case Phase.Failed:
                    {
                        buttons.Children.Add(Btn("Back", delegate { SetPhase(Phase.Editing); }, null));
                        var quit = Btn("Quit", delegate { Close(); }, null);
                        quit.Margin = new Thickness(0);
                        buttons.Children.Add(quit);
                        dp.Children.Add(Selectable(resultMessage, Brushes.Red));
                        break;
                    }
            }
            bottom.Content = dp;
        }

        static TextBox Selectable(string text, Brush color)
        {
            var t = new TextBox { Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
            if (color != null) t.Foreground = color;
            return t;
        }

        void ChooseOutput()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "MPEG-4 video (*.mp4)|*.mp4", DefaultExt = ".mp4", OverwritePrompt = false };
            try
            {
                dlg.FileName = Path.GetFileName(outputBox.Text);
                dlg.InitialDirectory = Path.GetDirectoryName(outputBox.Text);
            }
            catch (Exception) { }
            if (dlg.ShowDialog(this) == true) outputBox.Text = dlg.FileName;
        }
    }

    // MARK: - App

    static class Program
    {
        [STAThread]
        static int Main(string[] argv)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string dir = Path.GetDirectoryName(exe);
            try { File.Delete(Path.Combine(dir, "CompressVideo.old.exe")); } catch (Exception) { }

            // Rebuild if the source is newer than this executable (like the macOS Quick Action does)
            string src = Path.Combine(dir, "CompressVideoGUI.cs"), build = Path.Combine(dir, "build.ps1");
            if (File.Exists(src) && File.Exists(build) && File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(exe)
                && Environment.GetEnvironmentVariable("COMPRESSVIDEO_REBUILT") == null)
            {
                try
                {
                    var b = Process.Start(Util.Hidden("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", build }));
                    b.WaitForExit();
                    if (b.ExitCode == 0 && File.Exists(exe))
                    {
                        var psi = new ProcessStartInfo(exe, Util.Args(argv)) { UseShellExecute = false };
                        psi.EnvironmentVariables["COMPRESSVIDEO_REBUILT"] = "1";
                        Process.Start(psi);
                        return 0;
                    }
                }
                catch (Exception) { }
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            string input = argv.Length > 0 ? argv[argv.Length - 1] : null;
            if (argv.Length >= 2 && argv[0].Equals("-InputFile", StringComparison.OrdinalIgnoreCase)) input = argv[1];
            if (input == null)
            {
                MessageBox.Show("Usage: CompressVideo.exe <video file>", "Compress Video");
                return 1;
            }
            if (!File.Exists(input))
            {
                MessageBox.Show("File not found:\n" + input, "Compress Video", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            input = Path.GetFullPath(input);

            Util.RefreshPath();
            string ffmpeg = Util.Which("ffmpeg.exe"), ffprobe = Util.Which("ffprobe.exe");
            if (ffmpeg == null || ffprobe == null)
            {
                MessageBox.Show("ffmpeg is not installed or not in PATH.\n\nInstall it with:\n    winget install Gyan.FFmpeg",
                    "Compress Video", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }

            // Several files selected in Explorer start one copy each: show their windows one at a time
            using (var turn = new Mutex(false, @"Local\CompressVideoGUI"))
            {
                try { turn.WaitOne(); } catch (AbandonedMutexException) { }
                try
                {
                    try { File.Delete(Util.LogPath); } catch (Exception) { }
                    Util.Log("ffmpeg: " + ffmpeg);
                    var w = new MainWindow(input, ffmpeg, ffprobe);
                    return app.Run(w);
                }
                finally { turn.ReleaseMutex(); }
            }
        }
    }
}
