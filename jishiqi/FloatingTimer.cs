using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FloatingMathTimer
{
    static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "FloatingMathTimer_SingleInstance_7C1D", out created))
            {
                if (!created)
                {
                    IntPtr h = FindWindow(null, "悬浮计时器");
                    if (h != IntPtr.Zero) SetForegroundWindow(h);
                    return;
                }

                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TimerForm());
                GC.KeepAlive(mutex);
            }
        }
    }

    sealed class TimerForm : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "FloatingMathTimer";
        const string Title = "悬浮计时器";

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        readonly Stopwatch sw = new Stopwatch();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolTip tip = new ToolTip();
        readonly ToolStripMenuItem miRun = new ToolStripMenuItem("开始");
        readonly ToolStripMenuItem miTop = new ToolStripMenuItem("窗口置顶");
        readonly ToolStripMenuItem miBoot = new ToolStripMenuItem("开机自启");
        Label lbl;

        readonly float scale;
        readonly Font displayFont;

        bool pressed;
        bool dragging;
        Point anchor;
        Point startLocation;

        static readonly Color Accent = Color.FromArgb(110, 231, 183);
        static readonly Color Muted = Color.FromArgb(150, 160, 178);
        static readonly Color Bg = Color.FromArgb(24, 24, 28);
        static readonly Color BorderColor = Color.FromArgb(62, 62, 72);

        static string CfgPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "floattimer.cfg"); }
        }

        public TimerForm()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { scale = g.DpiX / 96f; }
            if (scale <= 0f || scale > 8f) scale = 1f;

            Text = Title;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Bg;
            Padding = new Padding(1);
            DoubleBuffered = true;
            Font = new Font("Microsoft YaHei UI", 9F);
            Opacity = 0.95;
            KeyPreview = true;

            displayFont = new Font("Consolas", 15F, FontStyle.Bold);

            int padX = Scaled(9);
            int padY = Scaled(5);
            Size maxText = TextRenderer.MeasureText("1:23:45", displayFont);
            Size minText = TextRenderer.MeasureText("00:00", displayFont);
            Size = new Size(maxText.Width + padX * 2 + 2, Math.Max(maxText.Height, minText.Height) + padY * 2 + 2);

            lbl = new Label();
            lbl.Dock = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.Font = displayFont;
            lbl.ForeColor = Muted;
            lbl.BackColor = Color.Transparent;
            lbl.Text = "00:00";
            lbl.MouseDown += HandleDown;
            lbl.MouseMove += HandleMove;
            lbl.MouseUp += HandleUp;
            Controls.Add(lbl);

            miRun.Click += delegate { Toggle(); };
            miTop.CheckOnClick = true;
            miTop.Click += delegate { TopMost = miTop.Checked; SaveConfig(); };
            miBoot.CheckOnClick = true;
            miBoot.Click += delegate { SetAutostart(miBoot.Checked); };

            ToolStripMenuItem miReset = new ToolStripMenuItem("归零");
            miReset.Click += delegate { ResetTimer(); };

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { Close(); };

            menu.Font = new Font("Microsoft YaHei UI", 9F);
            menu.Items.Add(miRun);
            menu.Items.Add(miReset);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miTop);
            menu.Items.Add(miBoot);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            ContextMenuStrip = menu;

            tip.SetToolTip(lbl, "单击 开始/暂停 · 拖动 换位置 · 右键 更多");

            tick.Interval = 100;
            tick.Tick += delegate { UpdateText(); };
            tick.Start();

            MouseDown += HandleDown;
            MouseMove += HandleMove;
            MouseUp += HandleUp;
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space) { Toggle(); e.Handled = true; }
                else if (e.KeyCode == Keys.R) { ResetTimer(); }
            };

            LoadConfig();
            miTop.Checked = TopMost;
            miBoot.Checked = IsAutostart();
            UpdateText();
        }

        int Scaled(float v)
        {
            return (int)Math.Round(v * scale);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: 不出现在 Alt+Tab
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetWindowPos(Handle, IntPtr.Zero, startLocation.X, startLocation.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Location = startLocation;
            ClampToScreen();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(BorderColor))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveConfig();
            base.OnFormClosing(e);
        }

        void Toggle()
        {
            if (sw.IsRunning) sw.Stop();
            else sw.Start();
            UpdateText();
        }

        void ResetTimer()
        {
            if (sw.Elapsed.TotalSeconds >= 1) AppendLog(sw.Elapsed);
            sw.Reset();
            UpdateText();
        }

        void AppendLog(TimeSpan t)
        {
            try
            {
                string d = t.TotalHours >= 1
                    ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                    : string.Format("{0}分{1:00}秒", (int)t.TotalMinutes, t.Seconds);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\t用时 " + d + "\r\n";
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "做题记录.txt"), line, new System.Text.UTF8Encoding(true));
            }
            catch { }
        }

        void UpdateText()
        {
            TimeSpan t = sw.Elapsed;
            string text;
            if (t.TotalHours >= 1)
                text = string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
            else
                text = string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);

            if (lbl.Text != text) lbl.Text = text;
            lbl.ForeColor = sw.IsRunning ? Accent : Muted;
            miRun.Text = sw.IsRunning ? "暂停" : "开始";
        }

        void HandleDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            pressed = true;
            dragging = false;
            anchor = Cursor.Position;
        }

        void HandleMove(object sender, MouseEventArgs e)
        {
            if (!pressed) return;
            Point now = Cursor.Position;
            if (!dragging)
            {
                if (Math.Abs(now.X - anchor.X) < Scaled(3) && Math.Abs(now.Y - anchor.Y) < Scaled(3)) return;
                dragging = true;
                anchor = now;
                return;
            }
            Location = new Point(Location.X + now.X - anchor.X, Location.Y + now.Y - anchor.Y);
            anchor = now;
        }

        void HandleUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !pressed) return;
            pressed = false;
            if (dragging)
            {
                dragging = false;
                ClampToScreen();
                SaveConfig();
            }
            else
            {
                Toggle();
            }
        }

        void ClampToScreen()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int x = Math.Min(Math.Max(Location.X, wa.Left), wa.Right - Width);
            int y = Math.Min(Math.Max(Location.Y, wa.Top), wa.Bottom - Height);
            Location = new Point(x, y);
        }

        Point Clamp(Point p, Rectangle wa)
        {
            int x = Math.Min(Math.Max(p.X, wa.Left), wa.Right - Width);
            int y = Math.Min(Math.Max(p.Y, wa.Top), wa.Bottom - Height);
            return new Point(x, y);
        }

        void LoadConfig()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Point p = new Point(wa.Right - Width - Scaled(14), wa.Top + (int)(wa.Height * 0.25));
            bool top = true;
            try
            {
                if (File.Exists(CfgPath))
                {
                    foreach (string line in File.ReadAllLines(CfgPath))
                    {
                        string[] kv = line.Split('=');
                        if (kv.Length != 2) continue;
                        string k = kv[0].Trim();
                        string v = kv[1].Trim();
                        int n;
                        if (k == "x" && int.TryParse(v, out n)) p = new Point(n, p.Y);
                        else if (k == "y" && int.TryParse(v, out n)) p = new Point(p.X, n);
                        else if (k == "top") top = v != "0";
                    }
                }
            }
            catch { }
            TopMost = top;
            startLocation = Clamp(p, wa);
        }

        void SaveConfig()
        {
            try
            {
                File.WriteAllText(CfgPath, "x=" + Location.X + "\r\ny=" + Location.Y + "\r\ntop=" + (TopMost ? "1" : "0") + "\r\n");
            }
            catch { }
        }

        bool IsAutostart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }
    }
}
