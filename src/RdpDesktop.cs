using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class RdpDesktop : AxHost
    {
        private static readonly Guid EventInterface = new Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6");
        private object client;
        private Action connecting, login;
        private Action<int> disconnected;

        public RdpDesktop() : base("8B918B82-7985-4C24-89DF-C33AD2BBFBCD")
        {
            Dock = DockStyle.Fill;
            AccessibleName = "Удалённый рабочий стол";
        }

        public event Action Changed;
        public string Status { get; private set; } = "подключение";
        public bool IsOpen { get; private set; } = true;

        public void Connect(RdpProfile profile)
        {
            CreateControl();
            client = GetOcx();
            var settings = Get(client, "AdvancedSettings8");
            Set(client, "Server", profile.Host);
            Set(client, "UserName", profile.User);
            Set(client, "Domain", profile.Domain);
            Set(client, "DesktopWidth", profile.Width == 0 ? Math.Max(640, Math.Min(Width, 4096)) : profile.Width);
            Set(client, "DesktopHeight", profile.Height == 0 ? Math.Max(480, Math.Min(Height, 2160)) : profile.Height);
            Set(settings, "RDPPort", profile.Port);
            Set(settings, "SmartSizing", true);
            Set(settings, "EnableCredSspSupport", true);
            Set(settings, "AuthenticationLevel", 2u);
            Set(settings, "RedirectClipboard", profile.Clipboard);
            Set(settings, "RedirectDrives", false);
            Set(settings, "RedirectPrinters", false);

            connecting = () => UpdateStatus("подключение", true);
            login = () =>
            {
                UpdateStatus("подключён", true);
                if (profile.FullScreen && Visible) FullScreen();
            };
            disconnected = reason => UpdateStatus(reason <= 3 ? "отключён" : "отключён (код " + reason + ")", false);
            ComEventsHelper.Combine(client, EventInterface, 1, connecting);
            ComEventsHelper.Combine(client, EventInterface, 3, login);
            ComEventsHelper.Combine(client, EventInterface, 4, disconnected);
            Call(client, "Connect");
        }

        public void FullScreen()
        {
            if (client != null && IsOpen) Set(client, "FullScreen", true);
        }

        private void UpdateStatus(string status, bool open)
        {
            Status = status;
            IsOpen = open;
            Changed?.Invoke();
        }

        private static object Get(object target, string name) =>
            target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

        private static void Set(object target, string name, object value) =>
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });

        private static void Call(object target, string name) =>
            target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, null);

        protected override void Dispose(bool disposing)
        {
            if (disposing && client != null)
            {
                try
                {
                    if (connecting != null) ComEventsHelper.Remove(client, EventInterface, 1, connecting);
                    if (login != null) ComEventsHelper.Remove(client, EventInterface, 3, login);
                    if (disconnected != null) ComEventsHelper.Remove(client, EventInterface, 4, disconnected);
                    if (IsOpen) Call(client, "Disconnect");
                }
                catch (Exception error) when (error is COMException || error is TargetInvocationException || error is InvalidComObjectException)
                {
                    // AxHost can release its COM wrapper when destroying the parent window's handle.
                }
                client = null;
            }
            base.Dispose(disposing);
        }
    }
}
