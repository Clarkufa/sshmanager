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
        /// <summary>«Сворачивать в трей при закрытии окна» from the settings.</summary>
        public static bool CloseToTray = true;

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
        private readonly TrayIcon notification;
        private FormWindowState restoreState;
        private bool restoring;

        public TrayWindow(Form window)
        {
            this.window = window;
            restoreState = FormWindowState.Normal;
            menu = Theme.Menu();
            menu.Items.Add(Theme.MenuItem("Открыть SSH Manager", Glyph.Terminal, (sender, args) => RequestRestore()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, (sender, args) => Application.Exit());
            notification = new TrayIcon(window.Icon ?? SystemIcons.Application, menu, Restore);
            window.Resize += OnResize;
            window.FormClosing += OnClosing;
        }

        public void RequestRestore() => notification.RequestRestore();

        private void Restore()
        {
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
                BringOntoScreen(window);
            }
            finally { restoring = false; }
            var modal = Application.OpenForms.Cast<Form>().LastOrDefault(form => form.Visible && form.Modal);
            var target = modal ?? window;
            if (modal != null) BringOntoScreen(modal);
            target.Activate();
            if (!SetForegroundWindow(target.Handle))
            {
                // Restore the Z order without making the window permanently topmost.
                const uint noSize = 0x0001, noMove = 0x0002, noActivate = 0x0010;
                SetWindowPos(target.Handle, IntPtr.Zero, 0, 0, 0, 0, noSize | noMove | noActivate);
                var attention = new FlashInfo
                {
                    Size = (uint)Marshal.SizeOf(typeof(FlashInfo)), Window = window.Handle, Flags = 2, Count = 3
                };
                FlashWindowEx(ref attention);
            }
        }

        private static void BringOntoScreen(Form form)
        {
            Rectangle bounds = form.Bounds;
            var caption = new Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Min(bounds.Height, SystemInformation.CaptionHeight + 12));
            foreach (var screen in Screen.AllScreens)
            {
                var visible = Rectangle.Intersect(screen.WorkingArea, caption);
                if (visible.Width >= Math.Min(64, caption.Width) && visible.Height >= Math.Min(12, caption.Height))
                    return;
            }

            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            var state = form.WindowState;
            if (state == FormWindowState.Maximized)
            {
                form.WindowState = FormWindowState.Normal;
                bounds = form.Bounds;
            }
            form.Location = new Point(work.X + Math.Max(0, (work.Width - bounds.Width) / 2),
                work.Y + Math.Max(0, (work.Height - bounds.Height) / 2));
            if (state == FormWindowState.Maximized) form.WindowState = state;
        }

        private void OnResize(object sender, EventArgs args)
        {
            if (restoring) return;
            if (window.WindowState == FormWindowState.Minimized)
            {
                if (notification.EnsureRegistered()) window.Hide();
            }
            else restoreState = window.WindowState;
        }

        private void OnClosing(object sender, FormClosingEventArgs args)
        {
            if (args.CloseReason != CloseReason.UserClosing) return;
            args.Cancel = true;
            if (!TrayApplication.CloseToTray)
            {
                window.BeginInvoke(new Action(Application.Exit));
                return;
            }
            if (notification.EnsureRegistered()) window.Hide();
            else window.WindowState = FormWindowState.Minimized;
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

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags, Count, Timeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashInfo info);
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
