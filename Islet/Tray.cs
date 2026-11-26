using System;
using System.Drawing.Drawing2D;
using Islet.Services;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Islet;

/// <summary>Tray icon + menu. All settings live here so the island itself stays clutter-free.</summary>
public sealed class Tray : IDisposable
{
    readonly WF.NotifyIcon _icon;
    readonly WF.ContextMenuStrip _menu;
    readonly IslandWindow _w;

    public Tray(IslandWindow w)
    {
        _w = w;
        var s = w.Settings;
        _menu = new WF.ContextMenuStrip
        {
            ShowImageMargin = false, ShowCheckMargin = true,
            Renderer = new DarkRenderer(), BackColor = SD.Color.FromArgb(32, 32, 32), ForeColor = SD.Color.White,
            Font = new SD.Font("Segoe UI", 9.5f),
        };

        var add = new WF.ToolStripMenuItem("Add app shortcut…", null, (_, _) => AddApp());
        _menu.Items.Add(add);
        _menu.Items.Add("Reset shortcuts to most used", null, (_, _) => w.Apps.ResetToMostUsed());
        _menu.Items.Add(new WF.ToolStripSeparator());

        Toggle("Auto-hide (show at top edge)", () => s.AutoHide, v => s.AutoHide = v);
        Toggle("Liquid glass effect", () => s.Glass, v => s.Glass = v);
        Toggle("Synced lyrics", () => s.Lyrics, v => s.Lyrics = v);
        Toggle("Show pill when idle", () => s.ShowIdlePill, v => s.ShowIdlePill = v);
        Toggle("Expand on hover", () => s.HoverExpand, v => s.HoverExpand = v);
        Toggle("Bluetooth alerts", () => s.BluetoothAlerts, v => s.BluetoothAlerts = v);
        Toggle("Battery alerts", () => s.BatteryAlerts, v => s.BatteryAlerts = v);

        var startup = new WF.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        try { startup.Checked = Settings.RunAtStartup; } catch { }
        startup.CheckedChanged += (_, _) => { try { Settings.RunAtStartup = startup.Checked; } catch { } };
        _menu.Items.Add(startup);

        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add("Quit Islet", null, (_, _) => { Dispose(); System.Windows.Application.Current.Shutdown(); });
        _menu.Opening += (_, _) => add.Enabled = w.Apps.Apps.Count < AppsService.Max;

        _icon = new WF.NotifyIcon { Icon = MakeIcon(), Text = "Islet", Visible = true, ContextMenuStrip = _menu };
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) w.ToggleHome(); };
    }

    void Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var item = new WF.ToolStripMenuItem(text) { Checked = get(), CheckOnClick = true };
        item.CheckedChanged += (_, _) => { set(item.Checked); _w.Settings.Save(); _w.OnSettingsChanged(); };
        _menu.Items.Add(item);
    }

    void AddApp()
    {
        using var dlg = new WF.OpenFileDialog
        {
            Title = "Pick an app to pin to the island",
            Filter = "Apps and shortcuts (*.exe;*.lnk)|*.exe;*.lnk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs",
            DereferenceLinks = false,
        };
        if (dlg.ShowDialog() == WF.DialogResult.OK) _w.Apps.Add(dlg.FileName);
    }

    public void ShowMenu()
    {
        _menu.Show(WF.Cursor.Position);
        Native.SetForegroundWindow(_menu.Handle); // so clicking elsewhere closes it
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    static SD.Icon MakeIcon()
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            var r = new SD.RectangleF(1, 9, 30, 14); float d = 14;
            path.AddArc(r.Left, r.Top, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
            path.CloseFigure();
            g.FillPath(SD.Brushes.White, path);
            using var dot = new SD.SolidBrush(SD.Color.FromArgb(40, 40, 44));
            g.FillEllipse(dot, 20, 12.5f, 7, 7);
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }

    sealed class DarkRenderer : WF.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkTable()) { RoundedEdges = false; }

        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? SD.Color.White : SD.Color.FromArgb(120, 120, 124);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using var pen = new SD.Pen(SD.Color.White, 1.7f);
            g.DrawLines(pen, new[]
            {
                new SD.PointF(r.Left + 3, r.Top + r.Height / 2f),
                new SD.PointF(r.Left + r.Width * 0.42f, r.Bottom - 4),
                new SD.PointF(r.Right - 3, r.Top + 4),
            });
        }
    }

    sealed class DarkTable : WF.ProfessionalColorTable
    {
        static readonly SD.Color Bg = SD.Color.FromArgb(32, 32, 32), Hi = SD.Color.FromArgb(55, 55, 58), Line = SD.Color.FromArgb(60, 60, 64);
        public override SD.Color ToolStripDropDownBackground => Bg;
        public override SD.Color ImageMarginGradientBegin => Bg;
        public override SD.Color ImageMarginGradientMiddle => Bg;
        public override SD.Color ImageMarginGradientEnd => Bg;
        public override SD.Color MenuBorder => Line;
        public override SD.Color MenuItemBorder => Hi;
        public override SD.Color MenuItemSelected => Hi;
        public override SD.Color SeparatorDark => Line;
        public override SD.Color SeparatorLight => Bg;
        public override SD.Color CheckBackground => Bg;
        public override SD.Color CheckSelectedBackground => Hi;
        public override SD.Color CheckPressedBackground => Hi;
    }
}
