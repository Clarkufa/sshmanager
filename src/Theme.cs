using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SshManager
{
    internal enum Glyph { None, Plus, Refresh, Edit, Delete, Terminal, Server, Folder, Move, MoveLeft, Copy, Key, Browse, Dot, Moon, Sun }
    internal enum ButtonStyle { Outline, Primary, Quiet, Tab, SelectedTab }

    internal static class Theme
    {
        private enum Tone { Canvas, Surface, Ink, Muted, Line, Selection, Mint, Teal, Danger, Primary, PrimaryHover, Disabled }
        private static readonly Color[] Light = Palette(0xf6f8fb, 0xffffff, 0x102235, 0x485c6d, 0xdfe6ee, 0xeaf2fa,
            0xe7f6f2, 0x00645c, 0xa42435, 0x00645c, 0x00786b, 0x7e8f9e);
        private static readonly Color[] Dark = Palette(0x171f2b, 0x202b3a, 0xe9eff8, 0xaabacf, 0x3b4c60, 0x2a4359,
            0x21413e, 0x85e1cc, 0xffa3ac, 0x146859, 0x197565, 0x899bb0);
        public static bool IsDark { get; set; }
        public static Color Canvas => Get(Tone.Canvas);
        public static Color Surface => Get(Tone.Surface);
        public static Color Ink => Get(Tone.Ink);
        public static Color Muted => Get(Tone.Muted);
        public static Color Line => Get(Tone.Line);
        public static Color Selection => Get(Tone.Selection);
        public static Color Mint => Get(Tone.Mint);
        public static Color Teal => Get(Tone.Teal);
        public static Color Danger => Get(Tone.Danger);
        public static Color Primary => Get(Tone.Primary);
        public static Color PrimaryHover => Get(Tone.PrimaryHover);
        public static Color Disabled => Get(Tone.Disabled);
        public static readonly Font Body = new Font("Segoe UI", 10);
        public static readonly Font Title = new Font("Segoe UI Semibold", 21);
        public static readonly Font Emphasis = new Font("Segoe UI Semibold", 10);
        private static readonly Dictionary<Tuple<Glyph, Color>, Bitmap> Icons = new Dictionary<Tuple<Glyph, Color>, Bitmap>();

        private static Color[] Palette(params int[] rgb)
        {
            return Array.ConvertAll(rgb, value => Color.FromArgb(unchecked((int)0xff000000) | value));
        }

        private static Color Get(Tone tone) => (IsDark ? Dark : Light)[(int)tone];

        public static void RefreshSwitcher(SurfaceButton button)
        {
            string label = IsDark ? "Светлая тема" : "Тёмная тема";
            button.Symbol = IsDark ? Glyph.Sun : Glyph.Moon;
            button.Text = button.AccessibleName = label;
            button.Invalidate();
        }

        public static void Apply(Form window)
        {
            Recolor(window);
            if (!window.IsHandleCreated) return;
            int dark = IsDark ? 1 : 0;
            var handle = window.Handle;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) < 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            int caption = IsDark ? ColorTranslator.ToWin32(Canvas) : -1;
            int text = IsDark ? ColorTranslator.ToWin32(Ink) : -1;
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, 0x0401); // Invalidate client area and frame.
        }

        private static void Recolor(Control control)
        {
            if (control is TextBoxBase || control is ListBox || control is ComboBox)
            {
                control.BackColor = Surface;
                control.ForeColor = Ink;
            }
            else
            {
                control.BackColor = CurrentColor(control.BackColor);
                control.ForeColor = CurrentColor(control.ForeColor);
            }
            if (control is DataGridView grid)
            {
                grid.BackgroundColor = Surface;
                grid.GridColor = Line;
                grid.RowsDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Surface, ForeColor = Ink, SelectionBackColor = Selection, SelectionForeColor = Ink
                };
                grid.ColumnHeadersDefaultCellStyle.BackColor = Canvas;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Canvas;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
            }
            if (control is ScrollBar || control is ComboBox)
            {
                control.HandleCreated -= ApplyNativeControlTheme;
                control.HandleCreated += ApplyNativeControlTheme;
                if (control.IsHandleCreated) ApplyNativeControlTheme(control, EventArgs.Empty);
            }
            foreach (Control child in control.Controls) Recolor(child);
            control.Invalidate();
        }

        private static void ApplyNativeControlTheme(object sender, EventArgs args)
        {
            string theme = null;
            if (IsDark) theme = sender is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer";
            SetWindowTheme(((Control)sender).Handle, theme, null);
        }

        private static Color CurrentColor(Color color)
        {
            var previous = IsDark ? Light : Dark;
            var current = IsDark ? Dark : Light;
            for (int index = 0; index < previous.Length; index++)
                if (color.ToArgb() == previous[index].ToArgb()) return current[index];
            return color;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string subAppName, string subIdList);

        public static Image AppMark()
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SshManager.AppMark.png"))
            using (var image = Image.FromStream(resource)) return new Bitmap(image);
        }

        public static SurfaceButton Button(string text, int width, bool primary = false, Glyph glyph = Glyph.None, bool danger = false, bool quiet = false)
        {
            return new SurfaceButton
            {
                Text = text, Width = width, Height = 34, Margin = new Padding(6, 1, 0, 0), Font = Emphasis,
                Style = primary ? ButtonStyle.Primary : quiet ? ButtonStyle.Quiet : ButtonStyle.Outline,
                Symbol = glyph, ForeColor = danger ? Danger : Ink, AccessibleName = text
            };
        }

        /// <summary>
        /// Scales a control built in 96-DPI units to the current DPI. Windows already scales fonts, but WinForms
        /// auto-scaling runs when AutoScaleDimensions is assigned, before the children exist, and never for
        /// controls created later, so every form and lazily created page calls this once after it is built.
        /// </summary>
        public static void ScaleToDpi(Control control)
        {
            float factor = control.DeviceDpi / 96f;
            if (factor == 1f) return;
            control.Scale(new SizeF(factor, factor));
            if (control is Form form && form.TopLevel)
            {
                var area = Screen.FromPoint(Cursor.Position).WorkingArea.Size;
                form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
                form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
            }
        }

        public static int Scaled(Control control, int logical) => (logical * control.DeviceDpi + 48) / 96;

        /// <summary>Draws a control's own scroll bars dark whatever the app theme, for areas that are always dark.</summary>
        public static void DarkScrollBars(Control control)
        {
            void Apply(object sender, EventArgs args) => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            control.HandleCreated += Apply;
            if (control.IsHandleCreated) Apply(control, EventArgs.Empty);
        }

        /// <summary>Shows a grey hint inside an empty text box.</summary>
        public static void SetCue(TextBox box, string cue)
        {
            void Apply(object sender, EventArgs args) => SendMessage(box.Handle, 0x1501, new IntPtr(1), cue); // EM_SETCUEBANNER
            box.HandleCreated += Apply;
            if (box.IsHandleCreated) Apply(box, EventArgs.Empty);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        public static Panel Frame(Control editor, Padding margin)
        {
            var field = new Panel { Dock = DockStyle.Fill, BackColor = Line, Margin = margin, Padding = new Padding(1) };
            if (editor is TextBox text)
            {
                text.BorderStyle = BorderStyle.None;
                var inset = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(7, 4, 7, 0) };
                inset.Controls.Add(text);
                inset.Click += (sender, args) => text.Focus();
                text.GotFocus += (sender, args) => field.BackColor = Teal;
                text.LostFocus += (sender, args) => field.BackColor = Line;
                field.Controls.Add(inset);
            }
            else
            {
                if (editor is ListBox list) list.BorderStyle = BorderStyle.None;
                field.Controls.Add(editor);
            }
            editor.Dock = DockStyle.Fill;
            return field;
        }

        public static void DrawChoice(object sender, DrawItemEventArgs args)
        {
            var combo = (ComboBox)sender;
            bool selected = (args.State & DrawItemState.Selected) != 0 &&
                (args.State & DrawItemState.ComboBoxEdit) == 0;
            using (var fill = new SolidBrush(selected ? Selection : Surface))
                args.Graphics.FillRectangle(fill, args.Bounds);
            if (args.Index >= 0)
                TextRenderer.DrawText(args.Graphics, combo.GetItemText(combo.Items[args.Index]), combo.Font,
                    Rectangle.Inflate(args.Bounds, -4, 0), Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (selected) args.DrawFocusRectangle();
        }

        public static ToolStripMenuItem MenuItem(string text, Glyph glyph, EventHandler action = null)
        {
            return new IconMenuItem(text, glyph, action);
        }

        public static ContextMenuStrip Menu()
        {
            var menu = new ContextMenuStrip { Font = Body, Renderer = new PaletteMenuRenderer() };
            menu.Opening += (sender, args) => RecolorMenu(menu);
            return menu;
        }

        private static void RecolorMenu(ToolStrip menu)
        {
            menu.BackColor = Surface;
            menu.ForeColor = Ink;
            foreach (ToolStripItem item in menu.Items)
            {
                item.ForeColor = Ink;
                if (item is IconMenuItem iconItem) iconItem.Recolor();
                if (item is ToolStripMenuItem parent && parent.HasDropDownItems)
                {
                    parent.DropDown.Renderer = menu.Renderer;
                    RecolorMenu(parent.DropDown);
                }
            }
        }

        private sealed class IconMenuItem : ToolStripMenuItem
        {
            private readonly Glyph glyph;

            public IconMenuItem(string text, Glyph glyph, EventHandler action) : base(text, null, action)
            {
                this.glyph = glyph;
                Recolor();
            }

            public void Recolor()
            {
                ForeColor = glyph == Glyph.Delete ? Danger : Ink;
                Image = Theme.Icon(glyph, glyph == Glyph.Delete ? Danger : Teal);
            }
        }

        private sealed class PaletteMenuRenderer : ToolStripProfessionalRenderer
        {
            public PaletteMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs args)
            {
                if (!args.Item.Enabled) args.TextColor = Disabled;
                base.OnRenderItemText(args);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs args)
            {
                args.ArrowColor = args.Item.Enabled ? Ink : Disabled;
                base.OnRenderArrow(args);
            }
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Surface;
            public override Color ImageMarginGradientBegin => Surface;
            public override Color ImageMarginGradientMiddle => Surface;
            public override Color ImageMarginGradientEnd => Surface;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Line;
            public override Color MenuItemSelected => Selection;
            public override Color MenuItemSelectedGradientBegin => Selection;
            public override Color MenuItemSelectedGradientEnd => Selection;
            public override Color MenuItemPressedGradientBegin => Selection;
            public override Color MenuItemPressedGradientMiddle => Selection;
            public override Color MenuItemPressedGradientEnd => Selection;
            public override Color CheckBackground => Mint;
            public override Color CheckSelectedBackground => Mint;
            public override Color CheckPressedBackground => Mint;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Surface;
        }

        public static Bitmap Icon(Glyph glyph, Color color)
        {
            var key = Tuple.Create(glyph, color);
            Bitmap icon;
            if (Icons.TryGetValue(key, out icon)) return icon;
            icon = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(icon))
            using (var p = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                switch (glyph)
                {
                    case Glyph.Plus: g.DrawLine(p, 8, 3, 8, 13); g.DrawLine(p, 3, 8, 13, 8); break;
                    case Glyph.Refresh: g.DrawArc(p, 2, 2, 12, 12, 25, 310); Trace(g, p, 14, 2, 14, 6, 10, 6); break;
                    case Glyph.Edit: Trace(g, p, 3, 10, 11, 2, 14, 5, 6, 13, 2, 14, 3, 10); g.DrawLine(p, 9, 4, 12, 7); break;
                    case Glyph.Delete: Trace(g, p, 4, 6, 5, 14, 11, 14, 12, 6); g.DrawLine(p, 2, 4, 14, 4); Trace(g, p, 6, 4, 6, 2, 10, 2, 10, 4); break;
                    case Glyph.Terminal: Trace(g, p, 3, 4, 7, 8, 3, 12); g.DrawLine(p, 9, 12, 14, 12); break;
                    case Glyph.Server: g.DrawRectangle(p, 2, 2, 12, 5); g.DrawRectangle(p, 2, 9, 12, 5); g.DrawLine(p, 10, 4.5f, 11, 4.5f); g.DrawLine(p, 10, 11.5f, 11, 11.5f); break;
                    case Glyph.Folder: Trace(g, p, 2, 13, 2, 3, 6, 3, 8, 5, 14, 5, 14, 13, 2, 13); break;
                    case Glyph.Move: g.DrawLine(p, 2, 8, 14, 8); Trace(g, p, 10, 4, 14, 8, 10, 12); break;
                    case Glyph.MoveLeft: g.DrawLine(p, 2, 8, 14, 8); Trace(g, p, 6, 4, 2, 8, 6, 12); break;
                    case Glyph.Copy: g.DrawRectangle(p, 5, 5, 9, 9); Trace(g, p, 2, 11, 2, 2, 11, 2); break;
                    case Glyph.Key: g.DrawEllipse(p, 1.5f, 1.5f, 6, 6); g.DrawLine(p, 6.5f, 6.5f, 14, 14); g.DrawLine(p, 11, 11, 13, 9); g.DrawLine(p, 9, 9, 10.5f, 7.5f); break;
                    case Glyph.Browse: g.DrawEllipse(p, 2, 2, 8, 8); g.DrawLine(p, 9, 9, 14, 14); break;
                    case Glyph.Moon:
                        using (var moon = new GraphicsPath())
                        {
                            moon.AddBezier(8, 2, 1, 2, 0, 13, 8, 14);
                            moon.AddBezier(8, 14, 11, 14, 13, 12, 14, 9);
                            moon.AddBezier(14, 9, 9, 12, 5, 7, 8, 2);
                            moon.CloseFigure();
                            g.DrawPath(p, moon);
                        }
                        break;
                    case Glyph.Sun:
                        g.DrawEllipse(p, 5, 5, 6, 6);
                        g.DrawLine(p, 8, 1, 8, 3); g.DrawLine(p, 8, 13, 8, 15);
                        g.DrawLine(p, 1, 8, 3, 8); g.DrawLine(p, 13, 8, 15, 8);
                        g.DrawLine(p, 3, 3, 4, 4); g.DrawLine(p, 12, 12, 13, 13);
                        g.DrawLine(p, 12, 4, 13, 3); g.DrawLine(p, 3, 13, 4, 12);
                        break;
                    case Glyph.Dot:
                        using (var fill = new SolidBrush(color)) g.FillEllipse(fill, 2, 2, 12, 12);
                        break;
                }
            }
            Icons.Add(key, icon);
            return icon;
        }

        public static GraphicsPath Round(Rectangle box, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
            path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
            path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Rectangle OutlineBounds(Rectangle bounds, int dpi)
        {
            // Leave room for both halves of the antialiased stroke.
            int inset = Math.Max(1, (dpi + 95) / 96);
            return new Rectangle(bounds.X + inset, bounds.Y + inset,
                bounds.Width - inset * 2 - 1, bounds.Height - inset * 2 - 1);
        }

        public static bool IsFileError(Exception exception)
        {
            return exception is IOException || exception is UnauthorizedAccessException || exception is System.Security.SecurityException;
        }

        public static void ShowError(IWin32Window owner, Exception exception)
        {
            MessageBox.Show(owner, exception.Message, "SSH Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void Trace(Graphics graphics, Pen pen, params int[] points)
        {
            var vertices = new Point[points.Length / 2];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new Point(points[i * 2], points[i * 2 + 1]);
            graphics.DrawLines(pen, vertices);
        }
    }

    internal class ThemedForm : Form
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Apply(this);
        }
    }

    internal sealed class SurfaceButton : Button
    {
        private ButtonStyle style;
        private bool hover;
        public Glyph Symbol { get; set; }
        public Color? Marker { get; set; }
        public ButtonStyle Style
        {
            get { return style; }
            set { style = value; Invalidate(); }
        }

        public SurfaceButton()
        {
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
            BackColor = Theme.Canvas;
            SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 10 || Height < 10) return;
            var graphics = e.Graphics;
            graphics.Clear(BackColor);
            var area = Theme.OutlineBounds(ClientRectangle, DeviceDpi);
            if (area.Width < 2 || area.Height < 2) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool primary = style == ButtonStyle.Primary;
            bool selected = style == ButtonStyle.SelectedTab;
            bool outline = style == ButtonStyle.Outline;
            var textColor = primary ? Color.White : selected ? Theme.Teal : style == ButtonStyle.Tab ? Theme.Muted : ForeColor;
            if (!Enabled) textColor = Theme.Disabled;
            var fill = Color.Transparent;
            if (primary) fill = !Enabled ? Theme.Line : hover ? Theme.PrimaryHover : Theme.Primary;
            else if (selected) fill = Theme.Mint;
            else if (hover && Enabled) fill = Theme.Selection;
            else if (outline) fill = Theme.Surface;
            using (var path = Theme.Round(area, Math.Min(7 * DeviceDpi / 96, Math.Min(area.Width, area.Height) / 2)))
            using (var brush = new SolidBrush(fill))
            using (var border = new Pen(Focused && ShowFocusCues ? Theme.Teal : Theme.Line, DeviceDpi / 96f))
            {
                graphics.FillPath(brush, path);
                if (outline || (Focused && ShowFocusCues)) graphics.DrawPath(border, path);
            }

            int icon = (Marker.HasValue ? 11 : Symbol == Glyph.None ? 0 : 16) * DeviceDpi / 96;
            int gap = icon == 0 || Text.Length == 0 ? 0 : 6 * DeviceDpi / 96;
            using (var format = new StringFormat(StringFormat.GenericTypographic)
            {
                LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            using (var ink = new SolidBrush(textColor))
            {
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                float textWidth = Text.Length == 0 ? 0 : Math.Min(Width - icon - gap - 8, graphics.MeasureString(Text, Font, Width, format).Width + 1);
                float start = (Width - textWidth - icon - gap) / 2;
                if (icon > 0)
                {
                    var iconBounds = new Rectangle((int)start, (Height - icon) / 2, icon, icon);
                    graphics.DrawImage(Theme.Icon(Marker.HasValue ? Glyph.Dot : Symbol, Marker ?? textColor), iconBounds);
                }
                var text = new RectangleF(start + icon + gap, 0, textWidth, Height);
                graphics.DrawString(Text, Font, ink, text, format);
            }
        }
    }

    /// <summary>A one-line label that shortens a file path in the middle instead of wrapping it.</summary>
    internal sealed class PathLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var fill = new SolidBrush(BackColor)) e.Graphics.FillRectangle(fill, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.SingleLine |
                TextFormatFlags.PathEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class ServerSearch : Panel
    {
        public readonly TextBox Input;

        public ServerSearch(string placeholder = "Найти сервер", string accessibleName = "Поиск подключений")
        {
            BackColor = Theme.Surface;
            Size = new Size(265, 34);
            Margin = new Padding(0, 1, 0, 0);
            Padding = new Padding(34, 8, 11, 6);
            Input = new TextBox { AccessibleName = accessibleName, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Ink };
            var inputArea = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var hint = new Label { Text = placeholder, Dock = DockStyle.Fill, ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true, UseMnemonic = false };
            inputArea.Controls.Add(Input);
            inputArea.Controls.Add(hint);
            hint.BringToFront();
            void RefreshHint()
            {
                hint.Visible = !Input.Focused && Input.TextLength == 0;
                Invalidate();
            }
            Input.TextChanged += (sender, args) => RefreshHint();
            Input.GotFocus += (sender, args) => RefreshHint();
            Input.LostFocus += (sender, args) => RefreshHint();
            hint.Click += (sender, args) => Input.Focus();
            Click += (sender, args) => Input.Focus();
            Controls.Add(inputArea);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 10 || Height < 10) return;
            var box = Theme.OutlineBounds(ClientRectangle, DeviceDpi);
            if (box.Width < 2 || box.Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outside = new SolidBrush(Theme.Canvas)) e.Graphics.FillRectangle(outside, ClientRectangle);
            using (var fill = new SolidBrush(Theme.Surface))
            using (var outline = new Pen(Input.Focused ? Theme.Teal : Theme.Line, DeviceDpi / 96f))
            using (var path = Theme.Round(box, Math.Min(7 * DeviceDpi / 96, Math.Min(box.Width, box.Height) / 2)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(outline, path);
            }
            int scaled = 16 * DeviceDpi / 96;
            e.Graphics.DrawImage(Theme.Icon(Glyph.Browse, Theme.Muted), new Rectangle(11 * DeviceDpi / 96, (Height - scaled) / 2, scaled, scaled));
        }

    }
}
