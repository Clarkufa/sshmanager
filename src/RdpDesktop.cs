using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Packet = SshManager.RdpWorker.Packet;

namespace SshManager
{
    internal sealed class RdpCredential
    {
        public readonly string User, Domain, Password;

        public RdpCredential(string user, string domain, string password)
        {
            User = user ?? "";
            Domain = domain ?? "";
            Password = password ?? "";
        }

        public static RdpCredential FromAccount(string account, string fallbackDomain, string password)
        {
            account = (account ?? "").Trim();
            int slash = account.IndexOf('\\');
            return slash > 0 ? new RdpCredential(account.Substring(slash + 1), account.Substring(0, slash), password)
                : new RdpCredential(account, fallbackDomain, password);
        }
    }

    internal sealed class RdpDesktop : RemoteDesktopPanel
    {
        private const int WmClipboardUpdate = 0x031D, WmKeyDown = 0x0100, WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
        private readonly Timer updates = new Timer { Interval = 33 };
        private readonly Timer resize = new Timer { Interval = 350 };
        private readonly HashSet<int> pressed = new HashSet<int>();
        private RdpWorker worker;
        private RdpProfile profile;
        private RdpCredential credential;
        private Bitmap frame;
        private Form fullScreenForm;
        private Button fullScreenExit;
        private string lastClipboard, error;
        private bool connected, disconnecting, awaitingPrompt;
        private Point? pointer;
        private IntPtr clipboardWindow;
        private long disconnectStarted;

        internal Func<string, string, bool> ConfirmCertificate;
        public Size DesktopSize => frame?.Size ?? Size.Empty;
        public override bool IsConnected => connected && IsOpen;
        protected override string TargetLine => profile == null ? "" : profile.Name + " · " + profile.Address;

        public RdpDesktop()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            TabStop = true;
            updates.Tick += (sender, args) => RefreshDesktop();
            resize.Tick += (sender, args) =>
            {
                resize.Stop();
                if (IsConnected) SendSize();
            };
        }

        public override void OpenInWindow()
        {
            if (profile != null) RdpWindow.Open(profile, credential);
        }

        public void Connect(RdpProfile profile, RdpCredential credential)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            this.credential = credential ?? new RdpCredential(profile.User, profile.Domain, "");
            Start();
        }

        private void Start()
        {
            Release();
            connected = disconnecting = awaitingPrompt = false;
            lastClipboard = error = null;
            HideEnded();
            UpdateStatus("подключение", true);
            try
            {
                // The handle is needed even when this tab is hidden while the connection starts.
                var handle = Handle;
                var next = new RdpWorker();
                worker = next;
                next.Received += (type, bytes) => OnUi(next, () => Receive(type, bytes));
                next.Ended += reason => OnUi(next, () => Finish(error ?? reason));
                next.Start(Arguments());
                updates.Start();
            }
            catch (Exception ex) when (ex is IOException || ex is Win32Exception ||
                                       ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                Finish(ex.Message);
            }
        }

        private IEnumerable<string> Arguments()
        {
            var resolution = TargetSize();
            yield return "/v:" + profile.Address;
            yield return "/size:" + resolution.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                         resolution.Height.ToString(CultureInfo.InvariantCulture);
            yield return "/bpp:32";
            var layout = new StringBuilder(9);
            if (GetKeyboardLayoutName(layout)) yield return "/kbd:layout:0x" + layout;
            yield return "/dynamic-resolution";
            yield return "/timeout:15000";
            yield return "+auto-reconnect";
            yield return "/auto-reconnect-max-retries:3";
            yield return profile.Clipboard ? "+clipboard" : "-clipboard";
            if (credential.User.Length != 0) yield return "/u:" + credential.User;
            if (credential.Domain.Length != 0) yield return "/d:" + credential.Domain;
            if (credential.Password.Length != 0) yield return "/p:" + credential.Password;
            if (profile.Options.Gateway.Length != 0)
            {
                yield return "/gateway:g:" + profile.Options.Gateway;
                if (profile.Options.GatewayUser.Length != 0)
                    yield return "/gu:" + profile.Options.GatewayUser;
            }
            if (profile.Options.Printers) yield return "/printer";
            foreach (string folder in profile.Options.Drives)
                yield return "/sshm-folder:" + (folder.Length == 2 ? folder + "\\" : folder);
        }

        private void OnUi(RdpWorker source, Action action)
        {
            try
            {
                BeginInvoke(new Action(() => { if (worker == source && !IsDisposed) action(); }));
            }
            catch (InvalidOperationException) { }
        }

        private void Receive(Packet type, byte[] bytes)
        {
            switch (type)
            {
                case Packet.Connected:
                    connected = true;
                    UpdateStatus("подключён", true);
                    if (profile.FullScreen && Visible) FullScreen();
                    SendSize();
                    SyncFocus();
                    break;
                case Packet.Error:
                    if (bytes.Length == 4) error = "Код FreeRDP: 0x" + BitConverter.ToUInt32(bytes, 0).ToString("X8");
                    break;
                case Packet.Certificate:
                    var details = Encoding.UTF8.GetString(bytes).Split(new[] { '\n' }, 3);
                    if (details.Length != 3) break;
                    awaitingPrompt = true;
                    try
                    {
                        var source = worker;
                        bool allow = ConfirmCertificate != null
                            ? ConfirmCertificate(details[1], details[2])
                            : MessageBox.Show(FindForm(),
                                (details[0] == "1" ? "Сертификат RDP изменился." : "Не удалось проверить сертификат RDP.") +
                                "\n\n" + details[1] + "\n" + details[2] + "\n\nПродолжить один раз?",
                                "Сертификат RDP", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                        if (source == worker) worker?.Send(Packet.TrustCertificate, new[] { (byte)(allow ? 1 : 0) });
                    }
                    finally { awaitingPrompt = false; }
                    break;
                case Packet.RemoteClipboard:
                    if (profile.Clipboard && ContainsFocus)
                    {
                        try
                        {
                            string text = Encoding.UTF8.GetString(bytes);
                            if (text.Length == 0) Clipboard.Clear();
                            else Clipboard.SetText(text);
                            lastClipboard = text;
                        }
                        catch (ExternalException) { }
                    }
                    break;
                case Packet.Authenticate:
                    if (bytes.Length != 1) break;
                    bool gateway = bytes[0] != 0;
                    var peer = worker;
                    awaitingPrompt = true;
                    try
                    {
                        var login = gateway
                            ? RdpLogonDialog.Ask(FindForm(), profile.Name, profile.Options.Gateway,
                                profile.Options.GatewayUser, "")
                            : RdpLogonDialog.Ask(FindForm(), profile.Name, profile.Address, credential.User, credential.Domain);
                        if (peer == worker)
                            peer.Send(Packet.Credentials, login == null ? new byte[0] :
                                Encoding.UTF8.GetBytes(login.User + "\0" + login.Domain + "\0" + login.Password + "\0"));
                    }
                    finally { awaitingPrompt = false; }
                    break;
            }
        }

        private void RefreshDesktop()
        {
            if (worker == null) return;
            if ((disconnecting && StopwatchSeconds(disconnectStarted) > 3) ||
                (!awaitingPrompt && worker.SecondsSinceActivity > (connected ? 25 : 45)))
            {
                Finish(disconnecting ? null : "Процесс FreeRDP перестал отвечать.");
                return;
            }
            if (pointer.HasValue)
            {
                SendPointer(0x0800, pointer.Value);
                pointer = null;
            }
            if (Visible && worker.ReadFrame(ref frame)) Invalidate();
        }

        private static double StopwatchSeconds(long since) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - since) / (double)System.Diagnostics.Stopwatch.Frequency;

        private Size TargetSize()
        {
            if (profile.Width > 0 && fullScreenForm == null) return new Size(profile.Width, profile.Height);
            var size = fullScreenForm?.ClientSize ?? ClientSize;
            return new Size(Math.Max(640, Math.Min(8192, size.Width)) & ~1, Math.Max(480, Math.Min(8192, size.Height)));
        }

        private void SendSize()
        {
            var size = TargetSize();
            worker?.Send(Packet.Resize, size.Width, size.Height, DeviceDpi);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ScheduleResize();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ScheduleResize();
        }

        private void ScheduleResize()
        {
            if (resize == null || !IsConnected) return;
            resize.Stop();
            resize.Start();
        }

        private Rectangle ScreenArea
        {
            get
            {
                if (frame == null || Width == 0 || Height == 0) return Rectangle.Empty;
                double scale = Math.Min((double)Width / frame.Width, (double)Height / frame.Height);
                int w = Math.Max(1, (int)(frame.Width * scale)), h = Math.Max(1, (int)(frame.Height * scale));
                return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (frame == null) return;
            var area = ScreenArea;
            if (area.Size == frame.Size) e.Graphics.DrawImageUnscaled(frame, area.Location);
            else
            {
                e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
                e.Graphics.DrawImage(frame, area);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            clipboardWindow = Handle;
            AddClipboardFormatListener(clipboardWindow);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (clipboardWindow != IntPtr.Zero) RemoveClipboardFormatListener(clipboardWindow);
            clipboardWindow = IntPtr.Zero;
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message msg)
        {
            if (msg.Msg == WmClipboardUpdate && ContainsFocus) SendClipboard();
            if (!SendKey(msg)) base.WndProc(ref msg);
        }

        public override bool PreProcessMessage(ref Message msg) => SendKey(msg) || base.PreProcessMessage(ref msg);

        private bool SendKey(Message msg)
        {
            bool up = msg.Msg == WmKeyUp || msg.Msg == WmSysKeyUp;
            if (!IsConnected || (!up && msg.Msg != WmKeyDown && msg.Msg != WmSysKeyDown)) return false;
            var key = (Keys)(int)msg.WParam;
            if (key == Keys.Cancel && (ModifierKeys & (Keys.Control | Keys.Alt)) == (Keys.Control | Keys.Alt))
            {
                if (!up)
                {
                    if (fullScreenForm == null) FullScreen();
                    else fullScreenForm.Close();
                }
                return true;
            }
            int scan = (int)(msg.LParam.ToInt64() >> 16) & 0x1ff;
            if (scan == 0) return true;
            if (up) pressed.Remove(scan);
            else pressed.Add(scan);
            worker.Send(Packet.Key, (up ? 0x8000 : 0) | (scan & 0x100), scan & 0xff);
            return true;
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            SyncFocus();
        }

        private void SyncFocus()
        {
            if (!IsConnected || !ContainsFocus) return;
            int toggles = (Control.IsKeyLocked(Keys.Scroll) ? 1 : 0) |
                          (Control.IsKeyLocked(Keys.NumLock) ? 2 : 0) |
                          (Control.IsKeyLocked(Keys.CapsLock) ? 4 : 0);
            worker.Send(Packet.Focus, toggles);
            SendClipboard();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            foreach (int scan in pressed)
                worker?.Send(Packet.Key, 0x8000 | (scan & 0x100), scan & 0xff);
            pressed.Clear();
            base.OnLostFocus(e);
        }

        private void SendClipboard()
        {
            if (!IsConnected || !profile.Clipboard) return;
            try
            {
                string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (text == lastClipboard || Encoding.UTF8.GetByteCount(text) > 1024 * 1024) return;
                if (worker.Send(Packet.LocalClipboard, Encoding.UTF8.GetBytes(text))) lastClipboard = text;
            }
            catch (ExternalException) { }
        }

        private void SendPointer(int flags, Point point)
        {
            var area = ScreenArea;
            if (!IsConnected || area.IsEmpty) return;
            int x = Math.Max(0, Math.Min(frame.Width - 1, (point.X - area.X) * frame.Width / area.Width));
            int y = Math.Max(0, Math.Min(frame.Height - 1, (point.Y - area.Y) * frame.Height / area.Height));
            worker.Send(Packet.Mouse, flags, x, y);
        }

        private static int ButtonFlags(MouseButtons button)
        {
            switch (button)
            {
                case MouseButtons.Left: return 0x1000;
                case MouseButtons.Right: return 0x2000;
                case MouseButtons.Middle: return 0x4000;
                case MouseButtons.XButton1: return 0x10001;
                case MouseButtons.XButton2: return 0x10002;
                default: return 0;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            pointer = null;
            if (ScreenArea.Contains(e.Location)) SendPointer(ButtonFlags(e.Button) | 0x8000, e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            pointer = null;
            SendPointer(ButtonFlags(e.Button), e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (fullScreenExit != null) fullScreenExit.Visible = e.Y < 4 || (fullScreenExit.Visible && e.Y < fullScreenExit.Height);
            pointer = e.Location;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int delta = Math.Max(-255, Math.Min(255, e.Delta));
            SendPointer(0x0200 | (delta & 0x1ff), e.Location);
        }

        public override void FullScreen()
        {
            if (!IsConnected || fullScreenForm != null || Parent == null) return;
            var home = Parent;
            var owner = FindForm();
            int index = home.Controls.GetChildIndex(this);
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                Bounds = Screen.FromControl(this).Bounds, ShowInTaskbar = false, TopMost = true,
                BackColor = BackColor, Text = profile.Name + " — RDP"
            };
            var exit = Theme.Button("Выйти из полноэкранного режима", 250, glyph: Glyph.MoveLeft);
            exit.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            exit.Left = form.ClientSize.Width - exit.Width;
            exit.Visible = false;
            exit.Click += (sender, args) => form.Close();
            fullScreenForm = form;
            fullScreenExit = exit;
            form.Controls.Add(this);
            form.Controls.Add(exit);
            exit.BringToFront();
            form.FormClosed += (sender, args) =>
            {
                fullScreenForm = null;
                fullScreenExit = null;
                if (IsDisposed || home.IsDisposed || home.Disposing) return;
                home.Controls.Add(this);
                home.Controls.SetChildIndex(this, Math.Min(index, home.Controls.Count - 1));
                Focus();
                ScheduleResize();
            };
            form.Show(owner);
            Focus();
            ScheduleResize();
        }

        public override void Reconnect()
        {
            if (profile != null) Start();
        }

        public override void Disconnect()
        {
            if (worker == null) return;
            disconnecting = true;
            disconnectStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (connected && worker.Send(Packet.Stop)) UpdateStatus("отключение", true);
            else Finish(null);
        }

        private void Finish(string reason)
        {
            bool normal = disconnecting || (reason == null && connected);
            connected = false;
            Release();
            fullScreenForm?.Close();
            UpdateStatus(normal ? "отключён" : "ошибка подключения", false);
            ShowEnded(normal ? "Сеанс завершён" : "Подключение прервано", normal ? "" : reason ?? "Процесс FreeRDP завершился.");
        }

        private void Release()
        {
            updates.Stop();
            resize.Stop();
            var old = worker;
            worker = null;
            old?.Dispose();
            pressed.Clear();
            pointer = null;
            frame?.Dispose();
            frame = null;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fullScreenForm?.Close();
                Release();
                updates.Dispose();
                resize.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        private static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool RemoveClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetKeyboardLayoutName(StringBuilder layout);
    }
}
