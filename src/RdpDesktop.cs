using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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

    internal sealed class RdpDesktop : Panel
    {
        private const int ResizeDelay = 450, RetryDelay = 1500, MaxRetries = 6;
        private static readonly Guid EventInterface = new Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6");
        private readonly List<KeyValuePair<int, Delegate>> handlers = new List<KeyValuePair<int, Delegate>>();
        private readonly Panel overlay, overlayReason;
        private readonly Label overlayTitle, overlayDetail;
        private readonly FlowLayoutPanel overlayActions;
        private readonly Timer resizeTimer;
        private RdpClientHost host;
        private object client;
        private RdpProfile profile;
        private RdpCredential credential;
        private Size appliedSize;
        private int appliedDpi, retries;
        private bool loggedIn, fullScreenMode;

        // Server authentication level: 2 warns about an unverified server. Tests against a throwaway server lower it.
        internal static uint ServerAuthentication = 2;

        public RdpDesktop()
        {
            Dock = DockStyle.Fill;
            BackColor = NativeTerminalView.Background;
            AccessibleName = "Удалённый рабочий стол";

            // Laid out by FitOverlay: centred when it fits, otherwise the title and buttons stay visible and
            // only the reason scrolls (the RDP client's descriptions run to several paragraphs).
            overlay = new Panel { Dock = DockStyle.Fill, BackColor = NativeTerminalView.Background, Visible = false };
            overlayTitle = new Label { AutoSize = true, Font = Theme.Title, ForeColor = Color.Gainsboro };
            overlayDetail = new Label { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.DarkGray,
                TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty };
            overlayReason = new Panel { AutoScroll = true, BackColor = NativeTerminalView.Background };
            overlayReason.Controls.Add(overlayDetail);
            Theme.DarkScrollBars(overlayReason);
            overlayActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty,
                BackColor = NativeTerminalView.Background };
            var reconnect = Theme.Button("Переподключить", 160, true, Glyph.Refresh);
            reconnect.Click += (sender, args) => Reconnect();
            var close = Theme.Button("Закрыть сеанс", 140);
            close.Click += (sender, args) => CloseRequested?.Invoke();
            // The buttons clear their corners with their own BackColor (Canvas by default).
            reconnect.BackColor = close.BackColor = NativeTerminalView.Background;
            overlayActions.Controls.AddRange(new Control[] { reconnect, close });
            overlay.Controls.AddRange(new Control[] { overlayTitle, overlayReason, overlayActions });
            overlay.Resize += (sender, args) => FitOverlay();
            overlay.VisibleChanged += (sender, args) => FitOverlay();
            Controls.Add(overlay);

            resizeTimer = new Timer { Interval = ResizeDelay };
            resizeTimer.Tick += (sender, args) => { resizeTimer.Stop(); SyncDisplay(); };
        }

        public event Action Changed;
        public event Action CloseRequested;
        public string Status { get; private set; } = "подключение";
        public bool IsOpen { get; private set; } = true;
        public bool IsConnected => loggedIn && IsOpen;
        public RdpProfile Profile => profile;
        public RdpCredential Credential => credential;

        public void Connect(RdpProfile profile, RdpCredential credential)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            this.credential = credential ?? new RdpCredential(profile.User, profile.Domain, "");
            Start();
        }

        public void Reconnect()
        {
            if (profile == null) return;
            try { Start(); }
            catch (Exception error) when (error is ExternalException || error is TargetInvocationException ||
                error is InvalidOperationException || error is ArgumentException)
            {
                ShowEnded("Не удалось подключиться", (error.InnerException ?? error).Message);
                UpdateStatus("ошибка подключения", false);
            }
        }

        public void Disconnect()
        {
            if (client == null || !IsOpen) return;
            try { Call(client, "Disconnect"); }
            catch (Exception error) when (IsComError(error)) { }
        }

        public void FullScreen()
        {
            if (client != null && IsOpen) Set(client, "FullScreen", true);
        }

        private void Start()
        {
            ReleaseClient();
            loggedIn = false;
            fullScreenMode = false;
            retries = 0;
            appliedSize = Size.Empty;
            overlay.Visible = false;
            host = new RdpClientHost();
            Controls.Add(host);
            host.BringToFront();
            host.SizeChanged += (sender, args) => ScheduleSync();
            host.CreateControl();
            client = host.Client;

            var settings = Get(client, "AdvancedSettings8");
            var size = InitialSize();
            Set(client, "Server", profile.Host);
            var account = LogonAccount(credential.User, credential.Domain);
            Set(client, "UserName", account.Key);
            Set(client, "Domain", account.Value);
            Set(client, "DesktopWidth", size.Width);
            Set(client, "DesktopHeight", size.Height);
            TrySet(client, "ConnectingText", "Подключение к " + profile.Address + "…");
            TrySet(client, "DisconnectedText", " ");
            TrySet(client, "FullScreenTitle", profile.Name + " — RDP");
            Set(settings, "RDPPort", profile.Port);
            Set(settings, "SmartSizing", true);
            Set(settings, "EnableCredSspSupport", true);
            Set(settings, "AuthenticationLevel", ServerAuthentication);
            Set(settings, "RedirectClipboard", profile.Clipboard);
            Set(settings, "RedirectDrives", false);
            Set(settings, "RedirectPrinters", false);
            TrySet(settings, "EnableAutoReconnect", true);
            TrySet(settings, "GrabFocusOnConnect", true);
            TrySet(settings, "BitmapPersistence", 1);
            TrySet(settings, "DisplayConnectionBar", true);
            if (credential.Password.Length > 0) SetPassword(client, settings, credential.Password);
            ApplyScale(client, DeviceDpi);
            appliedDpi = DeviceDpi;

            Listen(1, new Action(() => UpdateStatus("подключение", true)));
            // Windows servers report OnLoginComplete; xrdp and some appliances only report OnConnected.
            Listen(2, new Action(OnLogin));
            Listen(3, new Action(OnLogin));
            Listen(4, new Action<int>(OnDisconnected));
            // The client shows full screen in a window of its own, so the session follows that window, not ours.
            Listen(5, new Action(() => { fullScreenMode = true; ScheduleSync(); }));
            Listen(6, new Action(() => { fullScreenMode = false; ScheduleSync(); }));
            Listen(10, new Action<int>(code =>
            {
                loggedIn = false;
                resizeTimer.Stop();
                ShowEnded("Ошибка RDP-клиента", "Код ошибки: " + code);
                UpdateStatus("ошибка подключения", false);
            }));
            UpdateStatus("подключение", true);
            Call(client, "Connect");
        }

        private void OnLogin()
        {
            bool first = !loggedIn;
            loggedIn = true;
            if (first && host != null) appliedSize = profile.Width != 0 ? new Size(profile.Width, profile.Height) : Fit(host.ClientSize);
            UpdateStatus("подключён", true);
            if (first && profile.FullScreen && Visible)
            {
                try { FullScreen(); }
                catch (Exception error) when (IsComError(error)) { }
            }
            ScheduleSync();
        }

        private void OnDisconnected(int reason)
        {
            loggedIn = false;
            resizeTimer.Stop();
            string description = Describe(reason);
            bool normal = reason <= 3;
            UpdateStatus(normal ? "отключён" : "отключён (код " + reason + ")", false);
            ShowEnded(normal ? "Сеанс завершён" : "Подключение прервано", description);
        }

        private string Describe(int reason)
        {
            string text = "";
            try
            {
                uint extended = Convert.ToUInt32(Get(client, "ExtendedDisconnectReason"));
                text = Convert.ToString(Call(client, "GetErrorDescription", (uint)reason, extended)) ?? "";
            }
            catch (Exception error) when (IsComError(error) || error is InvalidCastException || error is OverflowException) { }
            if (reason <= 3 && text.Length == 0) text = "Удалённый сеанс закрыт.";
            return (text.Trim() + (reason > 3 ? "\nКод: " + reason : "")).Trim();
        }

        private void ShowEnded(string title, string detail)
        {
            if (IsDisposed) return;
            overlayTitle.Text = title;
            overlayDetail.Text = (profile == null ? "" : profile.Name + " · " + profile.Address + "\n") + detail;
            if (host != null) host.Visible = false;
            overlay.Visible = true;
            overlay.BringToFront();
            FitOverlay();
        }

        /// <summary>
        /// Centres the title, the reason and the buttons. When they do not fit, the reason gets the height that is
        /// left and scrolls, so «Переподключить» and «Закрыть сеанс» are always on screen.
        /// </summary>
        private void FitOverlay()
        {
            if (IsDisposed) return;
            var area = overlay.ClientSize;
            int edge = Theme.Scaled(this, 12), small = Theme.Scaled(this, 6), large = Theme.Scaled(this, 16);
            int width = Math.Min(Theme.Scaled(this, 560), area.Width - Theme.Scaled(this, 48));
            overlayDetail.MaximumSize = new Size(Math.Max(Theme.Scaled(this, 200), width), 0);
            var title = overlayTitle.PreferredSize;
            var detail = overlayDetail.GetPreferredSize(Size.Empty);
            var buttons = overlayActions.PreferredSize;
            int fixedHeight = title.Height + small + large + buttons.Height;
            int reasonHeight = Math.Max(0, Math.Min(detail.Height, area.Height - 2 * edge - fixedHeight));
            bool scrolls = reasonHeight < detail.Height;
            int reasonWidth = detail.Width + (scrolls ? SystemInformation.VerticalScrollBarWidth : 0);
            int top = Math.Max(edge, (area.Height - fixedHeight - reasonHeight) / 2);
            int Centre(int itemWidth) => Math.Max(0, (area.Width - itemWidth) / 2);

            overlay.SuspendLayout();
            overlayTitle.Location = new Point(Centre(title.Width), top);
            top += title.Height + small;
            overlayReason.Bounds = new Rectangle(Centre(reasonWidth), top, reasonWidth, reasonHeight);
            overlayDetail.Location = new Point(0, overlayReason.AutoScrollPosition.Y);
            top += reasonHeight + large;
            overlayActions.Location = new Point(Centre(buttons.Width), top);
            overlay.ResumeLayout(false);
        }

        private void UpdateStatus(string status, bool open)
        {
            Status = status;
            IsOpen = open;
            Changed?.Invoke();
        }

        /// <summary>
        /// The user and domain sent through CredSSP. With an empty domain Windows qualifies the name with this
        /// computer's own name (for example PC\Administrator), which a remote domain controller rejects. "." names the
        /// account database of the remote server itself: local accounts there, domain accounts on a domain controller.
        /// </summary>
        internal static KeyValuePair<string, string> LogonAccount(string user, string domain)
        {
            user = (user ?? "").Trim();
            domain = (domain ?? "").Trim();
            if (domain.Length == 0)
            {
                int slash = user.IndexOf('\\');
                if (slash > 0) return new KeyValuePair<string, string>(user.Substring(slash + 1), user.Substring(0, slash));
                if (user.Length > 0 && user.IndexOf('@') < 0) domain = ".";
            }
            return new KeyValuePair<string, string>(user, domain);
        }

        private Size InitialSize()
        {
            if (profile.Width != 0) return new Size(profile.Width, profile.Height);
            var area = host.ClientSize.Width >= 200 && host.ClientSize.Height >= 200 ? host.ClientSize : ClientSize;
            return Fit(area);
        }

        private static Size Fit(Size area) =>
            new Size(Math.Max(640, Math.Min(area.Width, 8192)) & ~1, Math.Max(480, Math.Min(area.Height, 8192)) & ~1);

        private void ScheduleSync()
        {
            if (!loggedIn || profile == null) return;
            resizeTimer.Stop();
            resizeTimer.Interval = ResizeDelay;
            resizeTimer.Start();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ScheduleSync();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) ScheduleSync();
        }

        private void SyncDisplay()
        {
            if (!loggedIn || client == null || host == null || !host.Visible) return;
            // Full screen uses the whole monitor even for a fixed size: the client's full-screen window does not
            // stretch the picture, so a smaller desktop would sit in black borders. Leaving it restores the fixed size.
            Size size;
            if (fullScreenMode) size = Fit(Screen.FromControl(host).Bounds.Size);
            else if (profile.Width != 0) size = new Size(profile.Width, profile.Height);
            else
            {
                var area = host.ClientSize;
                if (area.Width < 200 || area.Height < 200) return;
                size = Fit(area);
            }
            int dpi = DeviceDpi;
            if (size == appliedSize && dpi == appliedDpi) return;
            try
            {
                Call(client, "UpdateSessionDisplaySettings", (uint)size.Width, (uint)size.Height,
                    (uint)(size.Width * 254 / (dpi * 10)), (uint)(size.Height * 254 / (dpi * 10)), 0u,
                    (uint)DesktopScale(dpi), (uint)DeviceScale(dpi));
                appliedSize = size;
                appliedDpi = dpi;
                retries = 0;
            }
            catch (Exception error) when (IsComError(error))
            {
                // The display channel opens shortly after logon; older servers never support it and keep smart sizing.
                if (++retries > MaxRetries) return;
                resizeTimer.Interval = RetryDelay;
                resizeTimer.Start();
            }
        }

        private static int DesktopScale(int dpi)
        {
            int percent = dpi * 100 / 96, best = 100;
            foreach (int option in new[] { 100, 125, 150, 175, 200, 250, 300, 400, 500 })
                if (Math.Abs(option - percent) < Math.Abs(best - percent)) best = option;
            return best;
        }

        private static int DeviceScale(int dpi) => dpi >= 192 ? 180 : dpi >= 134 ? 140 : 100;

        private static void ApplyScale(object client, int dpi)
        {
            if (dpi <= 96) return;
            try
            {
                var extended = (IMsRdpExtendedSettings)client;
                object desktop = (uint)DesktopScale(dpi), device = (uint)DeviceScale(dpi);
                extended.PutProperty("DesktopScaleFactor", ref desktop);
                extended.PutProperty("DeviceScaleFactor", ref device);
            }
            catch (Exception error) when (IsComError(error) || error is InvalidCastException) { }
        }

        private static void SetPassword(object client, object settings, string password)
        {
            try { Set(settings, "ClearTextPassword", password); return; }
            catch (Exception error) when (IsComError(error)) { }
            ((IMsTscNonScriptable)client).PutClearTextPassword(password);
        }

        private void Listen(int id, Delegate handler)
        {
            ComEventsHelper.Combine(client, EventInterface, id, handler);
            handlers.Add(new KeyValuePair<int, Delegate>(id, handler));
        }

        private void ReleaseClient()
        {
            resizeTimer.Stop();
            if (host == null) return;
            var closing = host;
            var instance = client;
            host = null;
            client = null;
            try
            {
                foreach (var handler in handlers) ComEventsHelper.Remove(instance, EventInterface, handler.Key, handler.Value);
                if (IsOpen) Call(instance, "Disconnect");
            }
            catch (Exception error) when (IsComError(error))
            {
                // AxHost can release its COM wrapper when destroying the parent window's handle.
            }
            handlers.Clear();
            Controls.Remove(closing);
            closing.Dispose();
        }

        private static bool IsComError(Exception error) => error is COMException || error is TargetInvocationException ||
            error is InvalidComObjectException || error is MissingMethodException || error is ArgumentException;

        private static object Get(object target, string name) =>
            target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

        private static void Set(object target, string name, object value) =>
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });

        private static void TrySet(object target, string name, object value)
        {
            try { Set(target, name, value); }
            catch (Exception error) when (IsComError(error)) { }
        }

        private static object Call(object target, string name, params object[] args) =>
            target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseClient();
                resizeTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class RdpClientHost : AxHost
        {
            // MsRdpClient9NotSafeForScripting: allows passwords and per-session display updates.
            public RdpClientHost() : base("8B918B82-7985-4C24-89DF-C33AD2BBFBCD")
            {
                Dock = DockStyle.Fill;
                AccessibleName = "Удалённый рабочий стол";
            }

            public object Client => GetOcx();
        }

        [ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMsTscNonScriptable
        {
            void PutClearTextPassword([MarshalAs(UnmanagedType.BStr)] string password);
        }

        [ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMsRdpExtendedSettings
        {
            void PutProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);
        }
    }
}
