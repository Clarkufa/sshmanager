using System;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using VtNetCore.VirtualTerminal;
using VtNetCore.VirtualTerminal.Model;
using VtNetCore.XTermParser;

namespace SshManager
{
    internal sealed class TerminalScreen
    {
        public readonly VirtualTerminalController Engine = new VirtualTerminalController { MaximumHistoryLines = 3000 };
        private readonly TerminalParser parser;
        public int HistoryOffset;

        public TerminalScreen(int columns, int rows, Action<byte[]> send)
        {
            Engine.SetUTF8();
            Engine.ResizeView(columns, rows);
            Engine.SendData += (sender, args) => send(args.Data);
            parser = new TerminalParser(Engine);
        }

        public void Feed(byte[] bytes)
        {
            int top = Engine.ViewPort.TopRow;
            parser.Feed(bytes);
            if (HistoryOffset > 0)
                HistoryOffset = Math.Max(0, Math.Min(Engine.ViewPort.TopRow, HistoryOffset + Engine.ViewPort.TopRow - top));
        }

        // Remote output can contain an unfinished OSC or DCS sequence. Keep its pending buffer bounded.
        private sealed class TerminalParser : DataConsumer
        {
            public TerminalParser(VirtualTerminalController terminal) : base(terminal) { }

            public void Feed(byte[] bytes)
            {
                if (InputBuffer.Remaining >= 65536) InputBuffer = new XTermInputBuffer();
                Push(bytes);
            }
        }
    }

    internal sealed class NativeTerminalView : Control
    {
        public static readonly Color Background = Color.FromArgb(17, 26, 39);
        private static readonly Color Foreground = Color.FromArgb(225, 232, 241);
        private static readonly Color SelectedBackground = Color.FromArgb(43, 79, 98);
        private static readonly Color CursorColor = Color.FromArgb(144, 225, 199);
        private const int Inset = 10;

        private readonly Font normalFont, boldFont;
        private readonly StringFormat textFormat;
        private readonly ContextMenuStrip clipboardMenu;
        private float characterWidth;
        private int lineHeight;
        private TerminalSession session;
        private TextRange selection;
        private char pendingSurrogate;
        private bool selecting;
        private int mouseButton = -1;

        public NativeTerminalView()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            Cursor = Cursors.IBeam;
            BackColor = Background;
            normalFont = new Font("Consolas", 11);
            boldFont = new Font(normalFont, FontStyle.Bold);
            textFormat = new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces
            };
            clipboardMenu = Theme.Menu();
            var copy = new ToolStripMenuItem("Копировать", null, (sender, args) => CopySelection()) { ShortcutKeyDisplayString = "Ctrl+Shift+C" };
            var paste = new ToolStripMenuItem("Вставить", null, (sender, args) => Paste()) { ShortcutKeyDisplayString = "Ctrl+Shift+V" };
            clipboardMenu.Items.AddRange(new ToolStripItem[] { copy, paste });
            clipboardMenu.Opening += (sender, args) =>
            {
                copy.Enabled = HasSelection;
                paste.Enabled = session != null && session.IsOpen;
                args.Cancel = session == null || IsRemoteMouse;
            };
            ContextMenuStrip = clipboardMenu;
        }

        private VirtualTerminalController Engine { get { return session.Screen.Engine; } }
        private bool HasSelection { get { return selection != null && !selection.Start.Equals(selection.End); } }
        private bool IsRemoteMouse
        {
            get { return session != null && session.IsOpen && session.Screen.HistoryOffset == 0 &&
                Engine.MouseTrackingEnabled && (ModifierKeys & Keys.Shift) == 0; }
        }

        public Size GridSize
        {
            get
            {
                if (!IsHandleCreated || characterWidth <= 0 || lineHeight <= 0 || ClientSize.Width < 40 || ClientSize.Height < 40) return new Size(80, 24);
                return new Size(Math.Max(2, (int)((ClientSize.Width - Inset * 2) / characterWidth)),
                    Math.Max(2, (ClientSize.Height - Inset * 2) / lineHeight));
            }
        }

        public void Attach(TerminalSession next)
        {
            if (session != null && Focused) Engine.FocusOut();
            session = next;
            selection = null;
            selecting = false;
            mouseButton = -1;
            pendingSurrogate = '\0';
            FitSession();
            if (session != null && Focused) Engine.FocusIn();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs args)
        {
            base.OnHandleCreated(args);
            using (var g = CreateGraphics())
            {
                characterWidth = g.MeasureString("MMMMMMMMMMMMMMMM", normalFont, PointF.Empty, textFormat).Width / 16f;
                lineHeight = (int)Math.Ceiling(normalFont.GetHeight(g)) + 1;
            }
            FitSession();
        }

        protected override void OnResize(EventArgs args) { base.OnResize(args); FitSession(); }
        protected override void OnVisibleChanged(EventArgs args) { base.OnVisibleChanged(args); FitSession(); }
        protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); if (session != null) Engine.FocusIn(); Invalidate(); }
        protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); if (session != null) Engine.FocusOut(); Invalidate(); }

        private void FitSession()
        {
            if (session == null || lineHeight == 0 || !Visible || ClientSize.Height < 40 || ClientSize.Width < 40) return;
            var size = GridSize;
            if (size.Width == Engine.VisibleColumns && size.Height == Engine.VisibleRows) return;
            Engine.ResizeView(size.Width, size.Height);
            if (session.IsOpen) session.Process.Resize(size.Width, size.Height);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            args.Graphics.Clear(Background);
            if (session == null || lineHeight == 0) return;
            var g = args.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var screen = session.Screen;
            var size = GridSize;
            int first = Math.Max(0, Engine.ViewPort.TopRow - screen.HistoryOffset);
            for (int row = 0; row < size.Height; row++)
                DrawLine(g, Engine.ViewPort.GetLine(first + row), first + row, row, size.Width);

            var cursor = Engine.ViewPort.CursorPosition;
            if (screen.HistoryOffset == 0 && session.IsOpen && Engine.CursorState.ShowCursor &&
                cursor.Row >= 0 && cursor.Row < size.Height && cursor.Column >= 0 && cursor.Column < size.Width)
            {
                float x = Inset + cursor.Column * characterWidth;
                int y = Inset + cursor.Row * lineHeight;
                using (var ink = new Pen(CursorColor, Focused ? 2 : 1))
                    g.DrawLine(ink, x, y + lineHeight - 2, x + characterWidth - 1, y + lineHeight - 2);
            }
            if (screen.HistoryOffset > 0 && Engine.ViewPort.TopRow > 0)
            {
                int track = Math.Max(16, Height - Inset * 2);
                float progress = 1 - screen.HistoryOffset / (float)Engine.ViewPort.TopRow;
                using (var thumb = new SolidBrush(Color.FromArgb(97, 116, 139)))
                    g.FillRectangle(thumb, Width - 5, Inset + progress * (track - 16), 3, 16);
            }
        }

        private void DrawLine(Graphics graphics, TerminalLine line, int logicalRow, int viewRow, int columns)
        {
            if (line != null && line.DoubleWidth) columns /= 2;
            float advance = line != null && line.DoubleWidth ? characterWidth * 2 : characterWidth;
            int y = Inset + viewRow * lineHeight;

            for (int start = 0; start < columns;)
            {
                var attributes = CellAttributes(line, start);
                bool selected = HasSelection && selection.Contains(start, logicalRow);
                int end = start + 1;
                while (end < columns && attributes.Equals(CellAttributes(line, end)) &&
                    selected == (HasSelection && selection.Contains(end, logicalRow))) end++;

                bool inverse = Engine.CursorState.ReverseVideoMode ^ attributes.Reverse;
                var fg = ColorFor(inverse ? attributes.BackgroundWebColor : attributes.WebColor, true);
                var bg = selected ? SelectedBackground : ColorFor(inverse ? attributes.WebColor : attributes.BackgroundWebColor, false);
                float x = Inset + start * advance;
                float width = (end - start) * advance;
                if (bg != Background)
                    using (var brush = new SolidBrush(bg)) graphics.FillRectangle(brush, x, y, width, lineHeight);

                if (!attributes.Hidden && line != null && start < line.Count)
                {
                    var run = new StringBuilder();
                    for (int i = start; i < end && i < line.Count; i++)
                    {
                        run.Append(line[i].Char == '\0' ? ' ' : line[i].Char);
                        run.Append(line[i].CombiningCharacters);
                    }
                    using (var brush = new SolidBrush(fg))
                    {
                        var font = attributes.Bright ? boldFont : normalFont;
                        if (line.DoubleWidth)
                        {
                            var transform = graphics.Save();
                            graphics.TranslateTransform(x, y);
                            graphics.ScaleTransform(2, 1);
                            graphics.DrawString(run.ToString(), font, brush, PointF.Empty, textFormat);
                            graphics.Restore(transform);
                        }
                        else graphics.DrawString(run.ToString(), font, brush, new PointF(x, y), textFormat);
                    }
                    if (attributes.Underscore)
                        using (var pen = new Pen(fg)) graphics.DrawLine(pen, x, y + lineHeight - 3, x + width, y + lineHeight - 3);
                }
                start = end;
            }
        }

        private TerminalAttribute CellAttributes(TerminalLine line, int column)
        {
            return line == null || column >= line.Count ? Engine.NullAttribute : line[column].Attributes;
        }

        private static Color ColorFor(string value, bool foreground)
        {
            int rgb;
            if (value == null || value.Length != 7 || !int.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                return foreground ? Foreground : Background;
            if (!foreground && rgb == 0) return Background;
            if (foreground && (rgb == 0xffffff || rgb == 0xc0c0c0)) return Foreground;
            return Color.FromArgb(unchecked((int)0xff000000) | rgb);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) != 0) return base.IsInputKey(keyData);
            return key == Keys.Tab || key == Keys.Enter || key == Keys.Escape || key == Keys.Up || key == Keys.Down ||
                key == Keys.Left || key == Keys.Right || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Home ||
                key == Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs args)
        {
            base.OnKeyDown(args);
            if (session == null || args.Handled) return;
            if (args.Control && args.Shift && args.KeyCode == Keys.C) CopySelection();
            else if (args.Control && args.Shift && args.KeyCode == Keys.V) Paste();
            else if (args.Shift && (args.KeyCode == Keys.PageUp || args.KeyCode == Keys.PageDown))
                ScrollHistory((args.KeyCode == Keys.PageUp ? 1 : -1) * Math.Max(1, GridSize.Height - 1));
            else
            {
                if (!session.IsOpen) return;
                byte[] sequence = null;
                if (args.Control && !args.Alt && args.KeyCode >= Keys.A && args.KeyCode <= Keys.Z)
                    sequence = new[] { (byte)(args.KeyCode - Keys.A + 1) };
                else
                {
                    var key = args.KeyCode;
                    switch (key)
                    {
                        case Keys.Enter: sequence = new byte[] { 13 }; break;
                        case Keys.Back: sequence = new byte[] { 127 }; break;
                        case Keys.Escape: sequence = new byte[] { 27 }; break;
                        case Keys.Tab: sequence = args.Shift ? Encoding.ASCII.GetBytes("\u001b[Z") : new byte[] { 9 }; break;
                        default:
                            string name = key == Keys.PageUp ? "PageUp" : key == Keys.PageDown ? "PageDown" : key.ToString();
                            sequence = Engine.GetKeySequence(name, args.Control, args.Shift);
                            break;
                    }
                }
                // Printable keys, including Cyrillic and composed characters, are handled by KeyPress.
                if (sequence == null || (!args.Control && args.KeyCode >= Keys.A && args.KeyCode <= Keys.Z)) return;
                Send(sequence);
            }
            args.Handled = args.SuppressKeyPress = true;
        }

        protected override void OnKeyPress(KeyPressEventArgs args)
        {
            base.OnKeyPress(args);
            if (session == null || !session.IsOpen || args.Handled) return;
            char ch = args.KeyChar;
            if (char.IsHighSurrogate(ch)) { pendingSurrogate = ch; args.Handled = true; return; }
            string text = pendingSurrogate != '\0' && char.IsLowSurrogate(ch) ? new string(new[] { pendingSurrogate, ch }) : ch.ToString();
            pendingSurrogate = '\0';
            Send(Encoding.UTF8.GetBytes(text));
            args.Handled = true;
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (session == null) return;
            Focus();
            if (IsRemoteMouse)
            {
                mouseButton = args.Button == MouseButtons.Left ? 0 : args.Button == MouseButtons.Middle ? 1 : args.Button == MouseButtons.Right ? 2 : -1;
                if (mouseButton >= 0) ReportMouse(args.Location, mouseButton, false);
                return;
            }
            if (args.Button != MouseButtons.Left) return;
            var cell = CellAt(args.Location);
            selection = new TextRange { Start = cell, End = cell };
            selecting = true;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            if (session == null) return;
            if (selecting)
            {
                selection.End = CellAt(args.Location);
                Invalidate();
            }
            else if (IsRemoteMouse && (Engine.UseAllMouseTracking || (Engine.CellMotionMouseTracking && mouseButton >= 0)))
                ReportMouse(args.Location, (mouseButton < 0 ? 3 : mouseButton) | 32, false);
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (selecting && args.Button == MouseButtons.Left) { selecting = false; Capture = false; }
            if (IsRemoteMouse && mouseButton >= 0) ReportMouse(args.Location, mouseButton, true);
            mouseButton = -1;
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            base.OnMouseWheel(args);
            if (session == null) return;
            if (IsRemoteMouse) ReportMouse(args.Location, args.Delta > 0 ? 64 : 65, false);
            else ScrollHistory(args.Delta == 0 ? 0 : Math.Sign(args.Delta) * Math.Max(3, Math.Abs(args.Delta) / 40));
        }

        private void ReportMouse(Point point, int button, bool release)
        {
            var cell = CellAt(point);
            int x = cell.Column + 1, y = cell.Row - Engine.ViewPort.TopRow + 1;
            int modifier = (ModifierKeys & Keys.Control) != 0 ? 16 : 0;
            if (Engine.SgrMouseMode)
                session.Process.Send("\u001b[<" + (button | modifier) + ";" + x + ";" + y + (release ? "m" : "M"));
            else if ((!release || !Engine.X10SendMouseXYOnButton) && x <= 223 && y <= 223)
                session.Process.Send(new byte[] { 27, 91, 77, (byte)((release ? 3 : button | modifier) + 32), (byte)(x + 32), (byte)(y + 32) });
        }

        private TextPosition CellAt(Point point)
        {
            var size = GridSize;
            int top = Math.Max(0, Engine.ViewPort.TopRow - session.Screen.HistoryOffset);
            return new TextPosition(Math.Max(0, Math.Min(size.Width - 1, (int)((point.X - Inset) / characterWidth))),
                top + Math.Max(0, Math.Min(size.Height - 1, (point.Y - Inset) / lineHeight)));
        }

        private void ScrollHistory(int lines)
        {
            session.Screen.HistoryOffset = Math.Max(0, Math.Min(Engine.ViewPort.TopRow, session.Screen.HistoryOffset + lines));
            selection = null;
            Invalidate();
        }

        private void Send(byte[] bytes)
        {
            if (session.Screen.HistoryOffset != 0) ScrollHistory(-session.Screen.HistoryOffset);
            session.Process.Send(bytes);
        }

        private void CopySelection()
        {
            if (!HasSelection) return;
            var text = Engine.GetText(selection);
            if (String.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (ExternalException) { Theme.ShowError(this, new InvalidOperationException("Буфер обмена занят. Повторите попытку.")); }
        }

        private void Paste()
        {
            if (session == null || !session.IsOpen) return;
            try
            {
                var text = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
                if (text.Length == 0) return;
                if (Engine.BracketedPasteMode) text = "\u001b[200~" + text + "\u001b[201~";
                Send(Encoding.UTF8.GetBytes(text));
            }
            catch (ExternalException) { Theme.ShowError(this, new InvalidOperationException("Буфер обмена занят. Повторите попытку.")); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                clipboardMenu.Dispose();
                normalFont.Dispose();
                boldFont.Dispose();
                textFormat.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
