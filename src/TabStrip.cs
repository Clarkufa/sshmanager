using System;
using System.Drawing;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class TabStrip : FlowLayoutPanel
    {
        private const string DragFormat = "SshManager.Tab";
        private readonly Timer scrollTimer;
        private Rectangle dragThreshold;
        private string draggedTab;
        private int? markerX;
        private int scrollDirection;

        public event Action<string, string, bool> ReorderRequested;

        public TabStrip()
        {
            AccessibleName = "Вкладки серверов";
            AccessibleRole = AccessibleRole.PageTabList;
            WrapContents = false;
            AutoScroll = true;
            BackColor = Theme.Canvas;
            DoubleBuffered = true;
            AttachDropTarget(this);
            scrollTimer = new Timer { Interval = 55 };
            scrollTimer.Tick += (sender, args) => ScrollTowardsPointer();
        }

        public void AddTab(SurfaceButton tab, string id)
        {
            Controls.Add(tab);
            AttachDropTarget(tab);
            if (id == null) return;

            tab.MouseDown += (sender, args) =>
            {
                var size = SystemInformation.DragSize;
                dragThreshold = args.Button == MouseButtons.Left
                    ? new Rectangle(args.X - size.Width / 2, args.Y - size.Height / 2, size.Width, size.Height)
                    : Rectangle.Empty;
            };
            tab.MouseMove += (sender, args) =>
            {
                if (args.Button != MouseButtons.Left || dragThreshold.IsEmpty || dragThreshold.Contains(args.Location)) return;
                dragThreshold = Rectangle.Empty;
                draggedTab = id;
                try { tab.DoDragDrop(new DataObject(DragFormat, id), DragDropEffects.Move); }
                finally
                {
                    draggedTab = null;
                    ClearDropPreview();
                }
            };
            tab.MouseUp += (sender, args) => dragThreshold = Rectangle.Empty;
        }

        private void AttachDropTarget(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += PreviewDrop;
            control.DragOver += PreviewDrop;
            control.DragLeave += (sender, args) => ClearDropPreview();
            control.DragDrop += DropTab;
        }

        private void PreviewDrop(object sender, DragEventArgs args)
        {
            args.Effect = DragDropEffects.None;
            if (draggedTab == null || !args.Data.GetDataPresent(DragFormat, false)) return;
            var point = PointToClient(new Point(args.X, args.Y));
            bool before;
            var anchor = FindAnchor(point.X, out before);
            if (anchor != null && (string)anchor.Tag != draggedTab)
            {
                args.Effect = DragDropEffects.Move;
                SetMarker(before ? anchor.Left - 3 : anchor.Right + 2);
            }
            else SetMarker(null);

            int edge = 24 * DeviceDpi / 96;
            scrollDirection = point.X < edge ? -1 : point.X > ClientSize.Width - edge ? 1 : 0;
            scrollTimer.Enabled = HorizontalScroll.Visible && scrollDirection != 0;
        }

        private void DropTab(object sender, DragEventArgs args)
        {
            if (draggedTab == null || !args.Data.GetDataPresent(DragFormat, false)) return;
            var source = draggedTab;
            bool before;
            var anchor = FindAnchor(PointToClient(new Point(args.X, args.Y)).X, out before);
            var anchorId = anchor == null ? null : (string)anchor.Tag;
            ClearDropPreview();
            if (anchorId == null || source == anchorId) return;
            // Let the native drag finish before rebuilding and disposing the tab controls.
            BeginInvoke(new Action(() => ReorderRequested?.Invoke(source, anchorId, before)));
        }

        private Control FindAnchor(int x, out bool before)
        {
            Control last = null;
            foreach (Control tab in Controls)
            {
                if (tab.Tag == null) continue;
                if (x < tab.Left + tab.Width / 2)
                {
                    before = true;
                    return tab;
                }
                last = tab;
            }
            before = false;
            return last;
        }

        private void ScrollTowardsPointer()
        {
            int position = -AutoScrollPosition.X;
            int limit = Math.Max(0, HorizontalScroll.Maximum - HorizontalScroll.LargeChange + 1);
            int next = Math.Max(0, Math.Min(limit, position + scrollDirection * 16 * DeviceDpi / 96));
            if (next == position) { scrollTimer.Stop(); return; }
            AutoScrollPosition = new Point(next, 0);
            bool before;
            var anchor = FindAnchor(PointToClient(Cursor.Position).X, out before);
            SetMarker(anchor == null || (string)anchor.Tag == draggedTab ? (int?)null : before ? anchor.Left - 3 : anchor.Right + 2);
        }

        private void ClearDropPreview()
        {
            scrollTimer.Stop();
            SetMarker(null);
        }

        private void SetMarker(int? position)
        {
            if (markerX == position) return;
            markerX = position;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            if (!markerX.HasValue) return;
            using (var pen = new Pen(Theme.Teal, 2 * DeviceDpi / 96f))
                args.Graphics.DrawLine(pen, markerX.Value, 4 * DeviceDpi / 96, markerX.Value, 30 * DeviceDpi / 96);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) scrollTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
