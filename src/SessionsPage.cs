using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class SessionTarget
    {
        public readonly string Alias, Address, Group;
        public readonly Color Color;
        public readonly PasswordTarget Password;
        public readonly bool WithoutForwarding;

        public SessionTarget(Connection connection, ServerGroup group)
        {
            Alias = connection.Alias;
            Password = PasswordTarget.From(connection);
            var host = connection.Get("hostname");
            var user = connection.Get("user");
            var port = connection.Get("port");
            if (host.Length == 0) host = Alias;
            if (port.Length > 0 && host.Contains(":")) host = "[" + host + "]";
            Address = (user.Length == 0 ? "" : user + "@") + host + (port.Length == 0 ? "" : ":" + port);
            Group = group == null ? "" : group.Name;
            var color = group == null ? null : TabColor.Find(group.ColorId);
            Color = color == null ? Theme.Teal : color.Swatch;
        }

        private SessionTarget(SessionTarget original)
        {
            Alias = original.Alias;
            Address = original.Address;
            Group = original.Group;
            Color = original.Color;
            Password = original.Password;
            WithoutForwarding = true;
        }

        public SessionTarget SkipForwarding() { return new SessionTarget(this); }
        public string Arguments { get { return WithoutForwarding ? "-o ClearAllForwardings=yes " + Alias : Alias; } }
    }

    internal sealed class TerminalSession : IDisposable
    {
        private static readonly Regex ForwardingError = new Regex(@"cannot\s*listen\s*to\s*port\s*:|could\s*not\s*request\s*local\s*forwarding", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ConsoleSequence = new Regex(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);
        public readonly SessionTarget Target;
        public readonly PseudoTerminal Process;
        public readonly TerminalScreen Screen;
        public readonly int Number;
        public string Name = "";
        public long LastUsed;
        public uint? ExitCode;
        public bool ForwardingFailed;
        private string recentOutput = "";
        private int inspectedOutput;

        public TerminalSession(SessionTarget target, PseudoTerminal process, int number, Size size)
        {
            Target = target;
            Process = process;
            Number = number;
            Screen = new TerminalScreen(size.Width, size.Height, bytes => Process.Send(bytes));
        }

        public string Title
        {
            get
            {
                if (Name.Length > 0) return Target.Alias + " · " + Name;
                return Number <= 1 ? Target.Alias : Target.Alias + " · " + Number;
            }
        }

        public bool IsOpen { get { return !ExitCode.HasValue; } }

        public bool CheckForForwardingError(byte[] bytes)
        {
            if (ForwardingFailed || Target.WithoutForwarding || inspectedOutput >= 65536) return false;
            inspectedOutput += bytes.Length;
            var text = recentOutput + Encoding.ASCII.GetString(bytes);
            ForwardingFailed = ForwardingError.IsMatch(ConsoleSequence.Replace(text, ""));
            if (!ForwardingFailed)
            {
                // ConPTY can replace a trailing space and cursor movement with screen-positioning sequences.
                var terminal = Screen.Engine;
                var start = Math.Max(0, terminal.ViewPort.TopRow - 3);
                var end = Math.Min(terminal.BottomRow, terminal.ViewPort.TopRow + terminal.VisibleRows);
                for (int row = start; row < end && !ForwardingFailed; row++)
                {
                    var line = terminal.ViewPort.GetLine(row);
                    if (line != null) ForwardingFailed = ForwardingError.IsMatch(new string(line.Select(cell => cell.Char).ToArray()));
                }
            }
            recentOutput = text.Substring(Math.Max(0, text.Length - 160));
            return ForwardingFailed;
        }
        public override string ToString() { return Title; }
        public void Dispose() { Process.Dispose(); }
    }

    internal sealed class SessionsPage : UserControl
    {
        private readonly List<TerminalSession> sessions = new List<TerminalSession>();
        private readonly Func<SessionTarget, Size, PseudoTerminal> startProcess;
        private readonly NativeTerminalView terminal;
        private readonly TextBox search;
        private readonly ListBox list;
        private readonly Label count, selectedTitle, selectedDetail, empty;
        private readonly Button duplicate, close, files;
        private readonly SurfaceButton themeButton;
        private readonly ContextMenuStrip sessionMenu;
        private readonly Timer timer;
        private TerminalSession active;
        private long activationOrder;
        private bool refreshingList;

        public event Action Changed;
        public event Action ShowServers;
        public event Action PickServer;
        public event Action ThemeRequested;
        public event Action<SessionTarget> ShowFiles;

        public SessionsPage(Func<SessionTarget, Size, PseudoTerminal> startProcess = null)
        {
            this.startProcess = startProcess ?? StartSsh;
            Dock = DockStyle.Fill;
            BackColor = Theme.Canvas;
            ForeColor = Theme.Ink;
            Font = Theme.Body;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(24, 15, 24, 21) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 234));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 61));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Margin = new Padding(0, 0, 0, 9) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 51));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new PictureBox { Image = Theme.AppMark(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(42, 42), Anchor = AnchorStyles.Left, Margin = new Padding(0) }, 0, 0);
            header.Controls.Add(new Label { Text = "Сеансы", Font = Theme.Title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0) }, 1, 0);
            var servers = Theme.Button("К серверам", 126, glyph: Glyph.MoveLeft);
            servers.Anchor = AnchorStyles.Right;
            servers.Click += (sender, args) => ShowServers?.Invoke();
            themeButton = Theme.Button("", 130);
            themeButton.Anchor = AnchorStyles.Right;
            RefreshThemeButton();
            themeButton.Click += (sender, args) => ThemeRequested?.Invoke();
            header.Controls.Add(themeButton, 2, 0);
            header.Controls.Add(servers, 3, 0);
            var newSession = Theme.Button("Новый сеанс", 132, true, Glyph.Plus);
            newSession.Anchor = AnchorStyles.Right;
            newSession.Click += (sender, args) => PickServer?.Invoke();
            header.Controls.Add(newSession, 4, 0);
            layout.Controls.Add(header, 0, 0);
            layout.SetColumnSpan(header, 2);

            var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), ColumnCount = 1, RowCount = 3 };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var filter = new ServerSearch("Найти сеанс", "Поиск открытых сеансов") { Dock = DockStyle.Top };
            search = filter.Input;
            search.TextChanged += (sender, args) => RefreshList();
            search.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter || list.Items.Count == 0) return;
                args.SuppressKeyPress = true;
                Activate(list.SelectedItem as TerminalSession ?? (TerminalSession)list.Items[0], true);
            };
            sidebar.Controls.Add(filter, 0, 0);
            count = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 0) };
            sidebar.Controls.Add(count, 0, 1);
            list = new ListBox { AccessibleName = "Список сеансов", Dock = DockStyle.Fill, Margin = new Padding(0), IntegralHeight = false,
                BackColor = Theme.Surface, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46 };
            list.DrawItem += DrawSession;
            list.SelectedIndexChanged += (sender, args) =>
            {
                if (!refreshingList && list.SelectedItem is TerminalSession) Activate((TerminalSession)list.SelectedItem, false);
            };
            list.MouseClick += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left && list.IndexFromPoint(args.Location) >= 0) FocusTerminal();
            };
            list.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter || list.SelectedItem == null) return;
                args.SuppressKeyPress = true;
                FocusTerminal();
            };
            sessionMenu = Theme.Menu();
            sessionMenu.Items.Add(Theme.MenuItem("Переименовать сеанс", Glyph.Edit, (sender, args) => RenameActive()));
            sessionMenu.Items.Add(Theme.MenuItem("Новый сеанс этого сервера", Glyph.Plus, (sender, args) => DuplicateActive()));
            sessionMenu.Items.Add(Theme.MenuItem("Файлы (SFTP)", Glyph.Folder, (sender, args) => OpenFiles()));
            var withoutForwarding = Theme.MenuItem("Новый сеанс без проброса портов", Glyph.Terminal,
                (sender, args) => DuplicateActive(withoutForwarding: true));
            sessionMenu.Items.Add(withoutForwarding);
            sessionMenu.Items.Add(Theme.MenuItem("Закрыть сеанс", Glyph.Delete, (sender, args) => CloseActive()));
            sessionMenu.Opening += (sender, args) =>
            {
                args.Cancel = active == null || list.SelectedItem == null;
                withoutForwarding.Available = active != null && active.ForwardingFailed;
            };
            list.ContextMenuStrip = sessionMenu;
            list.MouseDown += (sender, args) =>
            {
                if (args.Button == MouseButtons.Right) list.SelectedIndex = list.IndexFromPoint(args.Location);
            };
            sidebar.Controls.Add(Theme.Frame(list, Padding.Empty), 0, 2);
            layout.Controls.Add(sidebar, 0, 1);

            var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 2 };
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var info = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 7), ColumnCount = 2, RowCount = 2 };
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
            info.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
            selectedTitle = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = Theme.Emphasis, ForeColor = Theme.Ink, Margin = new Padding(0) };
            selectedDetail = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Theme.Muted, Margin = new Padding(0) };
            info.Controls.Add(selectedTitle, 0, 0);
            info.Controls.Add(selectedDetail, 0, 1);
            info.SetColumnSpan(selectedDetail, 2);
            var buttons = new FlowLayoutPanel { Anchor = AnchorStyles.Right, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            duplicate = Theme.Button("Новый сеанс этого сервера", 220, glyph: Glyph.Plus, quiet: true);
            duplicate.Click += (sender, args) => DuplicateActive();
            files = Theme.Button("Файлы", 86, glyph: Glyph.Folder);
            files.Click += (sender, args) => OpenFiles();
            close = Theme.Button("Закрыть", 81, danger: true, quiet: true);
            close.Click += (sender, args) => CloseActive();
            buttons.Controls.AddRange(new Control[] { files, duplicate, close });
            info.Controls.Add(buttons, 1, 0);
            workspace.Controls.Add(info, 0, 0);

            var display = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = NativeTerminalView.Background };
            terminal = new NativeTerminalView { Dock = DockStyle.Fill, AccessibleName = "SSH-терминал" };
            empty = new Label { Dock = DockStyle.Fill, Text = "Выберите сервер в каталоге, чтобы начать SSH-сеанс.",
                ForeColor = Color.Gainsboro, TextAlign = ContentAlignment.MiddleCenter, BackColor = NativeTerminalView.Background, Padding = new Padding(16) };
            display.Controls.Add(terminal);
            display.Controls.Add(empty);
            empty.BringToFront();
            workspace.Controls.Add(display, 0, 1);
            layout.Controls.Add(workspace, 1, 1);

            timer = new Timer { Interval = 33 };
            timer.Tick += (sender, args) => PumpOutput();
            RefreshList();
            RefreshDetails();
        }

        public void RefreshThemeButton() { Theme.RefreshSwitcher(themeButton); }

        public int CountOpen(string alias = null)
        {
            return sessions.Count(session => session.IsOpen && (alias == null || SameAlias(session, alias)));
        }

        public void Open(SessionTarget target, bool forceNew)
        {
            var related = sessions.Where(session => SameAlias(session, target.Alias)).ToArray();
            var existing = forceNew ? null : related.Where(session => session.IsOpen).OrderByDescending(session => session.LastUsed).FirstOrDefault();
            if (existing != null) { Activate(existing, true); return; }

            var size = terminal.GridSize;
            var child = startProcess(target, size);
            TerminalSession opened;
            try { opened = new TerminalSession(target, child, related.Length == 0 ? 1 : related.Max(session => session.Number) + 1, size); }
            catch { child.Dispose(); throw; }
            sessions.Add(opened);
            Activate(opened, true);
            timer.Start();
            Changed?.Invoke();
        }

        public void FocusTerminal()
        {
            if (active == null || !terminal.Visible) return;
            terminal.Select();
            terminal.Focus();
        }
        public void FocusSearch() { search.Focus(); search.SelectAll(); }

        public void Cycle()
        {
            var previous = sessions.Where(session => session != active && session.IsOpen).OrderByDescending(session => session.LastUsed).FirstOrDefault();
            if (previous != null) Activate(previous, true);
        }

        private static PseudoTerminal StartSsh(SessionTarget target, Size size)
        {
            if (!ConfigDocument.IsAlias(target.Alias)) throw new ArgumentException("Недопустимый псевдоним подключения.");
            var password = SshPasswords.Exists(target.Password) ? Terminal.FindPasswordTarget(target.Alias, target.Password.Id) : null;
            return Terminal.StartEmbedded(target.Arguments, size, password);
        }

        private void Activate(TerminalSession selected, bool focus)
        {
            if (selected != active)
            {
                active = selected;
                if (selected != null) selected.LastUsed = ++activationOrder;
                terminal.Attach(selected);
            }
            RefreshList();
            RefreshDetails();
            if (focus) FocusTerminal();
        }

        private void PumpOutput()
        {
            bool exited = false;
            bool repaint = false;
            bool detailsChanged = false;
            foreach (var session in sessions.Where(session => session.IsOpen))
            {
                byte[] bytes;
                for (int i = 0; i < 12 && session.Process.TryRead(out bytes); i++)
                {
                    session.Screen.Feed(bytes);
                    if (session.CheckForForwardingError(bytes) && session == active) detailsChanged = true;
                    repaint |= session == active;
                }
                uint code;
                if (!session.Process.TryGetExitCode(out code)) continue;
                session.ExitCode = code;
                session.Screen.Feed(Encoding.UTF8.GetBytes("\r\n\u001b[0mSSH завершился (код " + code + ").\r\n"));
                exited = true;
                repaint |= session == active;
            }
            if (repaint) terminal.Invalidate();
            if (detailsChanged && !exited) RefreshDetails();
            if (exited)
            {
                RefreshList();
                RefreshDetails();
                Changed?.Invoke();
            }
            if (CountOpen() == 0) timer.Stop();
        }

        private void RefreshList()
        {
            var query = search.Text.Trim();
            refreshingList = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                var visible = sessions.Where(session => new[] { session.Title, session.Target.Address, session.Target.Group }
                    .Any(text => text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
                foreach (var session in visible) list.Items.Add(session);
                list.SelectedItem = active;
            }
            finally { list.EndUpdate(); refreshingList = false; }
            count.Text = "Открыто: " + CountOpen();
        }

        private void RefreshDetails()
        {
            selectedTitle.Text = active == null ? "SSH-терминал" : active.Title;
            selectedDetail.Text = active == null ? "" : active.Target.Address +
                (active.Target.Group.Length == 0 ? "" : "  ·  " + active.Target.Group) +
                (active.ForwardingFailed ? "  ·  ошибка проброса порта" : active.Target.WithoutForwarding ? "  ·  без проброса" : "") +
                (active.IsOpen ? "" : "  ·  завершён");
            close.Enabled = duplicate.Enabled = files.Enabled = active != null;
            empty.Visible = active == null;
        }

        private void OpenFiles()
        {
            if (active != null) ShowFiles?.Invoke(active.Target);
        }

        private void DrawSession(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0) return;
            var session = (TerminalSession)list.Items[args.Index];
            var bounds = args.Bounds;
            bool selected = (args.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(selected ? Theme.Selection : Theme.Surface)) args.Graphics.FillRectangle(fill, bounds);
            using (var dot = new SolidBrush(session.IsOpen ? session.Target.Color : Theme.Line)) args.Graphics.FillEllipse(dot, bounds.X + 9, bounds.Y + 12, 7, 7);
            var title = new Rectangle(bounds.X + 20, bounds.Y + 4, bounds.Width - 26, 21);
            var subtitle = new Rectangle(bounds.X + 20, bounds.Y + 24, bounds.Width - 26, 19);
            const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(args.Graphics, session.Title, Theme.Emphasis, title, Theme.Ink, flags);
            TextRenderer.DrawText(args.Graphics, session.IsOpen ? session.Target.Address : "Завершён · " + session.Target.Address, Theme.Body, subtitle, Theme.Muted, flags);
            args.DrawFocusRectangle();
        }

        private void DuplicateActive(bool withoutForwarding = false)
        {
            if (active == null) return;
            try { Open(withoutForwarding ? active.Target.SkipForwarding() : active.Target, true); }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is ExternalException || ex is ArgumentException)
            {
                Theme.ShowError(this, ex);
            }
        }

        private void RenameActive()
        {
            var session = active;
            if (session == null) return;
            using (var dialog = new NameDialog("Переименовать сеанс", "Метка сеанса", session.Name, "Сохранить", name =>
            {
                if (name.Any(char.IsControl) || (name.Length > 0 && sessions.Any(item => item != session &&
                    SameAlias(item, session.Target.Alias) && String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))))
                    throw new ArgumentException("Укажите уникальную метку для сеанса этого сервера.");
                session.Name = name;
            }))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) { RefreshList(); RefreshDetails(); }
            }
        }

        private void CloseActive()
        {
            var closing = active;
            if (closing == null) return;
            if (MessageBox.Show(this, "Закрыть SSH-сеанс «" + closing.Title + "»?", "Вы уверены?",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            sessions.Remove(closing);
            closing.Dispose();
            Activate(sessions.OrderByDescending(session => session.LastUsed).FirstOrDefault(), true);
            Changed?.Invoke();
        }

        private static bool SameAlias(TerminalSession session, string alias)
        {
            return String.Equals(session.Target.Alias, alias, StringComparison.OrdinalIgnoreCase);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (timer != null) timer.Dispose();
                if (sessionMenu != null) sessionMenu.Dispose();
                foreach (var session in sessions) session.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
