using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageBattery
{
    internal static class Program
    {
        internal static readonly string Folder = AppDomain.CurrentDomain.BaseDirectory;
        internal static readonly string InstanceName = "Local\\CodexUsageBattery-v1-" + Environment.UserName;
        internal static string DataFile(string name)
        {
            string overrideDirectory = Environment.GetEnvironmentVariable("CODEX_USAGE_BATTERY_DATA_DIR");
            if (!String.IsNullOrWhiteSpace(overrideDirectory))
                return Path.Combine(Path.GetFullPath(overrideDirectory.Trim().Trim('"')), name);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(String.IsNullOrEmpty(local) ? Folder : Path.Combine(local, "CodexUsageBattery"), name);
        }
        internal static void WriteDataFile(string name, string content)
        {
            string path = DataFile(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content, Encoding.UTF8);
        }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Contains("--stop"))
                {
                    try { using (var signal = EventWaitHandle.OpenExisting(InstanceName + "-stop")) signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                if (args.Contains("--core-test")) { Verification.RunCore(); return 0; }
                if (args.Contains("--self-test")) { Verification.Run(); return 0; }
                if (args.Contains("--probe"))
                {
                    using (var client = new UsageClient())
                    {
                        var usage = client.ReadAsync().GetAwaiter().GetResult();
                        WriteDataFile("probe-result.json", Json.Encode(new {
                            primaryRemaining = usage.Primary == null ? (int?)null : usage.Primary.Remaining,
                            secondaryRemaining = usage.Secondary == null ? (int?)null : usage.Secondary.Remaining,
                            updatedAt = usage.UpdatedAt.ToString("o"),
                            windows = Native.FindCodexWindows().Select(w => new {
                                handle = w.Handle.ToInt64(), title = w.Title, dpi = Native.Dpi(w.Handle),
                                left = w.Bounds.Left, top = w.Bounds.Top,
                                width = w.Bounds.Width, height = w.Bounds.Height
                            }).ToArray()
                        }));
                    }
                    return 0;
                }
                bool created;
                using (var mutex = new Mutex(true, InstanceName, out created))
                {
                    if (!created) return 0;
                    using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, InstanceName + "-stop"))
                    {
                        Native.SetProcessDpiAware();
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        using (var form = new BatteryForm(stop)) Application.Run(form);
                    }
                    mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    string message = ex.GetType().Name + ": " + ex.Message;
                    if (args.Contains("--core-test") || args.Contains("--self-test"))
                        File.WriteAllText(Path.Combine(Folder, "last-error.txt"), message, Encoding.UTF8);
                    else WriteDataFile("last-error.txt", message);
                }
                catch { }
                return 1;
            }
        }
    }

    internal static class Json
    {
        internal static Dictionary<string, object> Object(object value) { return value as Dictionary<string, object>; }
        internal static object Get(Dictionary<string, object> obj, string key)
        { object value; return obj != null && obj.TryGetValue(key, out value) ? value : null; }
        internal static Dictionary<string, object> Parse(string value)
        { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(value); }
        internal static string Encode(object value) { return new JavaScriptSerializer().Serialize(value); }
    }

    internal sealed class Quota
    {
        internal int Remaining;
        internal long? ResetsAt;
        internal int? Minutes;
        internal static Quota Parse(object value)
        {
            var data = Json.Object(value);
            object used = Json.Get(data, "usedPercent");
            if (used == null || used is bool || used is string) return null;
            double percent;
            try { percent = Convert.ToDouble(used, CultureInfo.InvariantCulture); }
            catch { return null; }
            if (Double.IsNaN(percent) || Double.IsInfinity(percent)) return null;
            var quota = new Quota { Remaining = (int)Math.Floor(Math.Max(0, Math.Min(100, 100 - percent))) };
            try { if (Json.Get(data, "resetsAt") != null) quota.ResetsAt = Convert.ToInt64(data["resetsAt"]); } catch { }
            try { if (Json.Get(data, "windowDurationMins") != null) quota.Minutes = Convert.ToInt32(data["windowDurationMins"]); } catch { }
            return quota;
        }
        internal bool ResetPending
        { get { return ResetsAt.HasValue && DateTimeOffset.UtcNow.ToUnixTimeSecondsCompat() >= ResetsAt.Value; } }
        internal string Label(string fallback)
        {
            if (Minutes == 10080) return "每周";
            if (Minutes.HasValue && Minutes.Value % 60 == 0) return (Minutes.Value / 60) + " 小时";
            if (Minutes.HasValue) return Minutes + " 分钟";
            return fallback;
        }
        internal string Detail(string fallback)
        {
            string value = ResetPending ? "等待刷新" : Remaining + "%";
            string reset = ResetsAt.HasValue ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(ResetsAt.Value).ToLocalTime().ToString("MM-dd HH:mm") : "未知";
            return Label(fallback) + "剩余 " + value + "  ·  重置 " + reset;
        }
    }

    internal static class TimeCompat
    {
        internal static long ToUnixTimeSecondsCompat(this DateTimeOffset time)
        { return (long)(time.UtcDateTime - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
    }

    internal sealed class Usage
    {
        internal Quota Primary, Secondary;
        internal DateTime UpdatedAt;
        internal static Usage Parse(Dictionary<string, object> result)
        {
            var map = Json.Object(Json.Get(result, "rateLimitsByLimitId"));
            var rate = Json.Object(Json.Get(map, "codex"));
            if (rate == null)
            {
                var legacy = Json.Object(Json.Get(result, "rateLimits"));
                var id = Json.Get(legacy, "limitId") as string;
                if (id == null || id == "codex") rate = legacy;
            }
            if (rate == null) throw new InvalidDataException("未返回 Codex 额度");
            return new Usage {
                Primary = Quota.Parse(Json.Get(rate, "primary")),
                Secondary = Quota.Parse(Json.Get(rate, "secondary")),
                UpdatedAt = DateTime.UtcNow
            };
        }
    }

    internal sealed class UsageClient : IDisposable
    {
        private Process process;
        private int nextId;
        private readonly object gate = new object();
        private readonly SemaphoreSlim readLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending =
            new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private bool disposed;

        private static string FindCli()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_USAGE_BATTERY_CLI");
            if (!String.IsNullOrWhiteSpace(configured))
            {
                string candidate = configured.Trim().Trim('"');
                if (File.Exists(candidate) && String.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase))
                    return candidate;
                throw new FileNotFoundException("CODEX_USAGE_BATTERY_CLI 指向的 Codex EXE 不存在");
            }
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(root))
            {
                string best = Directory.GetDirectories(root).Select(d => Path.Combine(d, "codex.exe"))
                    .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (best != null) return best;
            }
            foreach (string part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string candidate = Path.Combine(part.Trim('"'), "codex.exe"); if (File.Exists(candidate)) return candidate; }
                catch { }
            }
            throw new FileNotFoundException("找不到 Codex 客户端，请先安装或打开 Codex");
        }

        private async Task ConnectAsync()
        {
            if (process != null && !process.HasExited) return;
            var info = new ProcessStartInfo(FindCli(), "app-server --stdio") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Program.Folder
            };
            if (String.IsNullOrEmpty(info.EnvironmentVariables["CODEX_HOME"]))
                info.EnvironmentVariables["CODEX_HOME"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += OnLine;
            // Drain stderr without storing tokens, raw responses, or account information.
            process.ErrorDataReceived += delegate { };
            process.Exited += delegate { FailPending(); };
            if (!process.Start()) throw new IOException("无法启动用量查询连接");
            process.StandardInput.AutoFlush = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await RequestAsync("initialize", new { clientInfo = new { name = "codex_usage_battery", title = "Codex Usage Battery", version = "1.3.2-preview" } }).ConfigureAwait(false);
            Send(new { method = "initialized", @params = new { } });
        }

        private void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) { FailPending(); return; }
            try
            {
                var data = Json.Parse(e.Data);
                if (Json.Get(data, "method") != null)
                {
                    if (Json.Get(data, "id") != null)
                        Send(new { id = data["id"], error = new { code = -32601, message = "Method not supported by usage indicator" } });
                    return;
                }
                object raw = Json.Get(data, "id");
                if (raw == null) return;
                int id = Convert.ToInt32(raw);
                TaskCompletionSource<Dictionary<string, object>> completion;
                lock (gate)
                {
                    if (!pending.TryGetValue(id, out completion)) return;
                    pending.Remove(id);
                }
                if (Json.Get(data, "error") != null) completion.TrySetException(new IOException("Codex 用量查询失败，请检查登录或网络"));
                else completion.TrySetResult(Json.Object(Json.Get(data, "result")));
            }
            catch { /* Ignore unrelated protocol messages. */ }
        }

        private void Send(object data)
        {
            lock (gate)
            {
                if (disposed || process == null || process.HasExited) throw new IOException("用量连接已关闭");
                process.StandardInput.WriteLine(Json.Encode(data));
            }
        }

        private async Task<Dictionary<string, object>> RequestAsync(string method, object parameters)
        {
            int id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<Dictionary<string, object>>();
            lock (gate) pending[id] = completion;
            try
            {
                Send(new { id = id, method = method, @params = parameters });
                if (await Task.WhenAny(completion.Task, Task.Delay(20000)).ConfigureAwait(false) != completion.Task)
                    throw new TimeoutException("用量查询超时");
                return await completion.Task.ConfigureAwait(false);
            }
            finally { lock (gate) pending.Remove(id); }
        }

        internal async Task<Usage> ReadAsync()
        {
            await readLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) throw new ObjectDisposedException("UsageClient");
                await ConnectAsync().ConfigureAwait(false);
                return Usage.Parse(await RequestAsync("account/rateLimits/read", new { }).ConfigureAwait(false));
            }
            catch { Disconnect(); throw; }
            finally { readLock.Release(); }
        }

        private void FailPending()
        {
            TaskCompletionSource<Dictionary<string, object>>[] all;
            lock (gate) { all = pending.Values.ToArray(); pending.Clear(); }
            foreach (var item in all) item.TrySetException(new IOException("用量连接已断开"));
        }
        private void Disconnect()
        {
            var old = process; process = null;
            if (old != null)
            {
                try { if (!old.HasExited) old.Kill(); } catch { }
                old.Dispose();
            }
            FailPending();
        }
        public void Dispose() { disposed = true; Disconnect(); }
    }

    internal sealed class WindowInfo
    {
        internal IntPtr Handle;
        internal Rectangle Bounds;
        internal string Title;
    }

    internal static class Native
    {
        internal delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr handle, out RECT rect);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr handle, int index);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr handle);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out RECT rect, int size);
        internal static void SetProcessDpiAware() { try { SetProcessDPIAware(); } catch { } }
        internal static int Dpi(IntPtr handle) { try { return (int)GetDpiForWindow(handle); } catch { return 96; } }
        internal static bool IsTopMost(IntPtr handle)
        {
            long style = IntPtr.Size == 8 ? GetWindowLongPtr64(handle, -20).ToInt64() : GetWindowLong32(handle, -20);
            return (style & 8) != 0;
        }
        internal static IntPtr CodexMainWindow(IntPtr foreground)
        {
            if (foreground == IntPtr.Zero) return IntPtr.Zero;
            // Owned menus and previews must follow their main window, not become a new anchor.
            IntPtr root = GetAncestor(foreground, 3); // GA_ROOTOWNER
            if (root == IntPtr.Zero) root = foreground;
            Rectangle bounds = Bounds(root);
            return IsCodex(root) && IsWindowVisible(root) && !IsIconic(root) &&
                bounds.Width >= 450 && bounds.Height >= 300 ? root : IntPtr.Zero;
        }
        internal static Rectangle Bounds(IntPtr handle)
        {
            RECT rect;
            try { if (DwmGetWindowAttribute(handle, 9, out rect, Marshal.SizeOf(typeof(RECT))) == 0) return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom); }
            catch { }
            return GetWindowRect(handle, out rect) ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : Rectangle.Empty;
        }
        internal static bool IsCodex(IntPtr handle)
        {
            uint pid;
            GetWindowThreadProcessId(handle, out pid);
            if (pid == 0) return false;
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    if (!String.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase) &&
                        !String.Equals(process.ProcessName, "Codex", StringComparison.OrdinalIgnoreCase)) return false;
                    string path = process.MainModule.FileName;
                    return path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf("OpenAI\\Codex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        String.Equals(process.ProcessName, "Codex", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }
        internal static List<WindowInfo> FindCodexWindows()
        {
            var windows = new List<WindowInfo>();
            EnumWindows(delegate(IntPtr handle, IntPtr parameter) {
                if (IsWindowVisible(handle) && !IsIconic(handle) && CodexMainWindow(handle) == handle)
                {
                    Rectangle bounds = Bounds(handle);
                    if (bounds.Width > 450 && bounds.Height > 300)
                    {
                        var title = new StringBuilder(512); GetWindowText(handle, title, title.Capacity);
                        windows.Add(new WindowInfo { Handle = handle, Bounds = bounds, Title = title.ToString() });
                    }
                }
                return true;
            }, IntPtr.Zero);
            return windows;
        }
    }

    internal sealed class Settings
    {
        public bool Weekly { get; set; }
        public int RightOffset { get; set; }
        public int TopOffset { get; set; }
        public string Theme { get; set; }
        public string ViewMode { get; set; }
        public string Size { get; set; }
        public string Motion { get; set; }
        public bool PositionLocked { get; set; }
        public int PrimaryAlertPercent { get; set; }
        public int WeeklyAlertPercent { get; set; }
        public string PrimaryAlertCycle { get; set; }
        public string WeeklyAlertCycle { get; set; }
        private string storagePath;

        public Settings()
        {
            RightOffset = 220; TopOffset = 3;
            Theme = "mecha"; ViewMode = "single"; Size = "standard"; Motion = "gentle";
        }
        internal void Normalize()
        {
            if (Array.IndexOf(new[] { "mecha", "neon", "glass", "pixel" }, Theme) < 0) Theme = "mecha";
            if (Array.IndexOf(new[] { "single", "dual" }, ViewMode) < 0) ViewMode = "single";
            if (Array.IndexOf(new[] { "small", "standard", "large" }, Size) < 0) Size = "standard";
            if (Array.IndexOf(new[] { "static", "gentle", "vivid" }, Motion) < 0) Motion = "gentle";
            if (Array.IndexOf(new[] { 0, 10, 20, 30, 50 }, PrimaryAlertPercent) < 0) PrimaryAlertPercent = 0;
            if (Array.IndexOf(new[] { 0, 10, 20, 30, 50 }, WeeklyAlertPercent) < 0) WeeklyAlertPercent = 0;
            RightOffset = Math.Max(0, RightOffset); TopOffset = Math.Max(0, TopOffset);
        }
        internal float SizeFactor { get { return Size == "small" ? 0.85f : Size == "large" ? 1.2f : 1f; } }
        internal static Settings Parse(string json)
        {
            var result = new JavaScriptSerializer().Deserialize<Settings>(json) ?? new Settings();
            result.Normalize(); return result;
        }
        internal static Settings Load()
        { return LoadWithLegacy(Program.DataFile("settings.json"), Path.Combine(Program.Folder, "settings.json")); }
        internal static Settings LoadWithLegacy(string path, string legacyPath)
        {
            if (!File.Exists(path) && File.Exists(legacyPath))
            {
                var imported = LoadAt(legacyPath);
                imported.storagePath = path;
                return imported;
            }
            return LoadAt(path);
        }
        internal static Settings LoadAt(string path)
        {
            Settings result;
            try { result = Parse(File.ReadAllText(path)); }
            catch { result = new Settings(); }
            result.storagePath = path; return result;
        }
        internal void Save()
        {
            try
            {
                string path = storagePath ?? Program.DataFile("settings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Json.Encode(this), Encoding.UTF8);
            }
            catch { }
        }
        internal string StoragePath { get { return storagePath; } }
    }

    internal static class AlertPolicy
    {
        internal static string Cycle(Quota quota)
        { return quota != null && quota.ResetsAt.HasValue ? "reset:" + quota.ResetsAt.Value : "unknown-reset"; }
        internal static bool ShouldNotify(Quota quota, int threshold, string notifiedCycle)
        {
            return threshold > 0 && quota != null && !quota.ResetPending && quota.Remaining <= threshold &&
                Cycle(quota) != notifiedCycle;
        }
        internal static string RearmWhenUnknownResetRecovers(Quota quota, int threshold, string notifiedCycle)
        {
            return quota != null && !quota.ResetsAt.HasValue && threshold > 0 && quota.Remaining > threshold ? null : notifiedCycle;
        }
    }

    internal static class BatteryPainter
    {
        internal const int LogicalWidth = 72;
        internal const int LogicalHeight = 34;

        internal static void Paint(Graphics g, Rectangle rectangle, int? remaining, bool stale, bool weekly)
        {
            Paint(g, rectangle, remaining, stale, weekly ? "7D" : "5H", 0.45f, -1, 0);
        }

        internal static void Paint(Graphics g, Rectangle rectangle, int? remaining, bool stale,
            string badge, float pulse, float sweep, float numberFlash)
        { Paint(g, rectangle, remaining, stale, badge, pulse, sweep, numberFlash, false); }

        internal static void Paint(Graphics g, Rectangle rectangle, int? remaining, bool stale,
            string badge, float pulse, float sweep, float numberFlash, bool vivid)
        {
            var old = g.Save();
            g.TranslateTransform(rectangle.Left, rectangle.Top);
            float scale = rectangle.Width / (float)LogicalWidth;
            g.ScaleTransform(scale, scale);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            pulse = Math.Max(0, Math.Min(1, pulse));
            numberFlash = Math.Max(0, Math.Min(1, numberFlash));
            bool unavailable = stale || !remaining.HasValue;
            Color charge = Color.FromArgb(14, 227, 244);
            if (remaining.HasValue && remaining.Value <= 20) charge = Color.FromArgb(255, 180, 52);
            if (remaining.HasValue && remaining.Value <= 10) charge = Color.FromArgb(255, 78, 99);
            Color purple = Color.FromArgb(244, 61, 216), amber = Color.FromArgb(255, 174, 35);
            if (unavailable)
            { charge = Color.FromArgb(135, 155, 174); purple = Color.FromArgb(104, 115, 139); amber = Color.FromArgb(127, 138, 151); }

            // The opaque outside edge avoids a magenta halo against the transparency key.
            using (var armor = Polygon(new PointF[] {
                new PointF(3, 9), new PointF(7, 4), new PointF(16, 4), new PointF(18, 2),
                new PointF(48, 2), new PointF(50, 4), new PointF(62, 4), new PointF(67, 9),
                new PointF(67, 11), new PointF(70, 12), new PointF(71, 14), new PointF(71, 20),
                new PointF(70, 22), new PointF(67, 23), new PointF(67, 25), new PointF(63, 29),
                new PointF(51, 29), new PointF(49, 31), new PointF(18, 31), new PointF(16, 29),
                new PointF(7, 29), new PointF(3, 25) }))
            using (var armorFill = new LinearGradientBrush(new RectangleF(3, 2, 68, 29),
                Color.FromArgb(63, 76, 91), Color.FromArgb(11, 16, 26), 90f))
            using (var edge = new Pen(Color.FromArgb(9, 15, 25), 1))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.FillPath(armorFill, armor);
                g.DrawPath(edge, armor);
                var inside = g.Save();
                g.SetClip(armor);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using (var plating = Polygon(new PointF[] { new PointF(8, 5), new PointF(61, 5),
                    new PointF(66, 10), new PointF(66, 23), new PointF(61, 28), new PointF(8, 28),
                    new PointF(4.5f, 24), new PointF(4.5f, 9) }))
                using (var plateFill = new LinearGradientBrush(new RectangleF(5, 5, 61, 23),
                    Color.FromArgb(29, 39, 55), Color.FromArgb(11, 17, 28), 90f))
                using (var plateEdge = new Pen(Color.FromArgb(120, 133, 148), 0.7f))
                { g.FillPath(plateFill, plating); g.DrawPath(plateEdge, plating); }

                PointF[] leftLights = { new PointF(10, 7), new PointF(6.5f, 10), new PointF(6.5f, 23), new PointF(10, 26) };
                PointF[] rightLights = { new PointF(60.5f, 7), new PointF(64, 10), new PointF(64, 23), new PointF(60.5f, 26) };
                int glowAlpha = unavailable ? 24 : (int)(40 + 150 * pulse);
                using (var leftGlow = new Pen(Color.FromArgb(glowAlpha, purple), 4.2f))
                using (var rightGlow = new Pen(Color.FromArgb(glowAlpha, amber), 4.2f))
                using (var leftLine = new Pen(unavailable ? purple : ThemePainter.Pulsed(purple, pulse), 1.5f))
                using (var rightLine = new Pen(unavailable ? amber : ThemePainter.Pulsed(amber, pulse), 1.5f))
                {
                    g.DrawLines(leftGlow, leftLights); g.DrawLines(rightGlow, rightLights);
                    g.DrawLines(leftLine, leftLights); g.DrawLines(rightLine, rightLights);
                }

                using (var screen = Polygon(new PointF[] { new PointF(13, 7), new PointF(57, 7),
                    new PointF(61, 10), new PointF(61, 23), new PointF(57, 27), new PointF(13, 27),
                    new PointF(10, 24), new PointF(10, 10) }))
                using (var screenFill = new LinearGradientBrush(new RectangleF(10, 7, 51, 20),
                    Color.FromArgb(8, 22, 34), Color.FromArgb(3, 10, 19), 90f))
                using (var screenEdge = new Pen(Color.FromArgb(unavailable ? 100 : 175, charge), 0.8f))
                {
                    g.FillPath(screenFill, screen);
                    g.DrawPath(screenEdge, screen);
                }
                using (var trim = new Pen(Color.FromArgb(unavailable ? 85 : 190, charge), 1.1f))
                {
                    g.DrawLine(trim, 15, 8.6f, 54, 8.6f);
                    g.DrawLine(trim, 68.8f, 14, 68.8f, 20);
                }

                // Ten slanted segments are clipped to the actual remaining percentage.
                using (var track = new SolidBrush(Color.FromArgb(28, 48, 61)))
                using (var fill = new SolidBrush(unavailable ? charge : ThemePainter.Pulsed(charge, pulse)))
                {
                    for (int cell = 0; cell < 10; cell++)
                        using (var segment = Segment(14 + cell * 4.4f)) g.FillPath(track, segment);
                    if (remaining.HasValue && remaining.Value > 0)
                    {
                        var charged = g.Save();
                        float width = 43.6f * Math.Max(0, Math.Min(100, remaining.Value)) / 100f;
                        g.SetClip(new RectangleF(14, 23.1f, width, 3.8f), CombineMode.Intersect);
                        for (int cell = 0; cell < 10; cell++)
                            using (var segment = Segment(14 + cell * 4.4f)) g.FillPath(fill, segment);
                        g.Restore(charged);
                    }
                }
                if (!unavailable && sweep >= 0 && sweep <= 1)
                {
                    float x = 15 + 39 * sweep;
                    if (vivid)
                    {
                        var screenClip = g.Save();
                        g.SetClip(new RectangleF(10, 9, 51, 13), CombineMode.Intersect);
                        using (var glowBand = new SolidBrush(Color.FromArgb(105, charge)))
                            g.FillPolygon(glowBand, new PointF[] { new PointF(x - 7, 9), new PointF(x, 9),
                                new PointF(x + 7, 22), new PointF(x, 22) });
                        using (var beamCore = new Pen(Color.FromArgb(120, Color.White), 1.2f))
                            g.DrawLine(beamCore, x - 1, 9, x + 6, 22);
                        g.Restore(screenClip);
                        using (var glowLine = new Pen(Color.FromArgb(155, charge), 3f))
                            g.DrawLine(glowLine, Math.Max(12, x - 3), 7.5f, Math.Min(58, x + 5), 7.5f);
                    }
                    using (var shine = new Pen(Color.FromArgb(230, 235, 255, 255), vivid ? 1.8f : 1.2f))
                        g.DrawLine(shine, x, 8.1f, Math.Min(57, x + (vivid ? 5 : 3)), 8.1f);
                    if (vivid && remaining.HasValue && remaining.Value > 0)
                    {
                        var lit = g.Save();
                        g.SetClip(new RectangleF(14, 23.1f, 43.6f * Math.Max(0, Math.Min(100, remaining.Value)) / 100f, 3.8f), CombineMode.Intersect);
                        using (var gleam = new SolidBrush(Color.FromArgb(150, Color.White))) g.FillRectangle(gleam, x - 1, 23.7f, 4, 2.3f);
                        g.Restore(lit);
                    }
                }

                string value = remaining.HasValue ? remaining.Value.ToString(CultureInfo.InvariantCulture) + "%" : "--";
                using (var font = new Font("Consolas", 15.8f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var badgeFont = new Font("Consolas", badge.Length > 2 ? 5.0f : 6.8f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                using (var text = new SolidBrush(unavailable ? Color.FromArgb(184, 198, 210) : Color.FromArgb(241, 252, 255)))
                using (var glowText = new SolidBrush(Color.FromArgb(unavailable ? 15 : (int)(45 + 100 * numberFlash), charge)))
                using (var badgeBrush = new SolidBrush(unavailable ? Color.FromArgb(165, 178, 192) : Color.FromArgb(255, 200, 94)))
                {
                    g.DrawString(value, font, glowText, new RectangleF(9.3f, 6.6f, 45, 18), format);
                    g.DrawString(value, font, text, new RectangleF(8.7f, 6.1f, 45, 18), format);
                    g.DrawString(badge, badgeFont, badgeBrush, new RectangleF(52.0f, 10.2f, 10.5f, 10.6f), format);
                }
                g.Restore(inside);
            }
            if (stale)
                using (var warning = new SolidBrush(Color.FromArgb(255, 193, 65)))
                using (var diamond = Polygon(new PointF[] { new PointF(69, 2), new PointF(71, 4), new PointF(69, 6), new PointF(67, 4) }))
                    g.FillPath(warning, diamond);
            g.Restore(old);
        }
        private static GraphicsPath Segment(float x)
        {
            return Polygon(new PointF[] { new PointF(x + 0.9f, 23.7f), new PointF(x + 3.8f, 23.7f),
                new PointF(x + 2.9f, 26.0f), new PointF(x, 26.0f) });
        }
        private static GraphicsPath Polygon(PointF[] points)
        {
            var path = new GraphicsPath(); path.AddPolygon(points);
            path.CloseFigure(); return path;
        }
    }

    internal sealed class HoverIntent
    {
        private long enteredAt = -1, leftAt = -1;
        internal bool Visible { get; private set; }
        internal void Reset() { enteredAt = leftAt = -1; Visible = false; }
        internal bool Update(bool overBattery, bool overDetails, bool blocked, long now)
        {
            if (blocked) { Reset(); return false; }
            if (overBattery)
            {
                leftAt = -1;
                if (enteredAt < 0) enteredAt = now;
                if (now - enteredAt >= 300) Visible = true;
            }
            else if (Visible && overDetails) leftAt = -1;
            else
            {
                enteredAt = -1;
                if (Visible)
                {
                    if (leftAt < 0) leftAt = now;
                    if (now - leftAt >= 160) { Visible = false; leftAt = -1; }
                }
            }
            return Visible;
        }
    }

    internal sealed class UsageDetailsForm : Form
    {
        private Usage usage;
        private string content = "", status = "", warning = "";
        private bool stale, weekly, hasError, dual, locked;
        private string theme = "mecha";
        private float displayScale;
        private int warningHeight;
        private Font titleFont, labelFont, valueFont, metaFont;
        internal UsageDetailsForm()
        {
            Text = "Codex Usage Details";
            AccessibleName = "Codex 额度详情";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x8; return cp; }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
        }
        internal static int? DisplayRemaining(Quota quota)
        {
            return quota == null || quota.ResetPending ? (int?)null : quota.Remaining;
        }
        internal void SetContent(Usage newUsage, string error, bool isStale, bool isWeekly, float scale)
        { SetContent(newUsage, error, isStale, isWeekly, scale, "mecha", false, false); }
        internal void SetContent(Usage newUsage, string error, bool isStale, bool isWeekly, float scale,
            string newTheme, bool isDual, bool isLocked)
        {
            Quota primary = newUsage == null ? null : newUsage.Primary;
            Quota secondary = newUsage == null ? null : newUsage.Secondary;
            string first = primary == null ? "5 小时：暂无数据" : primary.Detail("短周期");
            string second = secondary == null ? "每周：暂无数据" : secondary.Detail("长周期");
            string newStatus = newUsage == null ? (error == null ? "正在连接…" : "暂无可显示的用量") :
                "更新于 " + newUsage.UpdatedAt.ToLocalTime().ToString("HH:mm:ss") + "  ·  每 30 秒刷新";
            string newWarning = error != null ? error + (newUsage == null ? "" : "；灰色数值为上次结果") :
                isStale && newUsage != null ? "数据可能已过期，正在等待刷新" : "";
            string firstReset = ResetText(primary), secondReset = ResetText(secondary);
            string signature = String.Join("\n", new string[] { first, second, firstReset, secondReset, newStatus, newWarning,
                isStale.ToString(), isWeekly.ToString(), newTheme, isDual.ToString(), isLocked.ToString() });
            if (content == signature && displayScale == scale) return;
            if (displayScale != scale || titleFont == null)
            {
                DisposeFonts();
                titleFont = new Font("Microsoft YaHei UI", 14 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
                labelFont = new Font("Microsoft YaHei UI", 11.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
                valueFont = new Font("Consolas", 13.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
                metaFont = new Font("Microsoft YaHei UI", 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            content = signature; displayScale = scale; usage = newUsage;
            status = newStatus; warning = newWarning; stale = isStale; weekly = isWeekly; hasError = error != null;
            theme = newTheme; dual = isDual; locked = isLocked;
            int width = Px(360), innerWidth = width - Px(32);
            warningHeight = warning.Length == 0 ? 0 : Math.Max(Px(16),
                TextRenderer.MeasureText(warning, metaFont, new Size(innerWidth, 10000),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height + Px(3));
            ClientSize = new Size(width, Px(212) + warningHeight);
            AccessibleDescription = first + "（" + firstReset + "）；" + second + "（" + secondReset + "）；" + status + (warning.Length == 0 ? "" : "；" + warning);
            Invalidate();
        }
        private int Px(float value) { return (int)Math.Round(value * displayScale); }
        private Rectangle Rect(float x, float y, float width, float height)
        { return new Rectangle(Px(x), Px(y), Px(width), Px(height)); }
        internal static string Countdown(long? resetsAt, DateTimeOffset now)
        {
            if (!resetsAt.HasValue) return "重置时间未知";
            long seconds = resetsAt.Value - now.ToUnixTimeSecondsCompat();
            if (seconds <= 0) return "等待刷新";
            if (seconds < 60) return "不足 1 分钟";
            long minutes = seconds / 60;
            if (minutes >= 1440) return (minutes / 1440) + "天" + ((minutes % 1440) / 60) + "小时";
            if (minutes >= 60) return (minutes / 60) + "小时" + (minutes % 60) + "分";
            return minutes + "分钟";
        }
        private static string ResetText(Quota quota)
        {
            if (quota == null || !quota.ResetsAt.HasValue) return "重置时间未知";
            try
            {
                string time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(quota.ResetsAt.Value).ToLocalTime().ToString("MM-dd HH:mm");
                string countdown = Countdown(quota.ResetsAt, DateTimeOffset.UtcNow);
                return "重置 " + time + " · " + (quota.ResetPending ? countdown : "还有 " + countdown);
            }
            catch { return "重置时间未知"; }
        }
        internal void ShowBeside(Form owner, Rectangle anchor, float scale)
        {
            Rectangle area = Screen.FromRectangle(anchor).WorkingArea;
            int gap = (int)Math.Round(6 * scale);
            int x = Math.Max(area.Left, Math.Min(area.Right - Width, anchor.Left));
            int y = anchor.Bottom + gap;
            if (y + Height > area.Bottom) y = anchor.Top - Height - gap;
            y = Math.Max(area.Top, Math.Min(area.Bottom - Height, y));
            bool changed = !Visible || Left != x || Top != y || !Native.IsTopMost(Handle);
            if (!Visible) Show(owner);
            if (changed) Native.SetWindowPos(Handle, new IntPtr(-1), x, y, Width, Height, 0x0010 | 0x0040 | 0x0200);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (labelFont == null) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var border = new Pen(Color.FromArgb(221, 225, 231)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            Color leftAccent = theme == "mecha" ? Color.FromArgb(223, 63, 192) : ThemePainter.Accent(theme, false);
            Color rightAccent = theme == "mecha" ? Color.FromArgb(246, 173, 37) : ThemePainter.Accent(theme, true);
            using (var purple = new Pen(leftAccent, Math.Max(1, 2 * displayScale)))
            using (var amber = new Pen(rightAccent, Math.Max(1, 2 * displayScale)))
            {
                g.DrawLines(purple, new Point[] { new Point(Px(1), Px(19)), new Point(Px(1), Px(10)),
                    new Point(Px(10), Px(1)), new Point(Px(40), Px(1)) });
                g.DrawLines(amber, new Point[] { new Point(Width - Px(42), Px(1)), new Point(Width - Px(10), Px(1)),
                    new Point(Width - Px(1), Px(10)), new Point(Width - Px(1), Px(19)) });
            }
            var single = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, "Codex 剩余额度", titleFont, Rect(16, 12, 240, 22), Color.FromArgb(17, 24, 39), single);
            string state = hasError ? "连接异常" : usage == null ? "连接中" : stale ? "数据过期" : "实时";
            TextRenderer.DrawText(g, state, metaFont, Rect(267, 13, 76, 20),
                stale ? Color.FromArgb(154, 107, 45) : Color.FromArgb(23, 140, 141), single | TextFormatFlags.Right);
            using (var divider = new Pen(Color.FromArgb(231, 235, 240)))
            { g.DrawLine(divider, Px(16), Px(39), Width - Px(16), Px(39)); g.DrawLine(divider, Px(16), Px(161), Width - Px(16), Px(161)); }

            DrawQuota(g, usage == null ? null : usage.Primary, "5 小时", 46, false, dual || !weekly);
            DrawQuota(g, usage == null ? null : usage.Secondary, "每周", 104, true, dual || weekly);
            TextRenderer.DrawText(g, status, metaFont, Rect(16, 167, 328, 17), Color.FromArgb(100, 112, 128), single);
            if (warning.Length != 0)
                TextRenderer.DrawText(g, warning, metaFont, new Rectangle(Px(16), Px(184), Px(328), warningHeight),
                    Color.FromArgb(164, 95, 31), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            string footer = (dual ? "双周期显示" : "点击切换周期") + " · " + (locked ? "位置已锁定" : "拖动调整位置") + " · 右键菜单";
            TextRenderer.DrawText(g, footer, metaFont,
                new Rectangle(Px(16), Px(187) + warningHeight, Px(328), Px(16)),
                Color.FromArgb(113, 123, 137), single | TextFormatFlags.EndEllipsis);
        }
        private void DrawQuota(Graphics g, Quota quota, string fallback, float y, bool longPeriod, bool selected)
        {
            var single = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            string label = (quota == null ? fallback : quota.Label(fallback)) + "剩余" + (selected ? "  ·  当前" : "");
            int? amount = DisplayRemaining(quota);
            string value = quota == null ? "暂无数据" : !amount.HasValue ? "等待刷新" : amount.Value + "%";
            Color accent = ThemePainter.Accent(theme, longPeriod);
            if (amount.HasValue && amount.Value <= 20) accent = Color.FromArgb(235, 153, 31);
            if (amount.HasValue && amount.Value <= 10) accent = Color.FromArgb(227, 74, 92);
            if (stale || !amount.HasValue) accent = Color.FromArgb(157, 170, 182);
            TextRenderer.DrawText(g, label, labelFont, Rect(16, y, 235, 19), Color.FromArgb(39, 49, 65), single | TextFormatFlags.EndEllipsis);
            Font actualValueFont = amount.HasValue ? valueFont : labelFont;
            TextRenderer.DrawText(g, value, actualValueFont, Rect(253, y, 91, 19),
                stale ? Color.FromArgb(141, 153, 166) : Color.FromArgb(23, 33, 49), single | TextFormatFlags.Right);
            DrawGauge(g, new RectangleF(Px(16), Px(y + 22), Px(328), Px(8)), amount, accent);
            TextRenderer.DrawText(g, ResetText(quota), metaFont, Rect(16, y + 33, 328, 16), Color.FromArgb(113, 123, 137), single);
        }
        internal static void DrawGauge(Graphics g, RectangleF box, int? amount, Color accent)
        {
            float corner = Math.Min(box.Height / 2, 3);
            PointF[] points = { new PointF(box.Left + corner, box.Top), new PointF(box.Right - corner, box.Top),
                new PointF(box.Right, box.Top + corner), new PointF(box.Right, box.Bottom - corner),
                new PointF(box.Right - corner, box.Bottom), new PointF(box.Left + corner, box.Bottom),
                new PointF(box.Left, box.Bottom - corner), new PointF(box.Left, box.Top + corner) };
            using (var track = new GraphicsPath())
            using (var trackBrush = new SolidBrush(Color.FromArgb(232, 237, 242)))
            using (var fill = new SolidBrush(accent))
            {
                track.AddPolygon(points);
                g.FillPath(trackBrush, track);
                if (!amount.HasValue || amount.Value <= 0) return;
                var clip = g.Save();
                g.SetClip(track);
                g.SetClip(new RectangleF(box.Left, box.Top, box.Width * Math.Max(0, Math.Min(100, amount.Value)) / 100f, box.Height), CombineMode.Intersect);
                g.FillRectangle(fill, box);
                using (var stripe = new Pen(Color.FromArgb(55, Color.White), Math.Max(1, box.Height / 3)))
                    for (float x = box.Left - box.Height; x < box.Right; x += box.Height * 1.8f)
                        g.DrawLine(stripe, x, box.Bottom, x + box.Height, box.Top);
                g.Restore(clip);
            }
        }
        private void DisposeFonts()
        {
            if (titleFont != null) titleFont.Dispose();
            if (labelFont != null) labelFont.Dispose();
            if (valueFont != null) valueFont.Dispose();
            if (metaFont != null) metaFont.Dispose();
            titleFont = labelFont = valueFont = metaFont = null;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeFonts();
            base.Dispose(disposing);
        }
    }

    internal sealed class BatteryForm : Form
    {
        private readonly UsageClient client = new UsageClient();
        private readonly EventWaitHandle stop;
        private readonly Settings settings;
        private readonly System.Windows.Forms.Timer followTimer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        private readonly UsageDetailsForm details = new UsageDetailsForm();
        private readonly HoverIntent hover = new HoverIntent();
        private readonly Stopwatch hoverClock = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer hoverTimer = new System.Windows.Forms.Timer { Interval = 60 };
        private readonly System.Windows.Forms.Timer animationTimer = new System.Windows.Forms.Timer { Interval = 100 };
        private readonly System.Windows.Forms.Timer alertIconTimer = new System.Windows.Forms.Timer { Interval = 12000 };
        private readonly NotifyIcon alertIcon = new NotifyIcon { Icon = SystemIcons.Information, Text = "Codex 用量提醒", Visible = false };
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly List<Action> menuChecks = new List<Action>();
        private Usage usage;
        private bool refreshing, closing, dragging, moved;
        private string error;
        private IntPtr target;
        private Point dragStart, dragOrigin;
        private Rectangle targetBounds;
        private float scale = 1;
        private DateTime lastStatus;
        private long scanStartedAt = -1, numberChangedAt = -1, motionStartedAt, lastDetailsUpdate = -1000;
        private int positionError;
        private readonly bool verificationMode;
        private Action<string> verificationAlertSink;
        private bool verificationAnimationEnabled;
        private int verificationAnimationTicks;

        internal BatteryForm(EventWaitHandle stopSignal, bool verification = false)
        {
            stop = stopSignal;
            verificationMode = verification;
            settings = verification ? Settings.LoadAt(Path.Combine(Path.GetTempPath(),
                "CodexUsageBattery-selftest-" + Guid.NewGuid().ToString("N") + ".json")) : Settings.Load();
            Text = "Codex Usage Battery";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            // Keep the managed Form state in sync with the native z-order. WinForms may
            // recreate/show the HWND after tooltips, DPI changes, or switching applications.
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Magenta; TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            ClientSize = new Size(BatteryPainter.LogicalWidth, BatteryPainter.LogicalHeight);
            Cursor = Cursors.Hand;
            var displayMenu = new ToolStripMenuItem("显示方式");
            AddChoice(displayMenu, "5 小时（5H）", delegate { return settings.ViewMode == "single" && !settings.Weekly; },
                delegate { settings.ViewMode = "single"; settings.Weekly = false; Changed(); Follow(); });
            AddChoice(displayMenu, "每周（7D）", delegate { return settings.ViewMode == "single" && settings.Weekly; },
                delegate { settings.ViewMode = "single"; settings.Weekly = true; Changed(); Follow(); });
            AddChoice(displayMenu, "5H 和 7D 并排", delegate { return settings.ViewMode == "dual"; },
                delegate { settings.ViewMode = "dual"; Changed(); Follow(); });
            menu.Items.Add(displayMenu);

            var themeMenu = new ToolStripMenuItem("更换皮肤");
            AddChoice(themeMenu, "机甲能量舱", delegate { return settings.Theme == "mecha"; }, delegate { settings.Theme = "mecha"; Changed(); });
            AddChoice(themeMenu, "霓虹赛博", delegate { return settings.Theme == "neon"; }, delegate { settings.Theme = "neon"; Changed(); });
            AddChoice(themeMenu, "极光玻璃", delegate { return settings.Theme == "glass"; }, delegate { settings.Theme = "glass"; Changed(); });
            AddChoice(themeMenu, "像素游戏", delegate { return settings.Theme == "pixel"; }, delegate { settings.Theme = "pixel"; Changed(); });
            menu.Items.Add(themeMenu);

            var sizeMenu = new ToolStripMenuItem("图标大小");
            AddChoice(sizeMenu, "小", delegate { return settings.Size == "small"; }, delegate { settings.Size = "small"; Changed(); Follow(); });
            AddChoice(sizeMenu, "标准", delegate { return settings.Size == "standard"; }, delegate { settings.Size = "standard"; Changed(); Follow(); });
            AddChoice(sizeMenu, "大", delegate { return settings.Size == "large"; }, delegate { settings.Size = "large"; Changed(); Follow(); });
            menu.Items.Add(sizeMenu);

            var motionMenu = new ToolStripMenuItem("动画效果");
            AddChoice(motionMenu, "静态", delegate { return settings.Motion == "static"; }, delegate { SetMotion("static"); });
            AddChoice(motionMenu, "柔和", delegate { return settings.Motion == "gentle"; }, delegate { SetMotion("gentle"); });
            AddChoice(motionMenu, "炫酷", delegate { return settings.Motion == "vivid"; }, delegate { SetMotion("vivid"); });
            menu.Items.Add(motionMenu);

            var alertsMenu = new ToolStripMenuItem("低额度提醒");
            AddAlertChoices(alertsMenu, false);
            AddAlertChoices(alertsMenu, true);
            menu.Items.Add(alertsMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("立即刷新", null, async delegate { await RefreshUsage(); });
            var lockItem = new ToolStripMenuItem("锁定位置", null, delegate { settings.PositionLocked = !settings.PositionLocked; Changed(); });
            menu.Items.Add(lockItem);
            menuChecks.Add(delegate { lockItem.Checked = settings.PositionLocked; });
            menu.Items.Add("恢复默认位置", null, delegate { settings.RightOffset = 220; settings.TopOffset = 3; Changed(); Follow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出用量图标", null, delegate { Close(); });
            ContextMenuStrip = menu;
            menu.Opening += delegate { HideDetails(); foreach (var update in menuChecks) update(); };
            followTimer.Tick += delegate { if (stop.WaitOne(0)) { Close(); return; } Follow(); };
            refreshTimer.Tick += async delegate { await RefreshUsage(); };
            hoverTimer.Tick += delegate { UpdateHoverAt(Control.MousePosition, hoverClock.ElapsedMilliseconds, false); };
            animationTimer.Tick += delegate {
                if (Visible && !closing && !Stale)
                { if (verificationMode) verificationAnimationTicks++; Invalidate(); }
            };
            alertIconTimer.Tick += delegate { alertIconTimer.Stop(); alertIcon.Visible = false; };
            if (!verification)
                Shown += async delegate { Follow(); followTimer.Start(); refreshTimer.Start(); hoverTimer.Start(); await RefreshUsage(); };
            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            VisibleChanged += delegate {
                if (!Visible) HideDetails();
                UpdateAnimationTimer();
            };
            FormClosing += delegate { closing = true; followTimer.Stop(); refreshTimer.Stop(); hoverTimer.Stop(); animationTimer.Stop(); alertIconTimer.Stop(); alertIcon.Visible = false; HideDetails(); client.Dispose(); WriteStatus(); };
        }
        private void AddChoice(ToolStripMenuItem parent, string text, Func<bool> selected, Action click)
        {
            var item = new ToolStripMenuItem(text, null, delegate { click(); });
            parent.DropDownItems.Add(item);
            menuChecks.Add(delegate { item.Checked = selected(); });
        }
        private void AddAlertChoices(ToolStripMenuItem parent, bool weekly)
        {
            var periodMenu = new ToolStripMenuItem(weekly ? "7D 每周提醒" : "5H 额度提醒");
            foreach (int option in new[] { 0, 10, 20, 30, 50 })
            {
                int threshold = option;
                string title = threshold == 0 ? "仅变色（关闭通知）" : "剩余 " + threshold + "% 时提醒";
                AddChoice(periodMenu, title,
                    delegate { return (weekly ? settings.WeeklyAlertPercent : settings.PrimaryAlertPercent) == threshold; },
                    delegate {
                        if (weekly) settings.WeeklyAlertPercent = threshold; else settings.PrimaryAlertPercent = threshold;
                        Changed(); EvaluateAlerts();
                    });
            }
            parent.DropDownItems.Add(periodMenu);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; return cp; }
        }
        internal void RecreateForVerification() { RecreateHandle(); }
        internal ContextMenuStrip MenuForVerification { get { return menu; } }
        internal Settings SettingsForVerification { get { return settings; } }
        internal Settings ReloadSettingsForVerification()
        { if (!verificationMode) throw new InvalidOperationException(); return Settings.LoadAt(settings.StoragePath); }
        internal void SetUsageForVerification(Usage value)
        { if (!verificationMode) throw new InvalidOperationException(); usage = value; error = null; }
        internal void SetAlertSinkForVerification(Action<string> sink)
        { if (!verificationMode) throw new InvalidOperationException(); verificationAlertSink = sink; }
        internal void EvaluateAlertsForVerification()
        { if (!verificationMode) throw new InvalidOperationException(); EvaluateAlerts(); }
        internal MotionFrame MotionForVerification()
        {
            if (!verificationMode) throw new InvalidOperationException();
            return MotionFrame.At(settings.Motion, hoverClock.ElapsedMilliseconds, motionStartedAt, -1, -1, false, false);
        }
        internal void EnableAnimationForVerification(bool enabled)
        { if (!verificationMode) throw new InvalidOperationException(); verificationAnimationEnabled = enabled; UpdateAnimationTimer(); }
        internal bool AnimationEnabledForVerification { get { return animationTimer.Enabled; } }
        internal int AnimationTicksForVerification { get { return verificationAnimationTicks; } }
        internal void ClickForVerification()
        {
            if (!verificationMode) throw new InvalidOperationException();
            var click = new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0);
            OnMouseDown(this, click); OnMouseUp(this, click);
        }
        internal static Size MeasureOverlay(float dpiScale, float sizeFactor, bool dual)
        {
            float scale = dpiScale * sizeFactor;
            return new Size((int)Math.Round((dual ? 146 : BatteryPainter.LogicalWidth) * scale),
                (int)Math.Round(BatteryPainter.LogicalHeight * scale));
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref message);
        }
        private void Changed()
        {
            settings.Save(); UpdateDetails(); UpdateAnimationTimer(); Invalidate(); WriteStatus();
            if (settings.PrimaryAlertPercent == 0 && settings.WeeklyAlertPercent == 0)
            { alertIconTimer.Stop(); alertIcon.Visible = false; }
        }
        private void SetMotion(string mode)
        {
            settings.Motion = mode;
            motionStartedAt = hoverClock.ElapsedMilliseconds;
            Changed();
        }
        private void UpdateAnimationTimer()
        {
            int interval = settings.Motion == "vivid" ? 50 : 100;
            if (animationTimer.Interval != interval) animationTimer.Interval = interval;
            bool animate = Visible && !closing && (!verificationMode || verificationAnimationEnabled) &&
                settings.Motion != "static" && !SystemInformation.HighContrast && !Stale;
            if (animate && !animationTimer.Enabled) animationTimer.Start();
            else if (!animate && animationTimer.Enabled) animationTimer.Stop();
        }
        private Quota Current { get { return usage == null ? null : settings.Weekly ? usage.Secondary : usage.Primary; } }
        private bool Stale { get { return usage == null || error != null || DateTime.UtcNow - usage.UpdatedAt > TimeSpan.FromSeconds(90); } }
        private int? Percentage { get { return UsageDetailsForm.DisplayRemaining(Current); } }
        private static string PeriodBadge(Quota quota, bool weekly)
        {
            if (quota == null || !quota.Minutes.HasValue || quota.Minutes.Value <= 0) return weekly ? "7D" : "5H";
            int minutes = quota.Minutes.Value;
            if (minutes % 1440 == 0 && minutes / 1440 < 100) return (minutes / 1440) + "D";
            if (minutes % 60 == 0 && minutes / 60 < 100) return (minutes / 60) + "H";
            if (minutes < 100) return minutes + "M";
            return "--";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            long now = hoverClock.ElapsedMilliseconds;
            int? amount = Percentage;
            MotionFrame frame = MotionFrame.At(settings.Motion, now, motionStartedAt, scanStartedAt, numberChangedAt,
                SystemInformation.HighContrast || Stale, amount.HasValue && amount.Value <= 10);
            if (settings.ViewMode == "dual")
            {
                int width = (int)Math.Round(ClientRectangle.Width * BatteryPainter.LogicalWidth / 146f);
                var left = new Rectangle(0, 0, width, ClientRectangle.Height);
                var right = new Rectangle(ClientRectangle.Width - width, 0, width, ClientRectangle.Height);
                Quota primary = usage == null ? null : usage.Primary, secondary = usage == null ? null : usage.Secondary;
                ThemePainter.Paint(e.Graphics, left, UsageDetailsForm.DisplayRemaining(primary), Stale,
                    PeriodBadge(primary, false), frame.Pulse, frame.Sweep, frame.NumberFlash, settings.Theme, frame.Vivid);
                ThemePainter.Paint(e.Graphics, right, UsageDetailsForm.DisplayRemaining(secondary), Stale,
                    PeriodBadge(secondary, true), frame.Pulse, frame.Sweep, frame.NumberFlash, settings.Theme, frame.Vivid);
            }
            else ThemePainter.Paint(e.Graphics, ClientRectangle, amount, Stale,
                PeriodBadge(Current, settings.Weekly), frame.Pulse, frame.Sweep, frame.NumberFlash, settings.Theme, frame.Vivid);
        }

        private async Task RefreshUsage()
        {
            if (refreshing || closing) return;
            refreshing = true;
            int? previous = Percentage;
            bool wasWeekly = settings.Weekly;
            try
            {
                usage = await client.ReadAsync(); error = null;
                if (usage.Primary != null || usage.Secondary != null) scanStartedAt = hoverClock.ElapsedMilliseconds;
                if (wasWeekly == settings.Weekly && previous.HasValue && Percentage.HasValue && previous != Percentage)
                    numberChangedAt = hoverClock.ElapsedMilliseconds;
                EvaluateAlerts();
            }
            catch (Exception ex) { error = ex is TimeoutException ? "查询超时，将自动重试" : "暂时无法读取额度，请检查 Codex 登录和网络"; }
            finally
            {
                refreshing = false;
                if (!closing) { UpdateDetails(); UpdateAnimationTimer(); Invalidate(); WriteStatus(); }
            }
        }

        private void UpdateDetails()
        {
            details.SetContent(usage, error, Stale, settings.Weekly, scale, settings.Theme,
                settings.ViewMode == "dual", settings.PositionLocked);
        }
        private void EvaluateAlerts()
        {
            if ((verificationMode && verificationAlertSink == null) || closing || Stale || usage == null) return;
            string oldPrimary = settings.PrimaryAlertCycle, oldWeekly = settings.WeeklyAlertCycle;
            settings.PrimaryAlertCycle = AlertPolicy.RearmWhenUnknownResetRecovers(usage.Primary, settings.PrimaryAlertPercent, oldPrimary);
            settings.WeeklyAlertCycle = AlertPolicy.RearmWhenUnknownResetRecovers(usage.Secondary, settings.WeeklyAlertPercent, oldWeekly);
            bool primary = AlertPolicy.ShouldNotify(usage.Primary, settings.PrimaryAlertPercent, settings.PrimaryAlertCycle);
            bool weekly = AlertPolicy.ShouldNotify(usage.Secondary, settings.WeeklyAlertPercent, settings.WeeklyAlertCycle);
            if (!primary && !weekly)
            {
                if (oldPrimary != settings.PrimaryAlertCycle || oldWeekly != settings.WeeklyAlertCycle) settings.Save();
                return;
            }
            var messages = new List<string>();
            if (primary) messages.Add("5 小时剩余 " + usage.Primary.Remaining + "%");
            if (weekly) messages.Add("每周剩余 " + usage.Secondary.Remaining + "%");
            try
            {
                string message = String.Join("；", messages.ToArray());
                if (verificationMode) verificationAlertSink(message);
                else
                {
                    alertIcon.Visible = true;
                    alertIcon.ShowBalloonTip(10000, "Codex 额度提醒", message, ToolTipIcon.Warning);
                    alertIconTimer.Stop(); alertIconTimer.Start();
                }
                if (primary) settings.PrimaryAlertCycle = AlertPolicy.Cycle(usage.Primary);
                if (weekly) settings.WeeklyAlertCycle = AlertPolicy.Cycle(usage.Secondary);
                settings.Save();
            }
            catch { alertIcon.Visible = false; }
        }

        internal UsageDetailsForm DetailsForVerification { get { return details; } }
        internal void UpdateHoverAt(Point pointer, long now, bool blocked)
        {
            bool show = hover.Update(Bounds.Contains(pointer), details.Visible && details.Bounds.Contains(pointer),
                blocked || closing || !Visible || dragging || menu.Visible || Control.MouseButtons != MouseButtons.None, now);
            bool wasVisible = details.Visible;
            if (show)
            {
                if (!wasVisible || now - lastDetailsUpdate >= 1000)
                { UpdateDetails(); lastDetailsUpdate = now; }
                details.ShowBeside(this, Bounds, scale);
            }
            else if (details.Visible) details.Hide();
            if (wasVisible != details.Visible) WriteStatus();
        }
        private void HideDetails()
        {
            hover.Reset();
            if (!details.IsDisposed && details.Visible) details.Hide();
        }

        private void Follow()
        {
            if (closing) return;
            IntPtr foreground = Native.GetForegroundWindow();
            bool own = foreground == Handle || (details.IsHandleCreated && foreground == details.Handle) || menu.Visible || dragging;
            if (!own)
            {
                IntPtr main = Native.CodexMainWindow(foreground);
                if (main == IntPtr.Zero)
                { if (Visible) Hide(); if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 10) WriteStatus(); return; }
                target = main; targetBounds = Native.Bounds(main);
            }
            if (target == IntPtr.Zero || Native.IsIconic(target) || !Native.IsWindowVisible(target)) { if (Visible) Hide(); return; }
            targetBounds = Native.Bounds(target);
            scale = Math.Max(1, Native.Dpi(target) / 96f);
            Size measured = MeasureOverlay(scale, settings.SizeFactor, settings.ViewMode == "dual");
            int width = measured.Width, height = measured.Height;
            if (ClientSize.Width != width || ClientSize.Height != height) ClientSize = new Size(width, height);
            int x = targetBounds.Right - (int)Math.Round(settings.RightOffset * scale) - width;
            int y = targetBounds.Top + (int)Math.Round(settings.TopOffset * scale);
            x = Math.Max(targetBounds.Left + 8, Math.Min(targetBounds.Right - width - 8, x));
            y = Math.Max(targetBounds.Top, Math.Min(targetBounds.Bottom - height, y));
            bool changed = !Visible || Left != x || Top != y || Width != width || Height != height || !Native.IsTopMost(Handle);
            if (!Visible) Show();
            if (changed)
            {
                bool positioned = Native.SetWindowPos(Handle, new IntPtr(-1), x, y, width, height,
                    0x0010 | 0x0040 | 0x0200); // NOACTIVATE | SHOWWINDOW | NOOWNERZORDER
                positionError = positioned ? 0 : Marshal.GetLastWin32Error();
                HideDetails();
            }
            UpdateAnimationTimer();
            if (changed) Invalidate();
            if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 10) { Invalidate(); WriteStatus(); }
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            HideDetails();
            dragging = true; moved = false; dragStart = Control.MousePosition; dragOrigin = Location; Capture = true;
        }
        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            Point mouse = Control.MousePosition;
            int dx = mouse.X - dragStart.X, dy = mouse.Y - dragStart.Y;
            if (!moved && Math.Abs(dx) + Math.Abs(dy) < 5) return;
            moved = true;
            if (settings.PositionLocked) return;
            settings.RightOffset = Math.Max(0, (int)Math.Round((targetBounds.Right - dragOrigin.X - dx - Width) / scale));
            settings.TopOffset = Math.Max(0, (int)Math.Round((dragOrigin.Y + dy - targetBounds.Top) / scale));
            Follow();
        }
        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !dragging) return;
            dragging = false; Capture = false;
            if (!moved && settings.ViewMode != "dual") settings.Weekly = !settings.Weekly;
            Changed();
        }

        private void WriteStatus()
        {
            if (verificationMode) return;
            lastStatus = DateTime.UtcNow;
            try
            {
                Program.WriteDataFile("status.json", Json.Encode(new {
                    running = !closing, visible = Visible && !closing, processId = Process.GetCurrentProcess().Id,
                    version = "1.3.2-preview", recordedAt = DateTime.UtcNow.ToString("o"),
                    windowHandle = IsHandleCreated ? Handle.ToInt64() : 0,
                    topMost = IsHandleCreated && Native.IsTopMost(Handle), positionError = positionError,
                    detailsVisible = details.Visible,
                    detailsHandle = details.IsHandleCreated ? details.Handle.ToInt64() : 0,
                    period = settings.ViewMode == "dual" ? "dual" : settings.Weekly ? "weekly" : "primary", remaining = Percentage,
                    theme = settings.Theme, size = settings.Size, motion = settings.Motion, positionLocked = settings.PositionLocked,
                    animationActive = animationTimer.Enabled, highContrast = SystemInformation.HighContrast,
                    primaryRemaining = usage == null || usage.Primary == null ? (int?)null : usage.Primary.Remaining,
                    secondaryRemaining = usage == null || usage.Secondary == null ? (int?)null : usage.Secondary.Remaining,
                    updatedAt = usage == null ? null : usage.UpdatedAt.ToString("o"), stale = Stale, error = error,
                    overlay = new { left = Left, top = Top, width = Width, height = Height },
                    target = target.ToInt64()
                }));
            }
            catch { }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { client.Dispose(); followTimer.Dispose(); refreshTimer.Dispose(); hoverTimer.Dispose(); animationTimer.Dispose(); alertIconTimer.Dispose(); alertIcon.Dispose(); details.Dispose(); menu.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static class Verification
    {
        private static void Check(bool condition, string name) { if (!condition) throw new Exception("Verification failed: " + name); }
        private static ToolStripMenuItem Item(ToolStripItemCollection items, string text)
        {
            foreach (ToolStripItem item in items)
                if (item.Text == text && item is ToolStripMenuItem) return (ToolStripMenuItem)item;
            throw new Exception("Missing menu item: " + text);
        }
        private static int ChangedPixels(Bitmap left, Bitmap right)
        {
            int changed = 0;
            for (int y = 0; y < left.Height; y++)
                for (int x = 0; x < left.Width; x++)
                    if (left.GetPixel(x, y).ToArgb() != right.GetPixel(x, y).ToArgb()) changed++;
            return changed;
        }
        internal static void RunCore()
        {
            var oldSettings = Settings.Parse("{\"Weekly\":true,\"RightOffset\":391,\"TopOffset\":6}");
            Check(oldSettings.Weekly && oldSettings.RightOffset == 391 && oldSettings.TopOffset == 6,
                "old settings keep period and position");
            Check(oldSettings.Theme == "mecha" && oldSettings.ViewMode == "single" && oldSettings.Size == "standard" &&
                oldSettings.Motion == "gentle" && oldSettings.PrimaryAlertPercent == 0 && oldSettings.WeeklyAlertPercent == 0,
                "old settings get safe appearance and alert defaults");
            string migrationDirectory = Path.Combine(Path.GetTempPath(), "CodexBattery-migration-" + Guid.NewGuid().ToString("N"));
            string legacyPath = Path.Combine(migrationDirectory, "legacy.json");
            string destinationDirectory = Path.Combine(migrationDirectory, "data");
            string destinationPath = Path.Combine(destinationDirectory, "settings.json");
            Directory.CreateDirectory(migrationDirectory);
            try
            {
                string legacyJson = "{\"Weekly\":true,\"RightOffset\":291,\"TopOffset\":4}";
                File.WriteAllText(legacyPath, legacyJson, Encoding.UTF8);
                var imported = Settings.LoadWithLegacy(destinationPath, legacyPath);
                Check(imported.Weekly && imported.RightOffset == 291 && imported.StoragePath == destinationPath,
                    "legacy settings are loaded for the new user data directory");
                imported.Save();
                Check(File.Exists(destinationPath) && File.ReadAllText(legacyPath, Encoding.UTF8) == legacyJson,
                    "migration writes the new settings without changing the old file");
                File.WriteAllText(destinationPath, "{\"Weekly\":false,\"RightOffset\":123}", Encoding.UTF8);
                Check(!Settings.LoadWithLegacy(destinationPath, legacyPath).Weekly &&
                    Settings.LoadWithLegacy(destinationPath, legacyPath).RightOffset == 123,
                    "existing user data takes precedence over legacy settings");
            }
            finally
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                if (Directory.Exists(destinationDirectory)) Directory.Delete(destinationDirectory);
                if (File.Exists(legacyPath)) File.Delete(legacyPath);
                if (Directory.Exists(migrationDirectory)) Directory.Delete(migrationDirectory);
            }
            var savedSettings = Settings.Parse("{\"Theme\":\"pixel\",\"ViewMode\":\"dual\",\"Size\":\"large\",\"Motion\":\"static\",\"PositionLocked\":true,\"PrimaryAlertPercent\":20,\"WeeklyAlertPercent\":30}");
            var restoredSettings = Settings.Parse(Json.Encode(savedSettings));
            Check(restoredSettings.Theme == "pixel" && restoredSettings.ViewMode == "dual" && restoredSettings.Size == "large" &&
                restoredSettings.Motion == "static" && restoredSettings.PositionLocked && restoredSettings.PrimaryAlertPercent == 20 &&
                restoredSettings.WeeklyAlertPercent == 30, "new appearance and independent alert settings persist");
            var invalidSettings = Settings.Parse("{\"Theme\":\"invalid\",\"PrimaryAlertPercent\":99}");
            Check(invalidSettings.Theme == "mecha" && invalidSettings.PrimaryAlertPercent == 0, "invalid preferences fall back safely");
            Check(BatteryForm.MeasureOverlay(1.25f, 1f, false) == new Size(90, 42), "standard size stays compatible at 125 percent DPI");
            Check(BatteryForm.MeasureOverlay(1.25f, 1f, true) == new Size(182, 42), "dual size keeps both full-size gauges");
            Check(BatteryForm.MeasureOverlay(1.25f, 0.85f, false).Width < 90 &&
                BatteryForm.MeasureOverlay(1.25f, 1.2f, false).Width > 90, "small and large options change display size");
            var staticStart = MotionFrame.At("static", 0, 0, -1, -1, false, false);
            var staticLater = MotionFrame.At("static", 1500, 0, 1000, 1200, false, false);
            Check(staticStart.Pulse == staticLater.Pulse && staticLater.Sweep < 0 && staticLater.NumberFlash == 0,
                "static mode remains completely still, even during refresh");
            var gentleBright = MotionFrame.At("gentle", 1050, 0, -1, -1, false, false);
            var gentleDim = MotionFrame.At("gentle", 3150, 0, -1, -1, false, false);
            Check(gentleBright.Pulse - gentleDim.Pulse > 0.75f && gentleBright.Sweep < 0,
                "gentle mode has a visible slow breathing cycle without continuous sweep");
            var vividStart = MotionFrame.At("vivid", 0, 0, -1, -1, false, false);
            var vividLater = MotionFrame.At("vivid", 700, 0, -1, -1, false, false);
            var vividNext = MotionFrame.At("vivid", 3300, 0, -1, -1, false, false);
            Check(vividStart.Vivid && vividStart.Sweep == 0 && vividLater.Sweep > 0.5f && vividNext.Sweep > 0,
                "vivid mode begins immediately and the sweep repeats");
            var accessibleMotion = MotionFrame.At("vivid", 700, 0, 0, 0, true, false);
            Check(accessibleMotion.Pulse == staticStart.Pulse && accessibleMotion.Sweep < 0 && !accessibleMotion.Vivid,
                "high contrast or stale data suppresses motion");
            var fixedNow = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
            long epoch = fixedNow.ToUnixTimeSecondsCompat();
            Check(UsageDetailsForm.Countdown(null, fixedNow) == "重置时间未知", "unknown reset has no invented countdown");
            Check(UsageDetailsForm.Countdown(epoch - 1, fixedNow) == "等待刷新", "expired countdown waits for data");
            Check(UsageDetailsForm.Countdown(epoch + 30, fixedNow) == "不足 1 分钟", "sub-minute countdown");
            Check(UsageDetailsForm.Countdown(epoch + 3660, fixedNow) == "1小时1分", "hour countdown");
            Check(UsageDetailsForm.Countdown(epoch + 90000, fixedNow) == "1天1小时", "day countdown");
            var lowQuota = new Quota { Remaining = 20, ResetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSecondsCompat() };
            string lowCycle = AlertPolicy.Cycle(lowQuota);
            Check(!AlertPolicy.ShouldNotify(lowQuota, 0, null), "notifications default to off");
            Check(AlertPolicy.ShouldNotify(lowQuota, 20, null), "low quota notifies at the configured threshold");
            Check(!AlertPolicy.ShouldNotify(lowQuota, 20, lowCycle), "same cycle does not repeat");
            Check(AlertPolicy.ShouldNotify(lowQuota, 20, "reset:1"), "new quota cycle can notify");
            Check(!AlertPolicy.ShouldNotify(new Quota { Remaining = 0, ResetsAt = 1 }, 20, null), "expired data cannot notify");
            Check(AlertPolicy.RearmWhenUnknownResetRecovers(new Quota { Remaining = 80 }, 20, "unknown-reset") == null,
                "unknown reset rearms only after quota recovers");
            var sample = Usage.Parse(Json.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":14},\"secondary\":{\"usedPercent\":28}}}"));
            Check(sample.Primary.Remaining == 86 && sample.Secondary.Remaining == 72, "remaining means 100 minus used");
            Check(Quota.Parse(null) == null, "missing quota stays unknown");
            Check(Quota.Parse(Json.Parse("{\"usedPercent\":null}")) == null, "null is not zero usage");
            Check(Quota.Parse(Json.Parse("{\"usedPercent\":\"14\"}")) == null, "malformed quota stays unknown");
            Check(Quota.Parse(Json.Parse("{\"usedPercent\":0}")).Remaining == 100, "unused quota");
            Check(Quota.Parse(Json.Parse("{\"usedPercent\":100}")).Remaining == 0, "empty quota");
            Check(Quota.Parse(Json.Parse("{\"usedPercent\":140}")).Remaining == 0, "clamp lower bound");
            Check(Quota.Parse(Json.Parse("{\"usedPercent\":-1}")).Remaining == 100, "clamp upper bound");
            sample = Usage.Parse(Json.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":99}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":15}},\"other\":{\"primary\":{\"usedPercent\":1}}}}"));
            Check(sample.Primary.Remaining == 85, "prefer Codex bucket");
            bool rejected = false;
            try { Usage.Parse(Json.Parse("{\"rateLimits\":{\"limitId\":\"other\",\"primary\":{\"usedPercent\":1}}}")); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "do not display another bucket");
            Check(Quota.Parse(Json.Parse("{\"usedPercent\":60,\"resetsAt\":1}")).ResetPending, "expired snapshot waits for server");
            Check(UsageDetailsForm.DisplayRemaining(null) == null, "missing gauge stays unknown");
            Check(UsageDetailsForm.DisplayRemaining(new Quota { Remaining = 0 }) == 0, "empty gauge remains zero");
            Check(UsageDetailsForm.DisplayRemaining(new Quota { Remaining = 100 }) == 100, "full gauge remains full");
            Check(UsageDetailsForm.DisplayRemaining(new Quota { Remaining = 40, ResetsAt = 1 }) == null, "expired gauge waits for fresh data");
            using (var gauge = new Bitmap(220, 20))
            using (var g = Graphics.FromImage(gauge))
            {
                RectangleF bar = new RectangleF(10, 5, 200, 10);
                Color cyan = Color.FromArgb(6, 189, 208);
                g.Clear(Color.White);
                UsageDetailsForm.DrawGauge(g, bar, 37, cyan);
                Check(gauge.GetPixel(30, 10).R < 180 && gauge.GetPixel(100, 10).R > 200, "37 percent fills only the reported part of the gauge");
                g.Clear(Color.White);
                UsageDetailsForm.DrawGauge(g, bar, 0, cyan);
                Check(gauge.GetPixel(30, 10).R > 200, "zero quota has no colored fill");
                g.Clear(Color.White);
                UsageDetailsForm.DrawGauge(g, bar, null, cyan);
                Check(gauge.GetPixel(30, 10).R > 200, "unknown quota has no colored fill");
                g.Clear(Color.White);
                UsageDetailsForm.DrawGauge(g, bar, 100, cyan);
                Check(gauge.GetPixel(180, 10).R < 180, "full quota fills the gauge");
            }
            using (var firstFrame = new Bitmap(144, 68))
            using (var secondFrame = new Bitmap(144, 68))
            using (var firstGraphics = Graphics.FromImage(firstFrame))
            using (var secondGraphics = Graphics.FromImage(secondFrame))
            {
                firstGraphics.Clear(Color.White); secondGraphics.Clear(Color.White);
                BatteryPainter.Paint(firstGraphics, new Rectangle(0, 0, 144, 68), 82, false, "5H", 0, -1, 0);
                BatteryPainter.Paint(secondGraphics, new Rectangle(0, 0, 144, 68), 82, false, "5H", 1, -1, 0);
                Check(ChangedPixels(firstFrame, secondFrame) > 10, "breathing animation changes the visible frame");
                secondGraphics.Clear(Color.White);
                BatteryPainter.Paint(secondGraphics, new Rectangle(0, 0, 144, 68), 82, false, "5H", 0, 0.5f, 0);
                Check(ChangedPixels(firstFrame, secondFrame) > 0, "successful-refresh scan changes the visible frame");
            }
            foreach (string theme in new[] { "mecha", "neon", "glass", "pixel" })
            using (var empty = new Bitmap(144, 68))
            using (var full = new Bitmap(144, 68))
            using (var emptyGraphics = Graphics.FromImage(empty))
            using (var fullGraphics = Graphics.FromImage(full))
            {
                emptyGraphics.Clear(Color.White); fullGraphics.Clear(Color.White);
                ThemePainter.Paint(emptyGraphics, new Rectangle(0, 0, 144, 68), 0, false, "5H", 0.5f, -1, 0, theme);
                ThemePainter.Paint(fullGraphics, new Rectangle(0, 0, 144, 68), 100, false, "5H", 0.5f, -1, 0, theme);
                Check(empty.GetPixel(31, 49).ToArgb() != full.GetPixel(31, 49).ToArgb(), theme + " energy bar reflects quota");
            }
            foreach (string theme in new[] { "mecha", "neon", "glass", "pixel" })
            using (var dim = new Bitmap(90, 42))
            using (var bright = new Bitmap(90, 42))
            using (var noSweep = new Bitmap(90, 42))
            using (var sweep = new Bitmap(90, 42))
            using (var dimGraphics = Graphics.FromImage(dim))
            using (var brightGraphics = Graphics.FromImage(bright))
            using (var stillGraphics = Graphics.FromImage(noSweep))
            using (var sweepGraphics = Graphics.FromImage(sweep))
            {
                dimGraphics.Clear(Color.White); brightGraphics.Clear(Color.White);
                stillGraphics.Clear(Color.White); sweepGraphics.Clear(Color.White);
                ThemePainter.Paint(dimGraphics, new Rectangle(0, 0, 90, 42), 37, false, "5H", gentleDim.Pulse, -1, 0, theme, false);
                ThemePainter.Paint(brightGraphics, new Rectangle(0, 0, 90, 42), 37, false, "5H", gentleBright.Pulse, -1, 0, theme, false);
                Check(ChangedPixels(dim, bright) > 70, theme + " breathing is visible at actual icon size");
                ThemePainter.Paint(stillGraphics, new Rectangle(0, 0, 90, 42), 37, false, "5H", 0.5f, -1, 0, theme, true);
                ThemePainter.Paint(sweepGraphics, new Rectangle(0, 0, 90, 42), 37, false, "5H", 0.5f, 0.66f, 0, theme, true);
                Check(ChangedPixels(noSweep, sweep) > 25, theme + " vivid sweep changes the visible frame");
                Check(noSweep.GetPixel(50, 31).ToArgb() == sweep.GetPixel(50, 31).ToArgb(),
                    theme + " sweep does not light the unfilled part of the energy bar");
            }
        }
        internal static void Run()
        {
            RunCore();
            var previewUsage = new Usage {
                Primary = new Quota { Remaining = 82, Minutes = 300, ResetsAt = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeSecondsCompat() },
                Secondary = new Quota { Remaining = 37, Minutes = 10080, ResetsAt = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSecondsCompat() },
                UpdatedAt = DateTime.UtcNow
            };
            using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset))
            using (var overlay = new BatteryForm(stop, true))
            {
                overlay.Location = new Point(-10000, -10000);
                overlay.Show(); Application.DoEvents();
                Check(overlay.TopMost && Native.IsTopMost(overlay.Handle), "overlay starts topmost");
                overlay.Hide(); overlay.Show(); Application.DoEvents();
                Check(Native.IsTopMost(overlay.Handle), "overlay stays topmost after hide and show");
                overlay.RecreateForVerification(); Application.DoEvents();
                Check(Native.IsTopMost(overlay.Handle), "overlay stays topmost after HWND recreation");
                Point overBattery = new Point(overlay.Left + 20, overlay.Top + 15);
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    long time = cycle * 2000;
                    IntPtr foreground = Native.GetForegroundWindow();
                    overlay.UpdateHoverAt(overBattery, time, false);
                    Check(!overlay.DetailsForVerification.Visible, "hover waits without a click");
                    overlay.UpdateHoverAt(overBattery, time + 299, false);
                    Check(!overlay.DetailsForVerification.Visible, "short pass does not flash details");
                    overlay.UpdateHoverAt(overBattery, time + 360, false);
                    Application.DoEvents();
                    var popup = overlay.DetailsForVerification;
                    Check(popup.Visible && Native.IsTopMost(popup.Handle), "repeated hover shows an owned topmost card");
                    Check(Native.GetForegroundWindow() == foreground, "hover does not take keyboard focus");
                    Check(Screen.FromRectangle(overlay.Bounds).WorkingArea.Contains(popup.Bounds), "details stay on screen");
                    IntPtr popupHandle = popup.Handle;
                    popup.SetContent(previewUsage, null, false, false, 1.25f);
                    Check(popup.Visible && popup.Handle == popupHandle, "data refresh keeps the open card");
                    Check(popup.AccessibleDescription.Contains("82%") && popup.AccessibleDescription.Contains("37%"), "card describes both reported quotas");
                    if (cycle == 0)
                    {
                        using (var preview = new Bitmap(popup.Width, popup.Height))
                        { popup.DrawToBitmap(preview, popup.ClientRectangle); preview.Save(Path.Combine(Program.Folder, "details-preview.png"), ImageFormat.Png); }
                        popup.SetContent(previewUsage, "暂时无法读取额度，请检查 Codex 登录和网络", true, false, 1.25f);
                        Check(popup.AccessibleDescription.Contains("上次结果"), "stale card identifies previous values");
                        using (var preview = new Bitmap(popup.Width, popup.Height))
                        { popup.DrawToBitmap(preview, popup.ClientRectangle); preview.Save(Path.Combine(Program.Folder, "details-stale-preview.png"), ImageFormat.Png); }
                        popup.SetContent(null, "暂时无法读取额度，请检查 Codex 登录和网络", true, false, 1.25f);
                        Check(popup.AccessibleDescription.Contains("暂无数据") && !popup.AccessibleDescription.Contains("上次结果"), "initial failure has no invented previous value");
                        popup.SetContent(previewUsage, null, false, false, 1.25f);
                    }
                    Point overDetails = new Point(popup.Left + 20, popup.Top + 30);
                    overlay.UpdateHoverAt(overDetails, time + 700, false);
                    Check(popup.Visible, "moving onto details keeps them visible");
                    Point outside = new Point(popup.Right + 1000, popup.Bottom + 1000);
                    overlay.UpdateHoverAt(outside, time + 800, false);
                    overlay.UpdateHoverAt(outside, time + 961, false);
                    Check(!popup.Visible, "leaving closes details and allows another hover");
                }
                overlay.UpdateHoverAt(overBattery, 10000, false);
                overlay.UpdateHoverAt(overBattery, 10400, false);
                overlay.UpdateHoverAt(overBattery, 50000, false);
                Check(overlay.DetailsForVerification.Visible, "long hover does not expire after twenty seconds");
                overlay.UpdateHoverAt(overBattery, 50100, true);
                Check(!overlay.DetailsForVerification.Visible, "dragging or a menu dismisses details");
                overlay.SetUsageForVerification(previewUsage);
                var displayMenu = Item(overlay.MenuForVerification.Items, "显示方式");
                Item(displayMenu.DropDownItems, "5 小时（5H）").PerformClick();
                overlay.ClickForVerification();
                Check(overlay.SettingsForVerification.Weekly, "click still switches single display period");
                Item(displayMenu.DropDownItems, "5H 和 7D 并排").PerformClick();
                Check(overlay.SettingsForVerification.ViewMode == "dual", "dual display can be selected from the menu");
                overlay.ClickForVerification();
                Check(overlay.SettingsForVerification.ViewMode == "dual" && overlay.SettingsForVerification.Weekly,
                    "click does not hide either quota in dual mode");
                var themeMenu = Item(overlay.MenuForVerification.Items, "更换皮肤");
                foreach (string choice in new[] { "机甲能量舱", "霓虹赛博", "极光玻璃", "像素游戏" })
                    Item(themeMenu.DropDownItems, choice).PerformClick();
                Check(overlay.SettingsForVerification.Theme == "pixel", "all skin choices can be selected");
                Item(Item(overlay.MenuForVerification.Items, "图标大小").DropDownItems, "大").PerformClick();
                var motionMenu = Item(overlay.MenuForVerification.Items, "动画效果");
                // The off-screen test has no Codex anchor, so menu-driven Follow() hides it.
                overlay.Show(); Application.DoEvents();
                overlay.EnableAnimationForVerification(true);
                Item(motionMenu.DropDownItems, "炫酷").PerformClick();
                var initialVivid = overlay.MotionForVerification();
                Check(initialVivid.Vivid && initialVivid.Sweep >= 0 && initialVivid.Sweep < 0.25f,
                    "selecting vivid immediately starts a new visible preview");
                if (!SystemInformation.HighContrast)
                {
                    Check(overlay.AnimationEnabledForVerification, "vivid mode starts the actual render timer");
                    int ticksBefore = overlay.AnimationTicksForVerification;
                    for (int i = 0; i < 5; i++) { Thread.Sleep(60); Application.DoEvents(); }
                    Check(overlay.AnimationTicksForVerification > ticksBefore, "the live render timer ticks while visible");
                }
                Item(motionMenu.DropDownItems, "柔和").PerformClick();
                Check(!overlay.MotionForVerification().Vivid && overlay.MotionForVerification().Sweep < 0,
                    "selecting gentle immediately switches to breathing");
                Item(motionMenu.DropDownItems, "静态").PerformClick();
                Check(overlay.MotionForVerification().Pulse == 0.5f && overlay.MotionForVerification().Sweep < 0,
                    "selecting static immediately stops the animation");
                Check(!overlay.AnimationEnabledForVerification, "static mode stops the actual render timer");
                overlay.EnableAnimationForVerification(false);
                Item(overlay.MenuForVerification.Items, "锁定位置").PerformClick();
                var alertMenu = Item(overlay.MenuForVerification.Items, "低额度提醒");
                Item(Item(alertMenu.DropDownItems, "5H 额度提醒").DropDownItems, "剩余 20% 时提醒").PerformClick();
                Item(Item(alertMenu.DropDownItems, "7D 每周提醒").DropDownItems, "剩余 30% 时提醒").PerformClick();
                var fromMenu = overlay.ReloadSettingsForVerification();
                Check(fromMenu.ViewMode == "dual" && fromMenu.Theme == "pixel" && fromMenu.Size == "large" &&
                    fromMenu.Motion == "static" && fromMenu.PositionLocked && fromMenu.PrimaryAlertPercent == 20 &&
                    fromMenu.WeeklyAlertPercent == 30, "menu changes persist without affecting each other");
                var sentAlerts = new List<string>();
                overlay.SetAlertSinkForVerification(delegate(string message) { sentAlerts.Add(message); });
                var lowUsage = new Usage {
                    Primary = new Quota { Remaining = 18, ResetsAt = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSecondsCompat() },
                    Secondary = new Quota { Remaining = 12, ResetsAt = DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSecondsCompat() },
                    UpdatedAt = DateTime.UtcNow
                };
                overlay.SetUsageForVerification(lowUsage);
                overlay.EvaluateAlertsForVerification(); overlay.EvaluateAlertsForVerification();
                Check(sentAlerts.Count == 1 && sentAlerts[0].Contains("18%") && sentAlerts[0].Contains("12%"),
                    "simultaneous low periods combine and do not repeat");
                var alertSettings = overlay.ReloadSettingsForVerification();
                Check(alertSettings.PrimaryAlertCycle == AlertPolicy.Cycle(lowUsage.Primary) &&
                    alertSettings.WeeklyAlertCycle == AlertPolicy.Cycle(lowUsage.Secondary), "alert cycles survive restart");
                lowUsage.Primary.ResetsAt = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSecondsCompat();
                lowUsage.UpdatedAt = DateTime.UtcNow.AddMinutes(-5);
                overlay.EvaluateAlertsForVerification();
                Check(sentAlerts.Count == 1, "stale quota cannot send notifications");
                lowUsage.UpdatedAt = DateTime.UtcNow;
                overlay.EvaluateAlertsForVerification();
                Check(sentAlerts.Count == 2 && sentAlerts[1].Contains("18%") && !sentAlerts[1].Contains("12%"),
                    "only the changed period rearms in the next cycle");
                overlay.SetUsageForVerification(previewUsage);
                overlay.ClientSize = new Size(183, 43);
                using (var preview = new Bitmap(overlay.Width, overlay.Height))
                { overlay.DrawToBitmap(preview, overlay.ClientRectangle); preview.Save(Path.Combine(Program.Folder, "dual-runtime-preview.png"), ImageFormat.Png); }
                string verificationSettings = overlay.SettingsForVerification.StoragePath;
                overlay.Close();
                try { File.Delete(verificationSettings); } catch { }
            }
            using (var image = new Bitmap(1040, 335))
            using (var g = Graphics.FromImage(image))
            using (var heading = new Font("Microsoft YaHei UI", 14, FontStyle.Bold))
            using (var small = new Font("Microsoft YaHei UI", 10))
            using (var dark = new SolidBrush(Color.FromArgb(22, 28, 39)))
            using (var caption = new SolidBrush(Color.FromArgb(69, 82, 97)))
            {
                g.Clear(Color.FromArgb(242, 246, 250));
                g.DrawString("机甲电池 · 浅色标题栏（实际大小约 125%）", heading, dark, 24, 15);
                int?[] values = { 100, 85, 20, 5, 0, null, 85 };
                string[] labels = { "100% · 5H", "85% · 7D", "20%", "5%", "0%", "暂无数据", "连接中断" };
                for (int i = 0; i < values.Length; i++)
                {
                    int x = 25 + i * 143;
                    BatteryPainter.Paint(g, new Rectangle(x, 58, 90, 43), values[i], i == 6, i == 1);
                    g.DrawString(labels[i], small, caption, x + 4, 108);
                }
                g.FillRectangle(dark, 0, 154, image.Width, image.Height - 154);
                g.DrawString("深色标题栏（放大预览）", heading, Brushes.White, 24, 170);
                for (int i = 0; i < values.Length; i++)
                {
                    int x = 25 + i * 143;
                    BatteryPainter.Paint(g, new Rectangle(x, 207, 120, 57), values[i], i == 6, i == 1);
                    g.DrawString(labels[i], small, Brushes.LightGray, x + 8, 272);
                }
                g.DrawString("百分比为剩余额度 · 当前显示周期标在电池内", small, Brushes.LightGray, 25, 307);
                image.Save(Path.Combine(Program.Folder, "battery-preview.png"), ImageFormat.Png);
            }
            using (var image = new Bitmap(1080, 475))
            using (var g = Graphics.FromImage(image))
            using (var title = new Font("Microsoft YaHei UI", 15, FontStyle.Bold))
            using (var label = new Font("Microsoft YaHei UI", 10, FontStyle.Regular))
            using (var ink = new SolidBrush(Color.FromArgb(31, 41, 55)))
            using (var dark = new SolidBrush(Color.FromArgb(22, 28, 39)))
            {
                g.Clear(Color.FromArgb(242, 246, 250));
                g.FillRectangle(dark, 685, 0, 395, image.Height);
                g.DrawString("四款皮肤 · 125% 实际尺寸", title, ink, 20, 12);
                g.DrawString("双周期 · 深色标题栏", title, Brushes.White, 705, 12);
                string[] names = { "机甲能量舱", "霓虹赛博", "极光玻璃", "像素游戏" };
                string[] themes = { "mecha", "neon", "glass", "pixel" };
                int?[] amounts = { 100, 37, 5, null };
                string[] captions = { "100% · 5H", "37% · 7D", "5% · 5H", "暂无数据" };
                for (int row = 0; row < themes.Length; row++)
                {
                    int y = 52 + row * 102;
                    g.DrawString(names[row], label, ink, 20, y + 11);
                    for (int col = 0; col < amounts.Length; col++)
                    {
                        int x = 132 + col * 130;
                        ThemePainter.Paint(g, new Rectangle(x, y, 90, 43), amounts[col], false,
                            col == 1 ? "7D" : "5H", 0.6f, -1, 0, themes[row]);
                        g.DrawString(captions[col], label, ink, x, y + 47);
                    }
                    ThemePainter.Paint(g, new Rectangle(705, y, 90, 43), 82, false, "5H", 0.6f, -1, 0, themes[row]);
                    ThemePainter.Paint(g, new Rectangle(798, y, 90, 43), 37, false, "7D", 0.6f, -1, 0, themes[row]);
                    ThemePainter.Paint(g, new Rectangle(931, y, 120, 57), 100, false, "5H", 0.6f, -1, 0, themes[row]);
                }
                image.Save(Path.Combine(Program.Folder, "skin-preview.png"), ImageFormat.Png);
            }
            using (var image = new Bitmap(840, 440))
            using (var g = Graphics.FromImage(image))
            using (var title = new Font("Microsoft YaHei UI", 13, FontStyle.Bold))
            using (var label = new Font("Microsoft YaHei UI", 10))
            using (var ink = new SolidBrush(Color.FromArgb(31, 41, 55)))
            {
                g.Clear(Color.FromArgb(242, 246, 250));
                g.DrawString("动画分帧检查 · 实际大小约 125% · 数值为示例", title, ink, 18, 10);
                string[] headings = { "静态", "柔和 · 暗", "柔和 · 亮", "炫酷 · 流光一", "炫酷 · 流光二" };
                string[] modes = { "static", "gentle", "gentle", "vivid", "vivid" };
                long[] moments = { 0, 3150, 1050, 300, 900 };
                string[] themes = { "mecha", "neon", "glass", "pixel" };
                string[] names = { "机甲", "霓虹", "玻璃", "像素" };
                for (int col = 0; col < headings.Length; col++) g.DrawString(headings[col], label, ink, 145 + col * 135, 45);
                for (int row = 0; row < themes.Length; row++)
                {
                    int y = 75 + row * 88;
                    g.DrawString(names[row], label, ink, 22, y + 10);
                    for (int col = 0; col < modes.Length; col++)
                    {
                        MotionFrame frame = MotionFrame.At(modes[col], moments[col], 0, -1, -1, false, false);
                        ThemePainter.Paint(g, new Rectangle(145 + col * 135, y, 90, 42), 82, false, "5H",
                            frame.Pulse, frame.Sweep, frame.NumberFlash, themes[row], frame.Vivid);
                    }
                }
                image.Save(Path.Combine(Program.Folder, "motion-preview.png"), ImageFormat.Png);
            }
            File.WriteAllText(Path.Combine(Program.Folder, "self-test-result.txt"), "PASS: static/gentle/vivid timing; immediate switching; motion visibly changes all four skins; sweep stays inside reported fill; old/new preferences; countdown; deduplicated alerts; dual display; topmost lifecycle; hover and nonactivation. Previews rendered.\r\n", Encoding.UTF8);
        }
    }
}
