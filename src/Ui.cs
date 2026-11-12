using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WarpSwitchNext
{
    static class Palette
    {
        public static readonly Color Blue = Color.FromArgb(0, 103, 192);
        public static readonly Color BlueHover = Color.FromArgb(0, 88, 168);
        public static readonly Color Green = Color.FromArgb(16, 137, 62);
        public static readonly Color Orange = Color.FromArgb(202, 95, 0);
        public static readonly Color Red = Color.FromArgb(196, 43, 28);
        public static readonly Color RedHover = Color.FromArgb(170, 34, 22);
        public static readonly Color Gray = Color.FromArgb(118, 118, 118);
        public static readonly Color Text = Color.FromArgb(26, 26, 26);
        public static readonly Color Secondary = Color.FromArgb(96, 96, 96);
        public static readonly Color Tertiary = Color.FromArgb(132, 132, 132);
        public static readonly Color Window = Color.FromArgb(249, 249, 249);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(229, 229, 229);
        public static readonly Color Disabled = Color.FromArgb(200, 200, 200);

        public static Color ForPhase(string phase) =>
            phase == "on" ? Green : phase == "error" ? Orange : phase == "off" ? Gray : Blue;
    }

    static class Ui
    {
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

        public static Font Font(float size, FontStyle style = FontStyle.Regular) => Get("Microsoft YaHei UI", size, style);
        public static Font Mono(float size) => Get("Consolas", size, FontStyle.Regular);

        static Font Get(string family, float size, FontStyle style)
        {
            var key = family + size + style;
            if (cache.TryGetValue(key, out var f)) return f;
            try
            {
                f = new Font(family, size, style, GraphicsUnit.Point);
                if (!string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase))
                    f = new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Point);
            }
            catch { f = new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Point); }
            cache[key] = f;
            return f;
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>A small round status badge: a white-edged coloured disc with a check (connected) or "!" (failed).</summary>
        public static void Badge(Graphics g, RectangleF r, Color color, bool check)
        {
            using (var edge = new SolidBrush(Color.White)) g.FillEllipse(edge, r);
            var inner = RectangleF.Inflate(r, -r.Width * 0.09f, -r.Height * 0.09f);
            using (var fill = new SolidBrush(color)) g.FillEllipse(fill, inner);
            float w = inner.Width;
            using (var pen = new Pen(Color.White, Math.Max(1.5f, w * 0.14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (check)
                    g.DrawLines(pen, new[]
                    {
                        new PointF(inner.X + w * 0.28f, inner.Y + w * 0.52f),
                        new PointF(inner.X + w * 0.44f, inner.Y + w * 0.67f),
                        new PointF(inner.X + w * 0.73f, inner.Y + w * 0.35f),
                    });
                else
                {
                    g.DrawLine(pen, inner.X + w / 2, inner.Y + w * 0.26f, inner.X + w / 2, inner.Y + w * 0.56f);
                    g.DrawLine(pen, inner.X + w / 2, inner.Y + w * 0.74f, inner.X + w / 2, inner.Y + w * 0.745f);
                }
            }
        }

        public static Icon AppIcon()
        {
            using (var s = typeof(Ui).Assembly.GetManifestResourceStream("AppIcon.ico"))
                return s != null ? new Icon(s) : SystemIcons.Application;
        }

        public static Bitmap AppImage(int size)
        {
            using (var s = typeof(Ui).Assembly.GetManifestResourceStream("AppIcon.ico"))
            {
                if (s == null) return new Bitmap(size, size);
                using (var icon = new Icon(s, size, size)) return icon.ToBitmap();
            }
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        static readonly Dictionary<string, Icon> trayIcons = new Dictionary<string, Icon>();

        /// <summary>Small tray icon: the app icon with a coloured dot for the connection state.</summary>
        public static Icon TrayIcon(string phase)
        {
            var key = phase == "on" ? "on" : phase == "error" ? "error" : phase == "off" ? "off" : "busy";
            if (trayIcons.TryGetValue(key, out var cached)) return cached;
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    var color = key == "on" ? Palette.Green : key == "error" ? Palette.Orange : key == "off" ? Palette.Gray : Palette.Blue;
                    using (var picture = AppImage(size)) g.DrawImage(picture, new Rectangle(0, 0, size, size));
                    float d = size * 0.5f;
                    var dot = new RectangleF(size - d, size - d, d - 0.5f, d - 0.5f);
                    using (var ring = new SolidBrush(Color.White)) g.FillEllipse(ring, dot);
                    float edge = Math.Max(1.2f, size * 0.07f);
                    using (var fill = new SolidBrush(color)) g.FillEllipse(fill, RectangleF.Inflate(dot, -edge, -edge));
                }
                IntPtr handle = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(handle).Clone();
                DestroyIcon(handle);
                trayIcons[key] = icon;
                return icon;
            }
        }
    }

    /// <summary>
    /// The large status circle: the app picture in a round frame whose ring colour shows the state.
    /// Off: grey ring, picture in black and white. Busy: blue spinner, picture faded.
    /// Connected: green ring and a check badge. Failed: orange ring and a "!" badge.
    /// </summary>
    sealed class StatusRing : Control
    {
        static Bitmap picture;
        /// <summary>Set if the picture could not be drawn; the circle then shows plain colour.</summary>
        public static Exception LastError;
        readonly Timer spin = new Timer { Interval = 33 };
        float angle;
        Color tint = Palette.Gray;
        bool busy, connected, failed, off;

        public StatusRing()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            spin.Tick += (s, e) => { angle = (angle + 9) % 360; Invalidate(); };
            AccessibleRole = AccessibleRole.Graphic;
        }

        public Color Tint { get => tint; set { if (tint != value) { tint = value; Invalidate(); } } }
        public bool Connected { get => connected; set { if (connected != value) { connected = value; Invalidate(); } } }
        public bool Failed { get => failed; set { if (failed != value) { failed = value; Invalidate(); } } }
        public bool Off { get => off; set { if (off != value) { off = value; Invalidate(); } } }
        public bool Busy
        {
            get => busy;
            set { if (busy == value) return; busy = value; spin.Enabled = value; Invalidate(); }
        }

        static ImageAttributes Look(bool grey, bool faded)
        {
            if (!grey && !faded) return null;
            float a = faded ? 0.55f : 1f;
            var m = grey
                ? new ColorMatrix(new[]
                {
                    new[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                    new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                    new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                    new[] { 0f, 0, 0, a, 0 },
                    new[] { 0f, 0, 0, 0, 1 },
                })
                : new ColorMatrix { Matrix33 = a };
            var attrs = new ImageAttributes();
            attrs.SetColorMatrix(m);
            return attrs;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Math.Min(Width, Height);
            float stroke = Math.Max(2f, s * 0.03f);
            var outer = new RectangleF((Width - s) / 2 + stroke, (Height - s) / 2 + stroke, s - stroke * 2, s - stroke * 2);

            // The picture, cropped to the circle (overscanned so the icon's own edge never shows).
            // The ring drawn on top hides the clip edge. A drawing failure must never break the window.
            if (picture == null && LastError == null)
            {
                try { picture = Ui.AppImage(256); } catch (Exception ex) { LastError = ex; }
            }
            if (picture != null)
            {
                var dest = Rectangle.Round(RectangleF.Inflate(outer, outer.Width * 0.06f, outer.Height * 0.06f));
                var saved = g.Save();
                try
                {
                    using (var clip = new GraphicsPath()) { clip.AddEllipse(outer); g.SetClip(clip); }
                    using (var attrs = Look(off, busy))
                    {
                        if (attrs == null) g.DrawImage(picture, dest);
                        else g.DrawImage(picture, dest, 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, attrs);
                    }
                }
                catch (Exception ex) { LastError = ex; }
                finally { g.Restore(saved); }
            }
            else
            {
                using (var fill = new SolidBrush(Color.FromArgb(26, tint))) g.FillEllipse(fill, outer);
            }

            using (var ring = new Pen(Color.FromArgb(busy ? 70 : 255, tint), stroke)) g.DrawEllipse(ring, outer);
            if (busy)
            {
                using (var arc = new Pen(tint, stroke * 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, outer, angle - 90, 80);
            }
            if ((connected || failed) && !busy)
            {
                float b = s * 0.3f;
                var badge = new RectangleF(outer.Right - b * 0.92f, outer.Bottom - b * 0.92f, b, b);
                Ui.Badge(g, badge, tint, check: connected);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) spin.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A large rounded button with a solid accent colour.</summary>
    sealed class PillButton : Control
    {
        Color accent = Palette.Blue, hoverAccent = Palette.BlueHover;
        bool hover, pressed;

        public PillButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public void SetAccent(Color normal, Color hovered)
        {
            if (accent == normal && hoverAccent == hovered) return;
            accent = normal; hoverAccent = hovered; Invalidate();
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? BackColor);
            var color = !Enabled ? Palette.Disabled : (hover || pressed) ? hoverAccent : accent;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Ui.Rounded(r, Height / 2f))
            using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
            if (Focused && ShowFocusCues)
            {
                var inner = RectangleF.Inflate(r, -3, -3);
                using (var path = Ui.Rounded(inner, inner.Height / 2f))
                using (var pen = new Pen(Color.FromArgb(160, Color.White), 1.5f)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>An on/off switch. Clicking asks the owner to change the setting; the owner sets Checked.</summary>
    sealed class ToggleSwitch : Control
    {
        bool isOn;
        public event EventHandler Toggle;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool Checked
        {
            get => isOn;
            set { if (isOn == value) return; isOn = value; AccessibleDescription = value ? "已开启" : "已关闭"; Invalidate(); }
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Toggle?.Invoke(this, EventArgs.Empty); }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
            base.OnKeyUp(e);
        }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? BackColor);
            var track = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
            using (var path = Ui.Rounded(track, track.Height / 2))
            {
                if (isOn)
                {
                    using (var brush = new SolidBrush(Enabled ? Palette.Blue : Palette.Disabled)) g.FillPath(brush, path);
                }
                else
                {
                    using (var brush = new SolidBrush(Color.White)) g.FillPath(brush, path);
                    using (var pen = new Pen(Enabled ? Palette.Secondary : Palette.Disabled, 1.2f)) g.DrawPath(pen, path);
                }
            }
            float knob = track.Height * (isOn ? 0.62f : 0.52f);
            float cx = isOn ? track.Right - track.Height / 2 : track.Left + track.Height / 2;
            float cy = track.Top + track.Height / 2;
            using (var brush = new SolidBrush(isOn ? Color.White : (Enabled ? Palette.Secondary : Palette.Disabled)))
                g.FillEllipse(brush, cx - knob / 2, cy - knob / 2, knob, knob);
            if (Focused && ShowFocusCues)
            {
                using (var pen = new Pen(Palette.Text, 1) { DashStyle = DashStyle.Dot })
                using (var path = Ui.Rounded(RectangleF.Inflate(track, -2.5f, -2.5f), track.Height / 2 - 2.5f)) g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>A white card with rounded corners and thin separators between its rows.</summary>
    sealed class InfoPanel : Panel
    {
        public int Rows { get; set; } = 5;

        public InfoPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Palette.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = Height * 0.04f + 4;
            using (var path = Ui.Rounded(r, radius))
            {
                using (var brush = new SolidBrush(BackColor)) g.FillPath(brush, path);
                using (var pen = new Pen(Palette.Border)) g.DrawPath(pen, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            float rowHeight = (float)Height / Rows;
            int inset = (int)(Width * 0.04f);
            using (var pen = new Pen(Palette.Border))
                for (int i = 1; i < Rows; i++)
                {
                    int y = (int)Math.Round(rowHeight * i);
                    g.DrawLine(pen, inset, y, Width - inset, y);
                }
        }
    }
}
