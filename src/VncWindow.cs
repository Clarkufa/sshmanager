using System;
using System.Drawing;
using System.Windows.Forms;

namespace SshManager
{
    /// <summary>A standalone top-level window for one VNC session, with its own toolbar (like RdpWindow).</summary>
    internal sealed class VncWindow : ThemedForm
    {
        private readonly VncProfile profile;
        private readonly string password;
        private readonly VncDesktop desktop;
        private readonly Label status;
        private readonly Button fullScreen, reconnect, disconnect;
        private readonly ToolTip hints = new ToolTip();

        private VncWindow(VncProfile profile, string password, Action<string> openFiles)
        {
            this.profile = profile;
            this.password = password;
            Text = profile.Name + " — VNC";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.WindowsDefaultLocation;

            var toolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 2, Padding = new Padding(10, 6, 10, 6), BackColor = Theme.Canvas };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Theme.Muted, Margin = Padding.Empty };
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            fullScreen = Theme.Button("Весь экран", 116, true, Glyph.Server);
            fullScreen.Click += (sender, args) => desktop.FullScreen();
            hints.SetToolTip(fullScreen, "Ctrl+Alt+Enter возвращает из полноэкранного режима");
            reconnect = Theme.Button("Переподключить", 150, glyph: Glyph.Refresh);
            reconnect.Click += (sender, args) => desktop.Reconnect();
            disconnect = Theme.Button("Отключить", 104, danger: true, quiet: true);
            disconnect.Click += (sender, args) => desktop.Disconnect();
            if (openFiles != null)
            {
                var files = Theme.Button("Файлы", 86, glyph: Glyph.Folder);
                files.Click += (sender, args) => desktop.ShowFiles();
                buttons.Controls.Add(files);
            }
            buttons.Controls.AddRange(new Control[] { fullScreen, reconnect, disconnect });
            toolbar.Controls.Add(status, 0, 0);
            toolbar.Controls.Add(buttons, 1, 0);

            desktop = new VncDesktop { OpenFiles = openFiles, PasswordPrompt = (target, wrong) => VncLogonDialog.Ask(this, target, wrong) };
            desktop.Changed += RefreshState;
            desktop.CloseRequested += () => BeginInvoke(new Action(Close));
            Controls.Add(desktop);
            Controls.Add(toolbar);

            MinimumSize = new Size(480, 360);
            Theme.ScaleToDpi(this);
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(area.Width * 4 / 5, area.Height * 4 / 5);
            RefreshState();
            ResumeLayout();
        }

        public static void Open(VncProfile profile, string password = null, Action<string> openFiles = null)
        {
            var window = new VncWindow(profile, password ?? VncPasswords.Read(profile), openFiles);
            window.Show();
            window.Activate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            desktop.Connect(profile, password);
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            if (args.CloseReason == CloseReason.UserClosing && desktop.IsConnected &&
                MessageBox.Show(this, "Закрыть VNC-сеанс «" + profile.Name + "»?", "Вы уверены?", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) args.Cancel = true;
            base.OnFormClosing(args);
        }

        private void RefreshState()
        {
            if (IsDisposed) return;
            status.Text = profile.Name + "  ·  " + profile.Address + (profile.Via.Length == 0 ? "" : "  ·  через " + profile.Via) +
                "  ·  " + desktop.Status;
            Text = profile.Name + " — VNC" + (desktop.IsConnected ? "" : " (" + desktop.Status + ")");
            fullScreen.Enabled = disconnect.Enabled = desktop.IsConnected;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hints.Dispose();
            base.Dispose(disposing);
        }
    }
}
