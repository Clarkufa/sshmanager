using System;
using System.Drawing;
using System.Windows.Forms;

namespace SshManager
{
    /// <summary>
    /// What RDP and VNC sessions share: the status, the «Сеанс завершён» / «Подключение прервано» overlay with
    /// «Переподключить» and «Закрыть сеанс», and the commands the sessions page and the session windows call.
    /// </summary>
    internal abstract class RemoteDesktopPanel : Panel
    {
        private readonly Panel overlay, overlayReason;
        private readonly Label overlayTitle, overlayDetail;
        private readonly FlowLayoutPanel overlayActions;

        protected RemoteDesktopPanel()
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
        }

        public event Action Changed;
        public event Action CloseRequested;
        public string Status { get; private set; } = "подключение";
        public bool IsOpen { get; private set; } = true;
        public abstract bool IsConnected { get; }
        /// <summary>«Имя · адрес» above the reason in the overlay.</summary>
        protected abstract string TargetLine { get; }

        public abstract void Reconnect();
        public abstract void Disconnect();
        public abstract void FullScreen();
        /// <summary>«Открыть в отдельном окне»: the same connection in a window of its own.</summary>
        public abstract void OpenInWindow();

        protected void ShowEnded(string title, string detail)
        {
            if (IsDisposed) return;
            overlayTitle.Text = title;
            overlayDetail.Text = (TargetLine.Length == 0 ? "" : TargetLine + "\n") + detail;
            overlay.Visible = true;
            overlay.BringToFront();
            FitOverlay();
        }

        protected void HideEnded() { overlay.Visible = false; }

        protected void UpdateStatus(string status, bool open)
        {
            Status = status;
            IsOpen = open;
            Changed?.Invoke();
        }

        protected void RequestClose() { CloseRequested?.Invoke(); }

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
    }
}
