using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SshManager
{
    internal static class TrayApplication
    {
        public static void Run(string instanceName, Func<Form> createWindow)
        {
            using (var lifetime = new Mutex(false, @"Local\" + instanceName))
            {
                var startup = Stopwatch.StartNew();
                while (!TryAcquire(lifetime))
                {
                    if (ActivationWindow.Request(instanceName)) return;
                    if (startup.ElapsedMilliseconds >= 3000)
                    {
                        MessageBox.Show("SSH Manager уже запущен. Откройте его с помощью значка в трее.",
                            "SSH Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    Thread.Sleep(30);
                }

                try
                {
                    using (var window = createWindow())
                    using (var tray = new TrayWindow(window))
                    using (var activation = new ActivationWindow(instanceName, tray.RequestRestore))
                        Application.Run(window);
                }
                finally { lifetime.ReleaseMutex(); }
            }
        }

        private static bool TryAcquire(Mutex lifetime)
        {
            try { return lifetime.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }
    }

    internal sealed class TrayWindow : IDisposable
    {
        private readonly Form window;
        private readonly ContextMenuStrip menu;
        private readonly NotifyIcon notification;
        private FormWindowState restoreState;
        private bool restorePending;
        private bool restoring;

        public TrayWindow(Form window)
        {
            this.window = window;
            restoreState = FormWindowState.Normal;
            menu = Theme.Menu();
            menu.Items.Add(Theme.MenuItem("Открыть SSH Manager", Glyph.Terminal, (sender, args) => RequestRestore()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, (sender, args) => Application.Exit());
            notification = new NotifyIcon
            {
                Text = "SSH Manager", Icon = window.Icon ?? SystemIcons.Application, ContextMenuStrip = menu, Visible = true
            };
            notification.MouseClick += (sender, args) => { if (args.Button == MouseButtons.Left) RequestRestore(); };
            window.Resize += OnResize;
            window.FormClosing += OnClosing;
        }

        public void RequestRestore()
        {
            if (restorePending || !window.IsHandleCreated || window.IsDisposed || window.Disposing) return;
            restorePending = true;
            window.BeginInvoke((Action)Restore);
        }

        private void Restore()
        {
            restorePending = false;
            if (window.IsDisposed || window.Disposing) return;
            var state = restoreState;
            restoring = true;
            try
            {
                if (!window.Visible || window.WindowState == FormWindowState.Minimized || IsIconic(window.Handle))
                {
                    window.WindowState = state;
                    window.Show();
                    const int showMaximized = 3;
                    const int restore = 9;
                    ShowWindow(window.Handle, state == FormWindowState.Maximized ? showMaximized : restore);
                }
            }
            finally { restoring = false; }
            var modal = Application.OpenForms.Cast<Form>().LastOrDefault(form => form.Visible && form.Modal);
            var target = modal ?? window;
            target.Activate();
            SetForegroundWindow(target.Handle);
        }

        private void OnResize(object sender, EventArgs args)
        {
            if (restoring) return;
            if (window.WindowState == FormWindowState.Minimized) window.Hide();
            else restoreState = window.WindowState;
        }

        private void OnClosing(object sender, FormClosingEventArgs args)
        {
            if (args.CloseReason != CloseReason.UserClosing) return;
            args.Cancel = true;
            window.Hide();
        }

        public void Dispose()
        {
            window.Resize -= OnResize;
            window.FormClosing -= OnClosing;
            notification.Dispose();
            menu.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);
    }

    internal sealed class ActivationWindow : NativeWindow, IDisposable
    {
        private static readonly IntPtr MessageOnly = new IntPtr(-3); // HWND_MESSAGE
        private const int OpenMessage = 0x8001; // WM_APP + 1, used only by this window.
        private readonly Action open;

        public ActivationWindow(string name, Action open)
        {
            this.open = open;
            CreateHandle(new CreateParams { Parent = MessageOnly, Caption = name });
        }

        public static bool Request(string name)
        {
            var handle = FindWindowEx(MessageOnly, IntPtr.Zero, null, name);
            if (handle == IntPtr.Zero) return false;
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            if (processId != 0) AllowSetForegroundWindow(processId);
            return PostMessage(handle, OpenMessage, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == OpenMessage) open();
            else base.WndProc(ref message);
        }

        public void Dispose() { DestroyHandle(); }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    }
}
