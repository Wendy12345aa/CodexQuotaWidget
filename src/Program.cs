using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Codex Quota Widget")]
[assembly: System.Reflection.AssemblyDescription("Tiny always-on-top Codex quota display")]
[assembly: System.Reflection.AssemblyProduct("Codex Quota Widget")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace CodexQuotaWidget
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool demo = false;
            bool selfTest = false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, "--demo", StringComparison.OrdinalIgnoreCase))
                    demo = true;
                if (string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    demo = true;
                    selfTest = true;
                }
            }

            QuotaForm form = new QuotaForm(demo);
            System.Windows.Forms.Timer closeTimer = null;
            if (selfTest)
            {
                closeTimer = new System.Windows.Forms.Timer();
                closeTimer.Interval = 1000;
                closeTimer.Tick += delegate
                {
                    closeTimer.Stop();
                    form.Close();
                };
                form.Shown += delegate { closeTimer.Start(); };
            }
            Application.Run(form);
            if (closeTimer != null) closeTimer.Dispose();
        }
    }

    internal sealed class QuotaWindow
    {
        public int UsedPercent;
        public long? ResetsAt;
        public long? DurationMinutes;

        public int RemainingPercent
        {
            get { return Math.Max(0, Math.Min(100, 100 - UsedPercent)); }
        }
    }

    internal sealed class QuotaSnapshot
    {
        public QuotaWindow FiveHour;
        public QuotaWindow Weekly;
        public DateTime UpdatedAt;
    }

    internal sealed class CodexRateLimitClient : IDisposable
    {
        private Process process;
        private StreamWriter input;
        private int nextId;
        private readonly object sync = new object();
        private bool disposed;
        private bool initialized;

        public event Action<QuotaSnapshot> SnapshotReceived;
        public event Action<string> StatusChanged;

        public void Start()
        {
            lock (sync)
            {
                if (disposed) return;
                StopProcess();

                string codex = FindCodexExecutable();
                if (codex == null)
                {
                    RaiseStatus("Codex not found");
                    return;
                }

                try
                {
                    ProcessStartInfo info = new ProcessStartInfo();
                    info.FileName = codex;
                    info.Arguments = "app-server --stdio";
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    info.WindowStyle = ProcessWindowStyle.Hidden;
                    info.RedirectStandardInput = true;
                    info.RedirectStandardOutput = true;
                    info.RedirectStandardError = true;
                    info.StandardOutputEncoding = Encoding.UTF8;
                    info.StandardErrorEncoding = Encoding.UTF8;

                    process = new Process();
                    process.StartInfo = info;
                    process.EnableRaisingEvents = true;
                    process.OutputDataReceived += OnOutput;
                    process.ErrorDataReceived += OnError;
                    process.Exited += OnExited;

                    if (!process.Start())
                        throw new InvalidOperationException("Unable to start Codex.");

                    input = process.StandardInput;
                    input.AutoFlush = true;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    initialized = false;
                    nextId = 1;

                    Send(new Dictionary<string, object>
                    {
                        { "method", "initialize" },
                        { "id", nextId++ },
                        { "params", new Dictionary<string, object>
                            {
                                { "clientInfo", new Dictionary<string, object>
                                    {
                                        { "name", "codex-quota-widget" },
                                        { "title", "Codex Quota Widget" },
                                        { "version", "1.0.0" }
                                    }
                                },
                                { "capabilities", new Dictionary<string, object>
                                    {
                                        { "experimentalApi", true },
                                        { "requestAttestation", false },
                                        { "optOutNotificationMethods", new string[] { "thread/started", "turn/started", "turn/completed" } }
                                    }
                                }
                            }
                        }
                    });
                    RaiseStatus("Connecting...");
                }
                catch (Exception ex)
                {
                    StopProcess();
                    RaiseStatus(FriendlyError(ex.Message));
                }
            }
        }

        public void Refresh()
        {
            lock (sync)
            {
                if (disposed) return;
                if (process == null || process.HasExited)
                {
                    Start();
                    return;
                }
                if (!initialized) return;

                Send(new Dictionary<string, object>
                {
                    { "method", "account/rateLimits/read" },
                    { "id", nextId++ },
                    { "params", new Dictionary<string, object> { { "excludeResetCreditDetails", true } } }
                });
                RaiseStatus("Refreshing...");
            }
        }

        private void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            try
            {
                JavaScriptSerializer json = new JavaScriptSerializer();
                Dictionary<string, object> root = json.DeserializeObject(args.Data) as Dictionary<string, object>;
                if (root == null) return;

                object errorObject;
                if (root.TryGetValue("error", out errorObject))
                {
                    Dictionary<string, object> error = errorObject as Dictionary<string, object>;
                    string message = error != null && error.ContainsKey("message") ? Convert.ToString(error["message"], CultureInfo.InvariantCulture) : "Codex returned an error";
                    RaiseStatus(FriendlyError(message));
                    return;
                }

                object idObject;
                object resultObject;
                if (root.TryGetValue("id", out idObject) && Convert.ToInt32(idObject, CultureInfo.InvariantCulture) == 1 && root.TryGetValue("result", out resultObject))
                {
                    lock (sync)
                    {
                        if (disposed || input == null) return;
                        initialized = true;
                        Send(new Dictionary<string, object> { { "method", "initialized" } });
                    }
                    Refresh();
                    return;
                }

                if (root.TryGetValue("result", out resultObject))
                {
                    Dictionary<string, object> result = resultObject as Dictionary<string, object>;
                    QuotaSnapshot snapshot = ParseResponse(result);
                    if (snapshot != null) RaiseSnapshot(snapshot);
                    return;
                }

                object methodObject;
                object paramsObject;
                if (root.TryGetValue("method", out methodObject) && Convert.ToString(methodObject, CultureInfo.InvariantCulture) == "account/rateLimits/updated" && root.TryGetValue("params", out paramsObject))
                {
                    Dictionary<string, object> parameters = paramsObject as Dictionary<string, object>;
                    if (parameters != null)
                    {
                        object rateObject;
                        if (parameters.TryGetValue("rateLimits", out rateObject))
                        {
                            QuotaSnapshot snapshot = ParseSnapshot(rateObject as Dictionary<string, object>);
                            if (snapshot != null) RaiseSnapshot(snapshot);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseStatus("Unexpected Codex response: " + Shorten(ex.Message, 48));
            }
        }

        private void OnError(object sender, DataReceivedEventArgs args)
        {
            // App-server diagnostics go to stderr. They are intentionally not shown
            // unless the process itself exits; quota errors arrive as JSON-RPC errors.
        }

        private void OnExited(object sender, EventArgs args)
        {
            if (!disposed) RaiseStatus("Disconnected - click to retry");
        }

        private static QuotaSnapshot ParseResponse(Dictionary<string, object> result)
        {
            if (result == null) return null;
            Dictionary<string, object> selected = null;
            object byIdObject;
            if (result.TryGetValue("rateLimitsByLimitId", out byIdObject))
            {
                Dictionary<string, object> byId = byIdObject as Dictionary<string, object>;
                if (byId != null)
                {
                    object codexObject;
                    if (byId.TryGetValue("codex", out codexObject))
                        selected = codexObject as Dictionary<string, object>;
                    if (selected == null)
                    {
                        foreach (KeyValuePair<string, object> pair in byId)
                        {
                            selected = pair.Value as Dictionary<string, object>;
                            if (selected != null) break;
                        }
                    }
                }
            }

            if (selected == null)
            {
                object limitsObject;
                if (result.TryGetValue("rateLimits", out limitsObject))
                    selected = limitsObject as Dictionary<string, object>;
            }
            return ParseSnapshot(selected);
        }

        private static QuotaSnapshot ParseSnapshot(Dictionary<string, object> source)
        {
            if (source == null) return null;
            QuotaWindow first = ReadWindow(source, "primary");
            QuotaWindow second = ReadWindow(source, "secondary");
            if (first == null && second == null) return null;

            QuotaWindow five = null;
            QuotaWindow week = null;
            QuotaWindow[] windows = new QuotaWindow[] { first, second };
            foreach (QuotaWindow window in windows)
            {
                if (window == null) continue;
                if (window.DurationMinutes.HasValue && window.DurationMinutes.Value <= 360)
                    five = window;
                else if (window.DurationMinutes.HasValue && window.DurationMinutes.Value >= 1440)
                    week = window;
            }
            if (five == null) five = first;
            if (week == null) week = second;

            return new QuotaSnapshot { FiveHour = five, Weekly = week, UpdatedAt = DateTime.Now };
        }

        private static QuotaWindow ReadWindow(Dictionary<string, object> source, string key)
        {
            object value;
            if (!source.TryGetValue(key, out value)) return null;
            Dictionary<string, object> window = value as Dictionary<string, object>;
            if (window == null) return null;

            object usedObject;
            if (!window.TryGetValue("usedPercent", out usedObject)) return null;
            QuotaWindow result = new QuotaWindow();
            result.UsedPercent = Convert.ToInt32(usedObject, CultureInfo.InvariantCulture);

            object durationObject;
            if (window.TryGetValue("windowDurationMins", out durationObject) && durationObject != null)
                result.DurationMinutes = Convert.ToInt64(durationObject, CultureInfo.InvariantCulture);

            object resetObject;
            if (window.TryGetValue("resetsAt", out resetObject) && resetObject != null)
                result.ResetsAt = Convert.ToInt64(resetObject, CultureInfo.InvariantCulture);
            return result;
        }

        private void Send(Dictionary<string, object> payload)
        {
            if (input == null) return;
            JavaScriptSerializer json = new JavaScriptSerializer();
            input.WriteLine(json.Serialize(payload));
        }

        private static string FindCodexExecutable()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string part in path.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(part.Trim().Trim('"'), "codex.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
                if (Directory.Exists(root))
                {
                    FileInfo newest = null;
                    foreach (string file in Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories))
                    {
                        FileInfo info = new FileInfo(file);
                        if (newest == null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = info;
                    }
                    if (newest != null) return newest.FullName;
                }
            }
            catch { }
            return null;
        }

        private static string FriendlyError(string message)
        {
            string lower = (message ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("authentication required")) return "Sign in to Codex first";
            if (lower.Contains("not found")) return "Codex not found";
            if (lower.Contains("method not found")) return "Update Codex to view quota";
            return Shorten(message, 56);
        }

        private static string Shorten(string text, int length)
        {
            if (string.IsNullOrEmpty(text)) return "Unknown error";
            return text.Length <= length ? text : text.Substring(0, length - 1) + "...";
        }

        private void RaiseSnapshot(QuotaSnapshot snapshot)
        {
            Action<QuotaSnapshot> handler = SnapshotReceived;
            if (handler != null) handler(snapshot);
        }

        private void RaiseStatus(string status)
        {
            Action<string> handler = StatusChanged;
            if (handler != null) handler(status);
        }

        private void StopProcess()
        {
            initialized = false;
            try { if (input != null) input.Close(); } catch { }
            input = null;
            try
            {
                if (process != null && !process.HasExited) process.Kill();
            }
            catch { }
            if (process != null)
            {
                process.OutputDataReceived -= OnOutput;
                process.ErrorDataReceived -= OnError;
                process.Exited -= OnExited;
                process.Dispose();
            }
            process = null;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                StopProcess();
            }
        }
    }

    internal sealed class QuotaForm : Form
    {
        private const int WidthValue = 286;
        private const int HeightValue = 156;
        private readonly bool demo;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly CodexRateLimitClient client;
        private readonly ContextMenuStrip menu;
        private QuotaSnapshot snapshot;
        private string status = "Starting...";
        private bool hoverRefresh;
        private bool hoverClose;
        private bool dragging;
        private Point dragOrigin;
        private Point formOrigin;
        private readonly string settingsPath;

        public QuotaForm(bool demoMode)
        {
            demo = demoMode;
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaWidget", "settings.ini");

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(WidthValue, HeightValue);
            MinimumSize = MaximumSize = Size;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.96;
            BackColor = Color.FromArgb(19, 22, 31);
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);

            menu = new ContextMenuStrip();
            menu.Items.Add("Refresh now", null, delegate { RefreshQuota(); });
            menu.Items.Add("Copy status", null, delegate { CopyStatus(); });
            ToolStripMenuItem topItem = new ToolStripMenuItem("Always on top");
            topItem.Checked = true;
            topItem.CheckOnClick = true;
            topItem.CheckedChanged += delegate { TopMost = topItem.Checked; SaveSettings(); };
            menu.Items.Add(topItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });
            ContextMenuStrip = menu;

            LoadSettings(topItem);

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 60000;
            refreshTimer.Tick += delegate { RefreshQuota(); };

            if (!demo)
            {
                client = new CodexRateLimitClient();
                client.SnapshotReceived += OnSnapshot;
                client.StatusChanged += OnStatus;
            }

            Shown += OnShown;
            FormClosed += OnClosed;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            RectangleF card = new RectangleF(1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
            using (GraphicsPath path = RoundedRect(card, 18))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(245, 24, 28, 39)))
            using (Pen edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }

            using (SolidBrush dot = new SolidBrush(Color.FromArgb(110, 231, 183)))
                g.FillEllipse(dot, 16, 17, 8, 8);
            DrawText(g, "CODEX QUOTA", new Font(Font.FontFamily, 8.5f, FontStyle.Bold), Color.FromArgb(226, 232, 240), new RectangleF(31, 11, 150, 22), StringAlignment.Near);

            Color iconColor = Color.FromArgb(hoverRefresh ? 255 : 158, 166, 183);
            DrawText(g, "\u21bb", new Font("Segoe UI Symbol", 13f), iconColor, new RectangleF(228, 7, 28, 29), StringAlignment.Center);
            DrawText(g, "\u00d7", new Font("Segoe UI", 14f), Color.FromArgb(hoverClose ? 255 : 158, 166, 183), new RectangleF(255, 7, 25, 29), StringAlignment.Center);

            DrawQuotaRow(g, "5 H", snapshot == null ? null : snapshot.FiveHour, 46);
            DrawQuotaRow(g, "WEEK", snapshot == null ? null : snapshot.Weekly, 91);

            string footer;
            if (snapshot != null)
                footer = "Updated " + snapshot.UpdatedAt.ToString("HH:mm") + "  \u00b7  refreshes every 60s";
            else
                footer = status;
            DrawText(g, footer, new Font(Font.FontFamily, 7.7f), snapshot == null ? Color.FromArgb(251, 191, 36) : Color.FromArgb(120, 129, 148), new RectangleF(16, 134, 255, 16), StringAlignment.Near);
        }

        private void DrawQuotaRow(Graphics g, string name, QuotaWindow window, int y)
        {
            DrawText(g, name, new Font(Font.FontFamily, 8.0f, FontStyle.Bold), Color.FromArgb(148, 163, 184), new RectangleF(16, y, 45, 20), StringAlignment.Near);

            int remaining = window == null ? 0 : window.RemainingPercent;
            Color accent = remaining > 50 ? Color.FromArgb(110, 231, 183) : (remaining > 20 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(251, 113, 133));
            string percent = window == null ? "--" : remaining.ToString(CultureInfo.InvariantCulture) + "%";
            DrawText(g, percent, new Font(Font.FontFamily, 12.0f, FontStyle.Bold), window == null ? Color.FromArgb(100, 116, 139) : Color.White, new RectangleF(211, y - 4, 59, 25), StringAlignment.Far);

            Rectangle track = new Rectangle(62, y + 5, 139, 8);
            using (GraphicsPath trackPath = RoundedRect(track, 4))
            using (SolidBrush trackBrush = new SolidBrush(Color.FromArgb(51, 58, 73)))
                g.FillPath(trackBrush, trackPath);
            if (window != null && remaining > 0)
            {
                RectangleF bar = new RectangleF(track.X, track.Y, Math.Max(8, track.Width * remaining / 100f), track.Height);
                using (GraphicsPath barPath = RoundedRect(bar, 4))
                using (SolidBrush barBrush = new SolidBrush(accent))
                    g.FillPath(barBrush, barPath);
            }

            string reset = window == null ? "waiting for Codex" : FormatReset(window.ResetsAt);
            DrawText(g, reset, new Font(Font.FontFamily, 7.5f), Color.FromArgb(120, 129, 148), new RectangleF(62, y + 17, 205, 15), StringAlignment.Near);
        }

        private static string FormatReset(long? unixSeconds)
        {
            if (!unixSeconds.HasValue) return "reset time unavailable";
            DateTime local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).LocalDateTime;
            TimeSpan left = local - DateTime.Now;
            if (left.TotalSeconds <= 0) return "resetting soon";
            if (left.TotalDays >= 1) return "resets in " + (int)left.TotalDays + "d " + left.Hours + "h";
            if (left.TotalHours >= 1) return "resets in " + (int)left.TotalHours + "h " + left.Minutes + "m";
            return "resets in " + Math.Max(1, left.Minutes) + "m";
        }

        private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF bounds, StringAlignment alignment)
        {
            using (font)
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = alignment;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(text, font, brush, bounds, format);
            }
        }

        private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            float diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (GraphicsPath path = RoundedRect(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), 18))
                Region = new Region(path);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !RefreshBounds.Contains(e.Location) && !CloseBounds.Contains(e.Location))
            {
                dragging = true;
                dragOrigin = Cursor.Position;
                formOrigin = Location;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool newRefresh = RefreshBounds.Contains(e.Location);
            bool newClose = CloseBounds.Contains(e.Location);
            if (newRefresh != hoverRefresh || newClose != hoverClose)
            {
                hoverRefresh = newRefresh;
                hoverClose = newClose;
                Invalidate(new Rectangle(224, 4, 58, 34));
            }
            Cursor = (newRefresh || newClose) ? Cursors.Hand : Cursors.SizeAll;

            if (dragging)
            {
                Point now = Cursor.Position;
                Location = new Point(formOrigin.X + now.X - dragOrigin.X, formOrigin.Y + now.Y - dragOrigin.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                if (dragging)
                {
                    dragging = false;
                    Capture = false;
                    SaveSettings();
                }
                else if (RefreshBounds.Contains(e.Location)) RefreshQuota();
                else if (CloseBounds.Contains(e.Location)) Close();
            }
        }

        private Rectangle RefreshBounds { get { return new Rectangle(228, 7, 28, 29); } }
        private Rectangle CloseBounds { get { return new Rectangle(255, 7, 25, 29); } }

        private void OnShown(object sender, EventArgs e)
        {
            if (demo)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                snapshot = new QuotaSnapshot
                {
                    FiveHour = new QuotaWindow { UsedPercent = 32, DurationMinutes = 300, ResetsAt = now + 8237 },
                    Weekly = new QuotaWindow { UsedPercent = 58, DurationMinutes = 10080, ResetsAt = now + 288000 },
                    UpdatedAt = DateTime.Now
                };
                status = "Demo mode";
                Invalidate();
            }
            else
            {
                client.Start();
                refreshTimer.Start();
            }
        }

        private void RefreshQuota()
        {
            if (demo)
            {
                if (snapshot != null) snapshot.UpdatedAt = DateTime.Now;
                Invalidate();
            }
            else if (client != null)
            {
                client.Refresh();
            }
        }

        private void OnSnapshot(QuotaSnapshot value)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<QuotaSnapshot>(OnSnapshot), value);
                return;
            }
            snapshot = value;
            status = "Connected";
            Invalidate();
        }

        private void OnStatus(string value)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(OnStatus), value);
                return;
            }
            status = value;
            if (snapshot == null) Invalidate();
        }

        private void CopyStatus()
        {
            string text;
            if (snapshot == null)
                text = "Codex quota: " + status;
            else
                text = "Codex quota - 5h: " + Percent(snapshot.FiveHour) + " left (" + FormatReset(snapshot.FiveHour == null ? null : snapshot.FiveHour.ResetsAt) + "), week: " + Percent(snapshot.Weekly) + " left (" + FormatReset(snapshot.Weekly == null ? null : snapshot.Weekly.ResetsAt) + ")";
            try { Clipboard.SetText(text); } catch { }
        }

        private static string Percent(QuotaWindow window)
        {
            return window == null ? "--" : window.RemainingPercent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void LoadSettings(ToolStripMenuItem topItem)
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(work.Right - WidthValue - 24, work.Bottom - HeightValue - 24);
            try
            {
                if (!File.Exists(settingsPath)) return;
                string[] values = File.ReadAllText(settingsPath).Split(',');
                int x, y;
                bool top;
                if (values.Length >= 3 && int.TryParse(values[0], out x) && int.TryParse(values[1], out y) && bool.TryParse(values[2], out top))
                {
                    Rectangle all = SystemInformation.VirtualScreen;
                    if (x >= all.Left - WidthValue + 40 && x <= all.Right - 40 && y >= all.Top - HeightValue + 40 && y <= all.Bottom - 40)
                        Location = new Point(x, y);
                    TopMost = top;
                    topItem.Checked = top;
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                string directory = Path.GetDirectoryName(settingsPath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(settingsPath, Location.X + "," + Location.Y + "," + TopMost);
            }
            catch { }
        }

        private void OnClosed(object sender, FormClosedEventArgs e)
        {
            refreshTimer.Stop();
            SaveSettings();
            if (client != null) client.Dispose();
            menu.Dispose();
        }
    }
}
