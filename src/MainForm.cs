using System;
using System.Drawing;
using System.Windows.Forms;

namespace WarpSwitchNext
{
    /// <summary>The main window. Built in code at 96 DPI; Windows scales it for larger displays.</summary>
    sealed class MainForm : Form
    {
        readonly AppController c;
        public event Action ExitRequested;

        readonly StatusRing ring = new StatusRing();
        readonly Label stateLabel = new Label(), messageLabel = new Label();
        readonly LinkLabel detailLink = new LinkLabel();
        readonly Panel content = new Panel();
        readonly TextBox sniBox = new TextBox();
        readonly Button applyButton = new Button();
        readonly Label sniHint = new Label();
        readonly InfoPanel info = new InfoPanel();
        readonly Label ipValue = new Label(), coloValue = new Label(), routeValue = new Label();
        readonly ToggleSwitch autoSwitch = new ToggleSwitch();
        readonly PillButton mainButton = new PillButton();
        readonly LinkLabel diagnoseLink = new LinkLabel(), logLink = new LinkLabel(), exitLink = new LinkLabel();
        readonly Label footer = new Label();
        readonly LinkLabel termsLink = new LinkLabel();
        bool sniEdited;
        DiagnosticsForm diagnostics;

        const string HintText = "任何时候都能改。连接中点「应用」只会重启隧道，不用先关闭。";

        public MainForm(AppController controller)
        {
            c = controller;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None; // scaled explicitly below (Ui.Scale), the same at every DPI
            Text = "WARP 新开关";
            Name = "MainWindow";
            Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Palette.Window;
            Font = Ui.Font(9f);

            // Header
            var logo = new PictureBox { Bounds = new Rectangle(24, 22, 48, 48), SizeMode = PictureBoxSizeMode.Zoom, Image = Ui.AppImage(Ui.S(48)) };
            var title = new Label { Text = "WARP 新开关", Bounds = new Rectangle(82, 20, 300, 30), Font = Ui.Font(15f, FontStyle.Bold), ForeColor = Palette.Text };
            var subtitle = new Label { Text = "MASQUE over HTTP/2 · 伪装域名", Bounds = new Rectangle(84, 50, 300, 20), ForeColor = Palette.Secondary };

            // Status
            ring.Bounds = new Rectangle(160, 80, 100, 100);
            stateLabel.Name = "StateLabel";
            stateLabel.Bounds = new Rectangle(24, 184, 372, 34);
            stateLabel.TextAlign = ContentAlignment.MiddleCenter;
            stateLabel.Font = Ui.Font(17f, FontStyle.Bold);
            stateLabel.ForeColor = Palette.Text;
            messageLabel.Name = "MessageLabel";
            messageLabel.Bounds = new Rectangle(24, 218, 372, 38);
            messageLabel.TextAlign = ContentAlignment.TopCenter;
            messageLabel.ForeColor = Palette.Secondary;
            messageLabel.AutoEllipsis = true;
            // The full usque error is too long for the window: shown on request, and copied.
            detailLink.Name = "DetailLink";
            detailLink.Text = "错误详情";
            detailLink.Bounds = new Rectangle(160, 256, 100, 18);
            detailLink.TextAlign = ContentAlignment.MiddleCenter;
            detailLink.Font = Ui.Font(8f);
            detailLink.LinkBehavior = LinkBehavior.HoverUnderline;
            detailLink.Visible = false;
            detailLink.LinkClicked += (s, e) => ShowDetail();

            // 伪装域名: a plain text box that is always editable, and an 应用 button.
            var sniLabel = new Label { Text = "伪装域名（TLS 握手里显示的域名）", Bounds = new Rectangle(24, 276, 372, 20), ForeColor = Palette.Secondary };
            sniBox.Name = "SniBox";
            sniBox.AccessibleName = "伪装域名";
            sniBox.Bounds = new Rectangle(24, 298, 282, 28);
            sniBox.Font = Ui.Mono(11f);
            sniBox.ImeMode = ImeMode.Disable; // domains are English letters: keep the Chinese input method off
            sniBox.Text = c.Settings.Sni;
            sniBox.TextChanged += (s, e) => { if (sniBox.Focused) sniEdited = true; };
            sniBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; e.Handled = true; ApplySni(); }
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; sniEdited = false; sniBox.Text = c.Settings.Sni; ShowHint(null); }
            };
            applyButton.Name = "ApplySni";
            applyButton.AccessibleName = "应用伪装域名";
            applyButton.Text = "应用";
            applyButton.Bounds = new Rectangle(312, 297, 84, 30);
            applyButton.UseVisualStyleBackColor = true;
            applyButton.Click += (s, e) => ApplySni();
            sniHint.Name = "SniHint";
            sniHint.Bounds = new Rectangle(24, 329, 372, 30);
            sniHint.Font = Ui.Font(8f);
            ShowHint(null);

            // Details card
            info.Bounds = new Rectangle(24, 362, 372, 120);
            info.Rows = 3;
            AddRow(0, "出口 IP", ipValue);
            AddRow(1, "节点", coloValue);
            AddRow(2, "连接方式", routeValue);
            ipValue.Name = "IpValue";

            // Auto-connect
            var autoLabel = new Label { Text = "开机时自动连接", Bounds = new Rectangle(26, 494, 260, 20), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Palette.Secondary };
            var autoCaption = new Label { Text = "唤醒、网络恢复后也会自动重连", Bounds = new Rectangle(26, 514, 260, 18), Font = Ui.Font(8f), ForeColor = Palette.Tertiary };
            autoSwitch.Name = "AutoConnect";
            autoSwitch.Bounds = new Rectangle(354, 502, 42, 22);
            autoSwitch.AccessibleName = "开机时自动连接";
            autoSwitch.Toggle += (s, e) => c.SetAutoConnect(!c.AutoConnect);

            // Main button
            mainButton.Name = "MainButton";
            mainButton.AccessibleName = "开启或关闭连接";
            mainButton.Bounds = new Rectangle(24, 544, 372, 48);
            mainButton.Font = Ui.Font(13f, FontStyle.Bold);
            mainButton.Click += (s, e) => { if (c.Working) c.UserStop(); else c.UserStart(); };

            // Small actions
            Link(diagnoseLink, "网络诊断", new Rectangle(26, 602, 120, 22), ContentAlignment.MiddleLeft, ShowDiagnostics);
            diagnoseLink.Name = "Diagnose";
            Link(logLink, "打开日志", new Rectangle(244, 602, 76, 22), ContentAlignment.MiddleRight, () => Util.OpenFolder(c.DataDir));
            Link(exitLink, "退出", new Rectangle(326, 602, 70, 22), ContentAlignment.MiddleRight, () => ExitRequested?.Invoke());

            // Footer
            footer.Bounds = new Rectangle(24, 632, 372, 18);
            footer.TextAlign = ContentAlignment.MiddleCenter;
            footer.Font = Ui.Font(8f);
            footer.ForeColor = Palette.Tertiary;
            termsLink.Text = "Cloudflare 服务条款";
            termsLink.Bounds = new Rectangle(24, 650, 372, 18);
            termsLink.TextAlign = ContentAlignment.MiddleCenter;
            termsLink.Font = Ui.Font(8f);
            termsLink.LinkBehavior = LinkBehavior.HoverUnderline;
            termsLink.LinkClicked += (s, e) => Util.OpenUrl("https://www.cloudflare.com/terms/");

            content.Controls.AddRange(new Control[]
            {
                logo, title, subtitle, ring, stateLabel, messageLabel, detailLink,
                sniLabel, sniBox, applyButton, sniHint, info,
                autoLabel, autoCaption, autoSwitch, mainButton,
                diagnoseLink, logLink, exitLink, footer, termsLink,
            });
            mainButton.TabIndex = 0; sniBox.TabIndex = 1; applyButton.TabIndex = 2;

            // Everything is laid out at 96 DPI and scaled here. If the window would not fit on the
            // screen (high scaling on a small screen) it scrolls instead of squeezing.
            content.BackColor = Palette.Window;
            content.Bounds = new Rectangle(0, 0, 420, 672);
            Ui.ScaleChildren(content);
            content.Size = Ui.Sz(420, 672);
            var full = Ui.Sz(420, 672);
            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            int chrome = SystemInformation.CaptionHeight + 2 * SystemInformation.FixedFrameBorderSize.Height + 8;
            if (full.Height + chrome > screen.Height)
            {
                full = new Size(full.Width + SystemInformation.VerticalScrollBarWidth, Math.Max(Ui.S(300), screen.Height - chrome));
                content.AutoScroll = true;
                content.AutoScrollMinSize = Ui.Sz(420, 672);
            }
            ClientSize = full;
            content.Dock = DockStyle.Fill;
            Controls.Add(content);
            ResumeLayout(false);

            c.Changed += RefreshView;
            RefreshView();
        }

        void ShowDetail()
        {
            var s = c.State;
            var text = "状态：" + s.Message + "\r\n\r\nusque 的错误：\r\n" + (s.Detail ?? "（没有）") +
                       "\r\n\r\n完整日志：" + System.IO.Path.Combine(c.DataDir, "usque.log") + "\r\n（这段文字已复制，可以直接粘贴发给别人）";
            try { Clipboard.SetText(text); } catch { }
            MessageBox.Show(this, text, "错误详情 - WARP 新开关", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ShowHint(string error)
        {
            sniHint.Text = error ?? HintText;
            sniHint.ForeColor = error != null ? Palette.Red : Palette.Tertiary;
        }

        void ApplySni()
        {
            try
            {
                var saved = c.ApplySni(sniBox.Text);
                sniEdited = false;
                sniBox.Text = saved;
                sniBox.SelectionStart = saved.Length;
                ShowHint(null);
            }
            catch (UserError e)
            {
                ShowHint(e.Message); // keep what they typed so a small mistake is easy to fix
                sniBox.Focus();
            }
        }

        /// <summary>Puts a domain in the box and applies it (from 网络诊断).</summary>
        public void UseSni(string sni)
        {
            sniBox.Text = sni;
            ApplySni();
        }

        void ShowDiagnostics()
        {
            if (diagnostics == null || diagnostics.IsDisposed)
            {
                diagnostics = new DiagnosticsForm(c, this);
                diagnostics.Show(this);
            }
            else diagnostics.Activate();
        }

        void AddRow(int index, string name, Label value)
        {
            int y = index * 40 + 10;
            var key = new Label { Text = name, Bounds = new Rectangle(16, y, 110, 20), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Palette.Secondary, BackColor = Palette.Card };
            value.Bounds = new Rectangle(126, y, 230, 20);
            value.TextAlign = ContentAlignment.MiddleRight;
            value.Font = Ui.Mono(10f);
            value.ForeColor = Palette.Text;
            value.BackColor = Palette.Card;
            value.AutoEllipsis = true;
            info.Controls.Add(key);
            info.Controls.Add(value);
        }

        static void Link(LinkLabel link, string text, Rectangle bounds, ContentAlignment align, Action action)
        {
            link.Text = text;
            // Exactly as wide as its text, so a click anywhere on the link opens it.
            link.Font = Ui.Font(9f); // measure with the window's font
            link.AutoSize = true;
            link.Padding = new Padding(0, 3, 0, 3);
            var size = link.PreferredSize;
            // Laid out at 96 DPI like everything else (scaled later), so measure in 96-DPI units too.
            int w = (int)Math.Ceiling(size.Width / Ui.Scale), h = (int)Math.Ceiling(size.Height / Ui.Scale);
            link.Location = new Point(align == ContentAlignment.MiddleRight ? bounds.Right - w : bounds.Left, bounds.Top + (bounds.Height - h) / 2);
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.LinkClicked += (s, e) => action();
        }

        // For the build's tests.
        internal TextBox SniBox => sniBox;
        internal Button ApplyButton => applyButton;
        internal string Hint => sniHint.Text;

        public void RefreshView()
        {
            if (IsDisposed) return;
            var s = c.State;
            ring.Tint = Palette.ForPhase(s.Phase);
            ring.Busy = c.Busy;
            ring.Connected = s.Phase == "on";
            ring.Failed = s.Phase == "error";
            ring.Off = s.Phase == "off";
            stateLabel.Text = c.Title;
            messageLabel.Text = s.Message;
            ipValue.Text = s.Ip;
            coloValue.Text = s.Colo;
            routeValue.Text = s.Route;
            detailLink.Visible = !string.IsNullOrEmpty(s.Detail) && (s.Phase == "error" || s.Phase == "reconnecting");
            // Never overwrite what the user is typing.
            if (!sniEdited && sniBox.Text != c.Settings.Sni) sniBox.Text = c.Settings.Sni;

            autoSwitch.Checked = c.AutoConnect;
            autoSwitch.Enabled = !c.NeedsSetup;

            mainButton.Text = c.Working ? (c.Registering ? "取消注册" : "关闭") : (c.NeedsSetup ? "同意条款并注册" : "开启");
            if (c.Working) mainButton.SetAccent(Palette.Red, Palette.RedHover);
            else mainButton.SetAccent(Palette.Blue, Palette.BlueHover);
            mainButton.Enabled = !c.Stopping;

            footer.Text = c.NeedsSetup ? "首次使用将向 Cloudflare 注册属于你自己的免费账号。" : "系统代理 " + Ports.ProxyAddress + " · 服务器公钥校验保持开启";
            termsLink.Visible = c.NeedsSetup;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) c.Changed -= RefreshView;
            base.Dispose(disposing);
        }
    }

    /// <summary>网络诊断: shows each check as it runs, then offers the 伪装域名 that worked.</summary>
    sealed class DiagnosticsForm : Form
    {
        readonly AppController c;
        readonly MainForm main;
        readonly TextBox output = new TextBox();
        readonly Button useButton = new Button(), againButton = new Button(), copyButton = new Button(), closeButton = new Button();
        string best;

        public DiagnosticsForm(AppController controller, MainForm owner)
        {
            c = controller; main = owner;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = "网络诊断 - WARP 新开关";
            Name = "DiagnosticsWindow";
            Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = Ui.Sz(620, 460);
            MinimumSize = Ui.Sz(480, 320);
            Font = Ui.Font(9f);

            output.Name = "DiagnosticsOutput";
            output.Multiline = true; output.ReadOnly = true; output.ScrollBars = ScrollBars.Vertical;
            output.WordWrap = true;
            output.Font = Ui.Mono(9.5f);
            output.BackColor = Color.White;
            output.Bounds = Ui.R(12, 12, 596, 388);
            output.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            useButton.Name = "UseSni";
            useButton.Bounds = Ui.R(12, 414, 220, 32);
            useButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            useButton.Visible = false;
            useButton.Click += (s, e) => { if (best != null) { main.UseSni(best); useButton.Enabled = false; useButton.Text = "已改用 " + best; } };
            againButton.Text = "重新诊断";
            againButton.Bounds = Ui.R(334, 414, 88, 32);
            againButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            againButton.Click += (s, e) => Begin();
            copyButton.Text = "复制结果";
            copyButton.Bounds = Ui.R(426, 414, 88, 32);
            copyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            copyButton.Click += (s, e) => { try { Clipboard.SetText(output.Text); } catch { } };
            closeButton.Text = "关闭";
            closeButton.Bounds = Ui.R(518, 414, 90, 32);
            closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            closeButton.Click += (s, e) => Close();
            CancelButton = closeButton;

            Controls.AddRange(new Control[] { output, useButton, againButton, copyButton, closeButton });
            ResumeLayout(false);
            Shown += (s, e) => Begin();
        }

        void Begin()
        {
            if (c.Diagnosing) return;
            output.Clear();
            useButton.Visible = false; useButton.Enabled = true;
            againButton.Enabled = false;
            c.Diagnose(line =>
            {
                if (IsDisposed) return;
                output.AppendText(line + "\r\n");
            }, report =>
            {
                if (IsDisposed) return;
                againButton.Enabled = true;
                output.AppendText("\r\n结果已保存到 diagnose.txt（点主窗口的「打开日志」可以找到）。\r\n");
                best = report?.BestSni;
                if (best != null && best != c.Settings.Sni)
                {
                    useButton.Text = "使用 " + best;
                    useButton.Visible = true;
                }
            });
        }
    }
}
