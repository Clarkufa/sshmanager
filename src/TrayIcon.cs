using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class TrayIcon : NativeWindow, IDisposable
    {
        private const uint Add = 0, Modify = 1, Delete = 2, SetFocus = 3, SetVersion = 4;
        private const uint IconId = 1;
        private const int ShellCallback = 0x8002, RestoreMessage = 0x8003;
        private const int Select = 0x0400, KeySelect = 0x0401, ContextMenu = 0x007B;
        private const int LeftUp = 0x0202, RightUp = 0x0205;
        private readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        private readonly Icon icon;
        private readonly ContextMenuStrip menu;
        private readonly Action restore;
        private bool registered;
        private bool version4;
        private bool restorePending;

        public TrayIcon(Icon icon, ContextMenuStrip menu, Action restore)
        {
            this.icon = icon;
            this.menu = menu;
            this.restore = restore;

            // TaskbarCreated is broadcast to top-level windows, not message-only windows.
            CreateHandle(new CreateParams());
            ChangeWindowMessageFilterEx(Handle, ShellCallback, 1, IntPtr.Zero);
            if (taskbarCreated != 0) ChangeWindowMessageFilterEx(Handle, taskbarCreated, 1, IntPtr.Zero);
            menu.Closed += OnMenuClosed;
            EnsureRegistered();
        }

        public bool EnsureRegistered()
        {
            if (registered) return true;
            const uint message = 0x0001, showIcon = 0x0002, tip = 0x0004, showTip = 0x0080;
            var data = NewData();
            data.Flags = message | showIcon | tip | showTip;
            data.CallbackMessage = ShellCallback;
            data.Icon = icon.Handle;
            data.Tip = "SSH Manager";
            registered = ShellNotifyIcon(Add, ref data) || ShellNotifyIcon(Modify, ref data);
            data.Version = 4;
            version4 = registered && ShellNotifyIcon(SetVersion, ref data);
            return registered;
        }

        public void RequestRestore()
        {
            if (!restorePending && Handle != IntPtr.Zero)
                restorePending = PostMessage(Handle, RestoreMessage, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message message)
        {
            if (taskbarCreated != 0 && message.Msg == taskbarCreated)
            {
                registered = false;
                EnsureRegistered();
            }
            else if (message.Msg == RestoreMessage)
            {
                restorePending = false;
                restore();
            }
            else if (message.Msg == ShellCallback)
            {
                long details = message.LParam.ToInt64();
                long id = version4 ? (details >> 16) & 0xffff : message.WParam.ToInt64();
                if (id != IconId) return;
                long action = version4 ? details & 0xffff : details;
                if (action == LeftUp || action == Select || action == KeySelect)
                    RequestRestore();
                else if (version4 ? action == ContextMenu : action == RightUp)
                {
                    var position = GetMenuLocation();
                    SetForegroundWindow(Handle);
                    menu.Show(position);
                }
            }
            else base.WndProc(ref message);
        }

        private Point GetMenuLocation()
        {
            // The icon rectangle also supplies an anchor for keyboard-initiated menus.
            var id = new IconIdentifier
            {
                Size = (uint)Marshal.SizeOf(typeof(IconIdentifier)), Window = Handle, Id = IconId
            };
            if (ShellNotifyIconGetRect(ref id, out var rectangle) == 0)
                return new Point(rectangle.Left + (rectangle.Right - rectangle.Left) / 2,
                    rectangle.Top + (rectangle.Bottom - rectangle.Top) / 2);
            return Cursor.Position;
        }

        private void OnMenuClosed(object sender, ToolStripDropDownClosedEventArgs args)
        {
            if (args.CloseReason != ToolStripDropDownCloseReason.ItemClicked)
            {
                var data = NewData();
                ShellNotifyIcon(SetFocus, ref data);
            }
            PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            menu.Closed -= OnMenuClosed;
            if (registered)
            {
                var data = NewData();
                ShellNotifyIcon(Delete, ref data);
            }
            DestroyHandle();
        }

        private NotifyIconData NewData() => new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf(typeof(NotifyIconData)), Window = Handle, Id = IconId
        };

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint Size;
            public IntPtr Window;
            public uint Id, Flags, CallbackMessage;
            public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
            public uint InfoFlags;
            public Guid Guid;
            public IntPtr BalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconIdentifier
        {
            public uint Size;
            public IntPtr Window;
            public uint Id;
            public Guid Guid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        private static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData data);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
        private static extern int ShellNotifyIconGetRect(ref IconIdentifier id, out Rect rectangle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr window, int message, uint action, IntPtr changeInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);
    }
}
