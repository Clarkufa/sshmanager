using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SshManager
{
    /// <summary>A standalone top-level window for one RDP session, with its own toolbar.</summary>
    internal sealed class RdpWindow : ThemedForm
    {
        private readonly RdpProfile profile;
        private readonly RdpCredential credential;
        private readonly RdpDesktop desktop;
        private readonly Label status;
        private readonly Button fullScreen, reconnect, disconnect;
        private readonly ToolTip hints = new ToolTip();

        private RdpWindow(RdpProfile profile, RdpCredential credential)
        {
            this.profile = profile;
            this.credential = credential;
            Text = profile.Name + " — RDP";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            StartPosition = FormStartPosition.WindowsDefaultLocation;

            var toolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 2, Padding = new Padding(10, 6, 10, 6), BackColor = Theme.Canvas };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Theme.Muted, Margin = Padding.Empty };
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            fullScreen = Theme.Button("Весь экран", 116, true, Glyph.Server);
            fullScreen.Click += (sender, args) => RunCommand(desktop.FullScreen);
            hints.SetToolTip(fullScreen, "Ctrl+Alt+Break переключает полноэкранный режим внутри сеанса");
            reconnect = Theme.Button("Переподключить", 150, glyph: Glyph.Refresh);
            reconnect.Click += (sender, args) => desktop.Reconnect();
            disconnect = Theme.Button("Отключить", 104, danger: true, quiet: true);
            disconnect.Click += (sender, args) => RunCommand(desktop.Disconnect);
            buttons.Controls.AddRange(new Control[] { fullScreen, reconnect, disconnect });
            toolbar.Controls.Add(status, 0, 0);
            toolbar.Controls.Add(buttons, 1, 0);

            desktop = new RdpDesktop();
            desktop.Changed += RefreshState;
            desktop.CloseRequested += () => BeginInvoke(new Action(Close));
            Controls.Add(desktop);
            Controls.Add(toolbar);

            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int toolbarHeight = toolbar.Height * DeviceDpi / 96;
            var wanted = profile.Width != 0 ? new Size(profile.Width, profile.Height + toolbarHeight)
                : new Size(area.Width * 4 / 5, area.Height * 4 / 5);
            ClientSize = new Size(Math.Min(wanted.Width, area.Width - 40), Math.Min(wanted.Height, area.Height - 60));
            MinimumSize = new Size(480, 360);
            RefreshState();
        }

        public static void Open(RdpProfile profile, RdpCredential credential = null, IWin32Window owner = null)
        {
            credential = credential ?? RdpLogonDialog.Resolve(owner, profile);
            if (credential == null) return;
            var window = new RdpWindow(profile, credential);
            window.Show();
            window.Activate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { desktop.Connect(profile, credential); }
            catch (Exception error) when (error is ExternalException || error is TargetInvocationException ||
                error is InvalidOperationException || error is ArgumentException)
            {
                Theme.ShowError(this, new System.IO.IOException("Не удалось открыть RDP-сеанс. Попробуйте «Открыть в клиенте Windows» в меню подключения.\n" +
                    (error.InnerException ?? error).Message, error));
                Close();
            }
        }

        private void RefreshState()
        {
            if (IsDisposed) return;
            status.Text = profile.Name + "  ·  " + profile.Address + "  ·  " + desktop.Status;
            Text = profile.Name + " — RDP" + (desktop.IsConnected ? "" : " (" + desktop.Status + ")");
            fullScreen.Enabled = disconnect.Enabled = desktop.IsConnected;
        }

        private void RunCommand(Action command)
        {
            try { command(); }
            catch (Exception error) when (error is ExternalException || error is TargetInvocationException)
            { Theme.ShowError(this, error); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hints.Dispose();
            base.Dispose(disposing);
        }
    }
}
