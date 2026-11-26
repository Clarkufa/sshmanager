using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SshManager
{
    /// <summary>
    /// A VNC desktop inside the window (docs/ui-spec.md §8): draws the framebuffer scaled to the window or at its own
    /// size, passes keyboard, mouse and clipboard to the server and asks it to follow the window size when it can.
    /// </summary>
    internal sealed class VncDesktop : RemoteDesktopPanel
    {
        private const int ResizeDelay = 450;
        private readonly Timer resizeTimer;
        private readonly Dictionary<Keys, uint> pressed = new Dictionary<Keys, uint>();
        private VncClient client;
        private SshTunnel tunnel;
        private VncProfile profile;
        private string password;
        private bool connected, userClosed, sizeSynced;
        private int buttons;
        private string lastClipboard;
        private Form fullScreenForm;
        private Panel fullScreenBar;

        /// <summary>Asks for a password when the server wants one (wrongPassword: the last one was rejected); null cancels.</summary>
        public Func<VncProfile, bool, string> PasswordPrompt;

        public VncDesktop()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleName = "Удалённый рабочий стол VNC";
            resizeTimer = new Timer { Interval = ResizeDelay };
            resizeTimer.Tick += (sender, args) => { resizeTimer.Stop(); SyncSize(); };
        }

        public override bool IsConnected => connected && IsOpen;
        public VncProfile Profile => profile;
        protected override string TargetLine => profile == null ? "" : profile.Name + " · " + profile.Address +
            (profile.Via.Length == 0 ? "" : " · через " + profile.Via);
        private bool Fit => profile == null || profile.Scale != VncProfile.NativeScale;

        public void Connect(VncProfile profile, string password)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            this.password = password;
            Start();
        }

        public override void Reconnect()
        {
            if (profile != null) Start();
        }

        public override void Disconnect()
        {
            userClosed = true;
            client?.Close();
        }

        public override void OpenInWindow()
        {
            if (profile != null) VncWindow.Open(profile, password);
        }

        private void Start()
        {
            // Events arrive through BeginInvoke, which needs a handle even while the session is hidden.
            if (!IsHandleCreated) CreateHandle();
            Release();
            connected = false;
            userClosed = false;
            sizeSynced = false;
            HideEnded();
            UpdateStatus("подключение", true);
            var current = new VncClient { ViewOnly = profile.ViewOnly };
            client = current;
            current.Connected += () => OnUi(current, OnConnected);
            current.Updated += area => OnUi(current, () => OnUpdated(area));
            current.Resized += size => OnUi(current, OnResized);
            current.ClipboardReceived += text => OnUi(current, () => OnClipboard(text));
            current.Disconnected += error => OnUi(current, () => OnDisconnected(error));
            var target = profile;
            var secret = password;
            Task.Run(() =>
            {
                try
                {
                    string host = target.Host;
                    int port = target.Port;
                    if (target.Via.Length > 0)
                    {
                        var opened = SshTunnel.Open(target.Via, target.Host, target.Port);
                        if (!IsCurrent(current)) { opened.Dispose(); return; }
                        tunnel = opened;
                        host = "127.0.0.1";
                        port = opened.LocalPort;
                    }
                    current.Start(host, port, secret);
                }
                catch (Exception error) when (error is RfbException || error is System.IO.IOException ||
                    error is System.ComponentModel.Win32Exception || error is InvalidOperationException)
                {
                    OnUi(current, () => OnDisconnected(error is RfbException ? error : new RfbException(error.Message, error)));
                }
            });
        }

        private bool IsCurrent(VncClient candidate) => client == candidate && !IsDisposed;

        private void OnUi(VncClient source, Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => { if (IsCurrent(source)) action(); })); }
            catch (InvalidOperationException) { }
        }

        private void OnConnected()
        {
            connected = true;
            AutoScrollMinSize = Fit ? Size.Empty : new Size(client.Frame.Width, client.Frame.Height);
            UpdateStatus("подключён", true);
            Invalidate();
            if (profile.FullScreen) FullScreen();
            if (ContainsFocus) SendClipboard();
            else Focus();
        }

        private void OnUpdated(Rectangle area)
        {
            Invalidate(FrameToClient(area));
            if (!sizeSynced && client.CanResize && Fit) { sizeSynced = true; ScheduleSize(); }
        }

        private void OnResized()
        {
            AutoScrollMinSize = Fit ? Size.Empty : new Size(client.Frame.Width, client.Frame.Height);
            Invalidate();
        }

        private void OnClipboard(string text)
        {
            if (!profile.Clipboard || String.IsNullOrEmpty(text)) return;
            lastClipboard = text;
            try { Clipboard.SetText(text); }
            catch (ExternalException) { }
        }

        private void OnDisconnected(Exception error)
        {
            bool wasConnected = connected;
            connected = false;
            ReleaseKeys();
            tunnel?.Dispose();
            tunnel = null;
            LeaveFullScreen();
            if (error is RfbPasswordRequired || error is RfbAuthenticationFailed)
            {
                var entered = PasswordPrompt?.Invoke(profile, error is RfbAuthenticationFailed);
                if (!String.IsNullOrEmpty(entered))
                {
                    password = entered;
                    BeginInvoke(new Action(Start));
                    return;
                }
                UpdateStatus("отключён (ошибка)", false);
                ShowEnded("Подключение прервано", error.Message);
                return;
            }
            if (userClosed || error == null)
            {
                UpdateStatus("отключён", false);
                ShowEnded("Сеанс завершён", "Подключение закрыто.");
                return;
            }
            bool ended = wasConnected && error.InnerException is System.IO.EndOfStreamException;
            UpdateStatus(ended ? "отключён" : "отключён (ошибка)", false);
            ShowEnded(ended ? "Сеанс завершён" : "Подключение прервано", error.Message);
        }

        // --- drawing ------------------------------------------------------------------------------------------

        /// <summary>Where the framebuffer is drawn: scaled into the control keeping proportions, or at its size scrolled.</summary>
        private Rectangle FrameArea()
        {
            if (client == null) return Rectangle.Empty;
            int width = client.Frame.Width, height = client.Frame.Height;
            if (!Fit) return new Rectangle(AutoScrollPosition.X, AutoScrollPosition.Y, width, height);
            var area = ClientRectangle;
            if (area.Width <= 0 || area.Height <= 0) return Rectangle.Empty;
            double scale = Math.Min((double)area.Width / width, (double)area.Height / height);
            int w = (int)Math.Round(width * scale), h = (int)Math.Round(height * scale);
            return new Rectangle((area.Width - w) / 2, (area.Height - h) / 2, w, h);
        }

        private Rectangle FrameToClient(Rectangle area)
        {
            var target = FrameArea();
            if (client == null || target.IsEmpty) return ClientRectangle;
            double sx = (double)target.Width / client.Frame.Width, sy = (double)target.Height / client.Frame.Height;
            var result = new Rectangle(target.X + (int)Math.Floor(area.X * sx), target.Y + (int)Math.Floor(area.Y * sy),
                (int)Math.Ceiling(area.Width * sx) + 2, (int)Math.Ceiling(area.Height * sy) + 2);
            result.Inflate(1, 1);
            return result;
        }

        private Point ClientToFrame(Point point)
        {
            var target = FrameArea();
            if (client == null || target.Width <= 0 || target.Height <= 0) return Point.Empty;
            int x = (int)((point.X - target.X) * (double)client.Frame.Width / target.Width);
            int y = (int)((point.Y - target.Y) * (double)client.Frame.Height / target.Height);
            return new Point(Math.Max(0, Math.Min(client.Frame.Width - 1, x)), Math.Max(0, Math.Min(client.Frame.Height - 1, y)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.Clear(NativeTerminalView.Background);
            if (client == null || !connected) return;
            var target = FrameArea();
            lock (client.Frame.Lock)
            {
                var bitmap = client.Frame.Bitmap;
                if (bitmap == null) return;
                bool scaled = target.Width != bitmap.Width || target.Height != bitmap.Height;
                graphics.InterpolationMode = scaled ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(bitmap, target);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsConnected && Fit && client.CanResize) ScheduleSize();
        }

        private void ScheduleSize()
        {
            resizeTimer.Stop();
            resizeTimer.Start();
        }

        /// <summary>Servers with ExtendedDesktopSize (TigerVNC, x11vnc -xrandr) take the size of the window.</summary>
        private void SyncSize()
        {
            if (!IsConnected || !Fit || ClientSize.Width < 64 || ClientSize.Height < 64) return;
            if (client.Frame.Width == ClientSize.Width && client.Frame.Height == ClientSize.Height) return;
            client.RequestDesktopSize(ClientSize.Width, ClientSize.Height);
        }

        // --- input -----------------------------------------------------------------------------------------------

        protected override bool IsInputKey(Keys keyData) => IsConnected || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!IsConnected) return;
            if (e.KeyCode == Keys.Enter && e.Control && e.Alt && fullScreenForm != null) { LeaveFullScreen(); e.Handled = e.SuppressKeyPress = true; return; }
            // Ctrl+Alt on Windows is AltGr: let KeyPress deliver the character it produces.
            bool altGr = e.Control && e.Alt && !VncKeys.IsModifier(e.KeyCode) && (e.KeyCode < Keys.F1 || e.KeyCode > Keys.F24);
            var keysym = VncKeys.FromKeyDown(e.KeyCode, e.Control && !altGr, e.Alt && !altGr);
            if (keysym == null) return;
            if (!pressed.ContainsKey(e.KeyCode)) pressed[e.KeyCode] = keysym.Value;
            client.SendKey(keysym.Value, true);
            e.Handled = true;
            if (!VncKeys.IsModifier(e.KeyCode)) e.SuppressKeyPress = true;
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (!IsConnected || char.IsControl(e.KeyChar)) return;
            uint keysym = VncKeys.FromChar(e.KeyChar);
            client.SendKey(keysym, true);
            client.SendKey(keysym, false);
            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (client == null || !pressed.TryGetValue(e.KeyCode, out var keysym)) return;
            pressed.Remove(e.KeyCode);
            client.SendKey(keysym, false);
            e.Handled = true;
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            ReleaseKeys();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            SendClipboard();
        }

        private void ReleaseKeys()
        {
            if (client != null) foreach (var keysym in pressed.Values) client.SendKey(keysym, false);
            pressed.Clear();
        }

        private void SendClipboard()
        {
            if (!IsConnected || !profile.Clipboard) return;
            try
            {
                if (!Clipboard.ContainsText()) return;
                var text = Clipboard.GetText();
                if (text == lastClipboard) return;
                lastClipboard = text;
                client.SendClipboard(text);
            }
            catch (ExternalException) { }
        }

        private static int Mask(MouseButtons button) =>
            button == MouseButtons.Left ? 1 : button == MouseButtons.Middle ? 2 : button == MouseButtons.Right ? 4 : 0;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (!IsConnected) return;
            buttons |= Mask(e.Button);
            var point = ClientToFrame(e.Location);
            client.SendPointer(buttons, point.X, point.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!IsConnected) return;
            buttons &= ~Mask(e.Button);
            var point = ClientToFrame(e.Location);
            client.SendPointer(buttons, point.X, point.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (fullScreenBar != null) fullScreenBar.Visible = e.Y < 4 || (fullScreenBar.Visible && e.Y < fullScreenBar.Height);
            if (!IsConnected) return;
            var point = ClientToFrame(e.Location);
            client.SendPointer(buttons, point.X, point.Y);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!IsConnected || (!Fit && ModifierKeys == Keys.Shift)) { base.OnMouseWheel(e); return; }
            var point = ClientToFrame(e.Location);
            int steps = Math.Max(1, Math.Abs(e.Delta) / 120), wheel = e.Delta > 0 ? 8 : 16;
            for (int i = 0; i < steps; i++)
            {
                client.SendPointer(buttons | wheel, point.X, point.Y);
                client.SendPointer(buttons, point.X, point.Y);
            }
        }

        // --- full screen -------------------------------------------------------------------------------------------

        public override void FullScreen()
        {
            if (fullScreenForm != null || !IsConnected || Parent == null) return;
            var home = Parent;
            int index = home.Controls.GetChildIndex(this);
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                Bounds = Screen.FromControl(this).Bounds, ShowInTaskbar = false, TopMost = true,
                BackColor = NativeTerminalView.Background, Text = profile.Name + " — VNC", KeyPreview = true,
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            };
            var bar = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(this, 40), BackColor = Color.FromArgb(32, 44, 60), Visible = false };
            var title = new Label { Text = profile.Name + "  ·  " + profile.Address, ForeColor = Color.Gainsboro, AutoSize = false,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };
            var leave = Theme.Button("Выйти из полноэкранного режима", 250, glyph: Glyph.MoveLeft);
            leave.Dock = DockStyle.Right;
            leave.Click += (sender, args) => LeaveFullScreen();
            bar.Controls.Add(title);
            bar.Controls.Add(leave);
            form.Controls.Add(this);
            form.Controls.Add(bar);
            form.FormClosed += (sender, args) =>
            {
                fullScreenForm = null;
                fullScreenBar = null;
                if (IsDisposed || home.IsDisposed) return;
                home.Controls.Add(this);
                home.Controls.SetChildIndex(this, index);
                Focus();
            };
            fullScreenForm = form;
            fullScreenBar = bar;
            form.Show(FindForm());
            Focus();
        }

        private void LeaveFullScreen()
        {
            fullScreenForm?.Close();
        }

        private void Release()
        {
            resizeTimer.Stop();
            ReleaseKeys();
            var previous = client;
            client = null;
            previous?.Dispose();
            tunnel?.Dispose();
            tunnel = null;
            buttons = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                LeaveFullScreen();
                Release();
                resizeTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
