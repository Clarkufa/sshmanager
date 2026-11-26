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
        public readonly RdpProfile Desktop;
        public readonly VncProfile Vnc;
        /// <summary>Quick connect: ssh arguments without a config entry.</summary>
        public readonly string DirectArguments;
        /// <summary>A one-off remote command such as «Скопировать ключ на сервер».</summary>
        public readonly string RemoteCommand;
        public string Protocol => Desktop != null ? "RDP" : Vnc != null ? "VNC" : "SSH";
        public string Key => Desktop != null ? RdpKey(Desktop) : Vnc != null ? Vnc.Key : DirectArguments != null ? "quick:" + Address :
            RemoteCommand != null ? Alias + "\ncommand" : Alias;
        public static string RdpKey(RdpProfile profile) => "RDP:" + profile.Id;

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

        public SessionTarget(RdpProfile profile, ServerGroup group)
        {
            Desktop = profile;
            Alias = profile.Name;
            Address = profile.Address;
            Group = group == null ? "" : group.Name;
            Color = TabColor.Find(group?.ColorId)?.Swatch ?? Theme.Teal;
        }

        public SessionTarget(VncProfile profile, ServerGroup group)
        {
            Vnc = profile;
            Alias = profile.Name;
            Address = profile.Address + (profile.Via.Length == 0 ? "" : " · через " + profile.Via);
            Group = group == null ? "" : group.Name;
            Color = TabColor.Find(group?.ColorId)?.Swatch ?? Theme.Teal;
        }

        public SessionTarget(QuickTarget quick)
        {
            Alias = quick.Host;
            Address = quick.Address;
            Group = "";
            Color = Theme.Teal;
            DirectArguments = quick.SshArguments;
        }

        private SessionTarget(SessionTarget original, string command = null)
        {
            Alias = original.Alias;
            Address = original.Address;
            Group = original.Group;
            Color = original.Color;
            Password = original.Password;
            DirectArguments = original.DirectArguments;
            RemoteCommand = command ?? original.RemoteCommand;
            WithoutForwarding = true;
        }

        public SessionTarget SkipForwarding() { return new SessionTarget(this); }
        public SessionTarget WithCommand(string command) { return new SessionTarget(this, command); }

        public string Arguments
        {
            get
            {
                string options = WithoutForwarding ? "-o ClearAllForwardings=yes " : "";
                if (DirectArguments != null) return options + DirectArguments;
                return options + Alias + (RemoteCommand == null ? "" : " " + Terminal.Quote(RemoteCommand));
            }
        }
    }

    internal sealed class TerminalSession : IDisposable
    {
        private static readonly Regex ForwardingError = new Regex(@"cannot\s*listen\s*to\s*port\s*:|could\s*not\s*request\s*local\s*forwarding", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ConsoleSequence = new Regex(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);
        public readonly SessionTarget Target;
        public readonly PseudoTerminal Process;
        public readonly TerminalScreen Screen;
        /// <summary>The RDP or VNC desktop of a remote desktop session, null for SSH.</summary>
        public readonly RemoteDesktopPanel Desktop;
        public readonly int Number;
        public string Name = "";
        public long LastUsed;
        public uint? ExitCode;
        public bool ForwardingFailed;
        /// <summary>Automatic reconnects in a row and when the next one is due.</summary>
        public int Attempts;
        public DateTime? ReconnectAt;
        private string recentOutput = "";
        private int inspectedOutput;

        public TerminalSession(SessionTarget target, PseudoTerminal process, int number, Size size)
        {
            Target = target;
            Process = process;
            Number = number;
            Screen = new TerminalScreen(size.Width, size.Height, bytes => Process.Send(bytes));
        }

        public TerminalSession(SessionTarget target, RemoteDesktopPanel desktop, int number)
        {
            Target = target;
            Desktop = desktop;
            Number = number;
        }

        public string Title
        {
            get
            {
                if (Name.Length > 0) return Target.Alias + " · " + Name;
                return Number <= 1 ? Target.Alias : Target.Alias + " · " + Number;
            }
        }

        public bool IsOpen => Desktop == null ? !ExitCode.HasValue : Desktop.IsOpen;
        public string Status => Desktop?.Status ?? (IsOpen ? "" : "завершён");

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
        public void Dispose()
        {
            Process?.Dispose();
            Desktop?.Dispose();
        }
    }

    internal sealed class SessionsPage : UserControl
    {
        private const string FullScreenHint = "Ctrl+Alt+Break переключает полноэкранный режим внутри сеанса";
        private const string WindowHint = "Переоткрыть RDP-сеанс в отдельном окне";
        private readonly List<TerminalSession> sessions = new List<TerminalSession>();
        private readonly Func<SessionTarget, Size, PseudoTerminal> startProcess;
        private readonly NativeTerminalView terminal;
        private readonly Panel display;
        private readonly TextBox search;
        private readonly ListBox list;
        private readonly Label count, selectedTitle, selectedDetail, empty;
        private readonly Button duplicate, close, files, fullScreenButton, windowButton, reconnectButton, snippetsButton;
        private readonly TableLayoutPanel endedBar;
        private readonly Label endedText;
        private readonly ContextMenuStrip snippetMenu;
        private readonly Timer reconnectTimer;
        private bool broadcast;
        private Preferences preferences = new Preferences();
        private readonly SurfaceButton themeButton;
        private readonly ContextMenuStrip sessionMenu;
        private readonly ToolTip hints;
        private readonly TableLayoutPanel info;
        private readonly Dictionary<Button, string> labels = new Dictionary<Button, string>();
        private readonly Dictionary<Button, int> fullWidths = new Dictionary<Button, int>();
        private bool fittingButtons;
        private readonly Timer timer;
        private TerminalSession active;
        private long activationOrder;
        private bool refreshingList;

        public event Action Changed;
        public event Action ShowServers;
        public event Action PickServer;
        public event Action ThemeRequested;
        public event Action<SessionTarget> ShowFiles;
        public event Action AddSnippet;
        public event Action<Snippet> RemoveSnippet;
        public Func<IList<Snippet>> Snippets;

        /// <summary>Terminal font, scrollback and automatic reconnect from «Настройки».</summary>
        public Preferences Preferences
        {
            get { return preferences; }
            set
            {
                preferences = value ?? new Preferences();
                TerminalScreen.HistoryLines = preferences.Scrollback;
                terminal.SetFont(preferences.TerminalFont, preferences.TerminalSize);
            }
        }

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
                BackColor = Theme.Surface, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Scaled(this, 46) };
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
            var sessionFiles = Theme.MenuItem("Файлы (SFTP)", Glyph.Folder, (sender, args) => OpenFiles());
            var fullScreen = Theme.MenuItem("Полноэкранный режим RDP", Glyph.Server, (sender, args) => OpenFullScreen());
            var detach = Theme.MenuItem("Открыть в отдельном окне", Glyph.Move, (sender, args) => DetachActive());
            var reconnect = Theme.MenuItem("Переподключить", Glyph.Refresh, (sender, args) => active?.Desktop?.Reconnect());
            sessionMenu.Items.Add(sessionFiles);
            sessionMenu.Items.Add(fullScreen);
            sessionMenu.Items.Add(detach);
            sessionMenu.Items.Add(reconnect);
            var withoutForwarding = Theme.MenuItem("Новый сеанс без проброса портов", Glyph.Terminal,
                (sender, args) => DuplicateActive(withoutForwarding: true));
            sessionMenu.Items.Add(withoutForwarding);
            sessionMenu.Items.Add(Theme.MenuItem("Закрыть сеанс", Glyph.Delete, (sender, args) => CloseActive()));
            sessionMenu.Opening += (sender, args) =>
            {
                args.Cancel = active == null || list.SelectedItem == null;
                withoutForwarding.Available = active != null && active.ForwardingFailed;
                sessionFiles.Available = active != null && active.Desktop == null;
                fullScreen.Available = active?.Desktop?.IsConnected == true;
                detach.Available = reconnect.Available = active?.Desktop != null;
            };
            list.ContextMenuStrip = sessionMenu;
            list.MouseDown += (sender, args) =>
            {
                if (args.Button == MouseButtons.Right) list.SelectedIndex = list.IndexFromPoint(args.Location);
            };
            sidebar.Controls.Add(Theme.Frame(list, Padding.Empty), 0, 2);
            layout.Controls.Add(sidebar, 0, 1);

            var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 3 };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            workspace.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 7), ColumnCount = 2, RowCount = 2 };
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // As tall as the buttons, so their border is not cut.
            info.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
            fullScreenButton = Theme.Button("Весь экран", 116, glyph: Glyph.Server);
            fullScreenButton.Click += (sender, args) => OpenFullScreen();
            windowButton = Theme.Button("В окне", 92, glyph: Glyph.Move);
            windowButton.Click += (sender, args) => DetachActive();
            reconnectButton = Theme.Button("Переподключить", 150, glyph: Glyph.Refresh);
            reconnectButton.Click += (sender, args) => active?.Desktop?.Reconnect();
            snippetsButton = Theme.Button("Команды", 106, glyph: Glyph.Terminal);
            snippetsButton.Click += (sender, args) => ShowSnippets();
            snippetMenu = Theme.Menu();
            hints = new ToolTip();
            hints.SetToolTip(fullScreenButton, FullScreenHint);
            hints.SetToolTip(windowButton, WindowHint);
            hints.SetToolTip(snippetsButton, "Сохранённые команды");
            buttons.Controls.AddRange(new Control[] { files, snippetsButton, reconnectButton, windowButton, fullScreenButton, duplicate, close });
            foreach (var button in new[] { files, snippetsButton, reconnectButton, windowButton, fullScreenButton, duplicate })
            {
                labels[button] = button.Text;
                fullWidths[button] = button.Width; // Logical width; FitButtons scales it.
            }
            info.Controls.Add(buttons, 1, 0);
            info.Resize += (sender, args) => FitButtons();
            workspace.Controls.Add(info, 0, 0);

            display = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = NativeTerminalView.Background };
            terminal = new NativeTerminalView { Dock = DockStyle.Fill, AccessibleName = "SSH-терминал" };
            empty = new Label { Dock = DockStyle.Fill, Text = "Выберите сервер в каталоге, чтобы начать сеанс.",
                ForeColor = Color.Gainsboro, TextAlign = ContentAlignment.MiddleCenter, BackColor = NativeTerminalView.Background, Padding = new Padding(16) };
            display.Controls.Add(terminal);
            display.Controls.Add(empty);
            empty.BringToFront();
            workspace.Controls.Add(display, 0, 1);
            endedBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Visible = false,
                Margin = new Padding(0, 7, 0, 0), BackColor = Theme.Canvas };
            endedBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            endedBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            endedText = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Theme.Ink,
                Margin = new Padding(0) };
            var endedActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Right };
            var reconnectSsh = Theme.Button("Переподключить", 150, true, Glyph.Refresh);
            reconnectSsh.Click += (sender, args) => { if (active != null) Reconnect(active, automatic: false); };
            var closeEnded = Theme.Button("Закрыть сеанс", 128);
            closeEnded.Click += (sender, args) => CloseActive();
            endedActions.Controls.AddRange(new Control[] { reconnectSsh, closeEnded });
            endedBar.Controls.Add(endedText, 0, 0);
            endedBar.Controls.Add(endedActions, 1, 0);
            workspace.Controls.Add(endedBar, 0, 2);
            layout.Controls.Add(workspace, 1, 1);

            timer = new Timer { Interval = 33 };
            timer.Tick += (sender, args) => PumpOutput();
            reconnectTimer = new Timer { Interval = 500 };
            reconnectTimer.Tick += (sender, args) => RunDueReconnects();
            RefreshList();
            RefreshDetails();
        }

        public void RefreshThemeButton() { Theme.RefreshSwitcher(themeButton); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FitButtons();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) FitButtons();
        }

        public int CountOpen(string alias = null)
        {
            return sessions.Count(session => session.IsOpen && (alias == null || SameTarget(session, alias)));
        }

        public bool Open(SessionTarget target, bool forceNew, RdpCredential credential = null)
        {
            var related = sessions.Where(session => SameTarget(session, target.Key)).ToArray();
            var existing = forceNew ? null : related.Where(session => session.IsOpen).OrderByDescending(session => session.LastUsed).FirstOrDefault();
            if (existing != null) { Activate(existing, true); return true; }

            int number = related.Length == 0 ? 1 : related.Max(session => session.Number) + 1;
            if (target.Vnc != null)
            {
                OpenVnc(target, number);
                return true;
            }
            if (target.Desktop != null)
            {
                credential = credential ?? RdpLogonDialog.Resolve(this, target.Desktop);
                if (credential == null) return false;
                OpenDesktop(target, number, credential);
                return true;
            }

            var size = terminal.GridSize;
            var child = startProcess(target, size);
            TerminalSession opened;
            try { opened = new TerminalSession(target, child, number, size); }
            catch { child.Dispose(); throw; }
            sessions.Add(opened);
            Activate(opened, true);
            timer.Start();
            Changed?.Invoke();
            return true;
        }

        public void FocusTerminal()
        {
            Control view = active?.Desktop ?? (Control)terminal;
            if (active == null || !view.Visible) return;
            view.Select();
            view.Focus();
        }
        public void FocusSearch() { search.Focus(); search.SelectAll(); }

        public void Cycle()
        {
            var previous = sessions.Where(session => session != active && session.IsOpen).OrderByDescending(session => session.LastUsed).FirstOrDefault();
            if (previous != null) Activate(previous, true);
        }

        private static PseudoTerminal StartSsh(SessionTarget target, Size size)
        {
            if (target.DirectArguments != null) return Terminal.StartEmbedded(target.Arguments, size, null);
            if (!ConfigDocument.IsAlias(target.Alias)) throw new ArgumentException("Недопустимый псевдоним подключения.");
            var password = SshPasswords.Exists(target.Password) ? Terminal.FindPasswordTarget(target.Alias, target.Password.Id) : null;
            return Terminal.StartEmbedded(target.Arguments, size, password);
        }

        private void Activate(TerminalSession selected, bool focus)
        {
            if (selected != active)
            {
                active?.Desktop?.Hide();
                active = selected;
                if (selected != null) selected.LastUsed = ++activationOrder;
                terminal.Attach(selected?.Desktop == null ? selected : null);
                terminal.Visible = selected != null && selected.Desktop == null;
                if (selected?.Desktop != null)
                {
                    selected.Desktop.Show();
                    selected.Desktop.BringToFront();
                }
            }
            RefreshList();
            RefreshDetails();
            if (focus) FocusTerminal();
        }

        private void OpenDesktop(SessionTarget target, int number, RdpCredential credential)
        {
            var previous = active;
            var view = new RdpDesktop { Visible = false };
            Theme.ScaleToDpi(view);
            var opened = new TerminalSession(target, view, number);
            sessions.Add(opened);
            try
            {
                display.Controls.Add(view);
                Activate(opened, false);
                view.Changed += OnDesktopChanged;
                view.CloseRequested += () => CloseSession(opened, confirm: false);
                view.Connect(target.Desktop, credential);
                FocusTerminal();
            }
            catch
            {
                sessions.Remove(opened);
                view.Changed -= OnDesktopChanged;
                Activate(previous, true);
                opened.Dispose();
                throw;
            }
            Changed?.Invoke();
        }

        /// <summary>VNC in the window: the password comes from Credential Manager or, when the server asks, the user.</summary>
        private void OpenVnc(SessionTarget target, int number)
        {
            var previous = active;
            var view = new VncDesktop { Visible = false };
            view.PasswordPrompt = (profile, wrong) => VncLogonDialog.Ask(FindForm(), profile, wrong);
            Theme.ScaleToDpi(view);
            var opened = new TerminalSession(target, view, number);
            sessions.Add(opened);
            try
            {
                display.Controls.Add(view);
                Activate(opened, false);
                view.Changed += OnDesktopChanged;
                view.CloseRequested += () => CloseSession(opened, confirm: false);
                view.Connect(target.Vnc, VncPasswords.Read(target.Vnc));
                FocusTerminal();
            }
            catch
            {
                sessions.Remove(opened);
                view.Changed -= OnDesktopChanged;
                Activate(previous, true);
                opened.Dispose();
                throw;
            }
            Changed?.Invoke();
        }

        private void DetachActive()
        {
            var desktop = active?.Desktop;
            if (desktop == null) return;
            CloseSession(active, confirm: false);
            desktop.OpenInWindow();
        }

        private void OnDesktopChanged()
        {
            RefreshList();
            RefreshDetails();
            Changed?.Invoke();
        }

        private void OpenFullScreen()
        {
            try { active?.Desktop?.FullScreen(); }
            catch (Exception error) when (error is ExternalException || error is System.Reflection.TargetInvocationException)
            { Theme.ShowError(this, error); }
        }

        private void PumpOutput()
        {
            bool exited = false;
            bool repaint = false;
            bool detailsChanged = false;
            foreach (var session in sessions.Where(session => session.IsOpen && session.Process != null))
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
                // 0 is a normal exit and 130 is Ctrl+C; anything else (usually 255) means the connection broke.
                if (preferences.AutoReconnect && code != 0 && code != 130 && session.Target.RemoteCommand == null && session.Attempts < 3)
                {
                    session.Attempts++;
                    session.ReconnectAt = DateTime.UtcNow.AddSeconds(5);
                    reconnectTimer.Start();
                }
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
            if (!sessions.Any(session => session.IsOpen && session.Process != null)) timer.Stop();
        }

        private void RefreshList()
        {
            var query = search.Text.Trim();
            refreshingList = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                var visible = sessions.Where(session => new[] { session.Title, session.Target.Address, session.Target.Group, session.Target.Protocol }
                    .Any(text => text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
                foreach (var session in visible) list.Items.Add(session);
                list.SelectedItem = active;
            }
            finally { list.EndUpdate(); refreshingList = false; }
            int open = CountOpen();
            count.Text = "Открыто: " + open + (open == sessions.Count ? "" : " из " + sessions.Count);
        }

        private void RefreshDetails()
        {
            selectedTitle.Text = active == null ? "Сеансы SSH и RDP" : active.Title;
            selectedDetail.Text = active == null ? "" : active.Target.Protocol + "  ·  " + active.Target.Address +
                (active.Target.Group.Length == 0 ? "" : "  ·  " + active.Target.Group) +
                (active.ForwardingFailed ? "  ·  ошибка проброса порта" : active.Target.WithoutForwarding ? "  ·  без проброса" : "") +
                (active.Status.Length == 0 ? "" : "  ·  " + active.Status);
            close.Enabled = duplicate.Enabled = active != null;
            files.Visible = active != null && active.Desktop == null;
            windowButton.Visible = reconnectButton.Visible = fullScreenButton.Visible = active?.Desktop != null;
            fullScreenButton.Enabled = active?.Desktop?.IsConnected == true;
            snippetsButton.Visible = active != null && active.Desktop == null;
            snippetsButton.Enabled = active != null && active.IsOpen;
            bool ended = active != null && active.Desktop == null && !active.IsOpen;
            if (ended)
                endedText.Text = active.ReconnectAt.HasValue
                    ? "Соединение прервано · повтор через 5 с (попытка " + active.Attempts + " из 3)"
                    : "Сеанс SSH завершён (код " + active.ExitCode + ")";
            endedBar.Visible = ended;
            empty.Visible = active == null;
            FitButtons();
        }

        /// <summary>
        /// Keeps room for the session title: when the buttons do not fit, "Новый сеанс этого сервера" and then the
        /// other buttons show only their icons, with the label in a tooltip.
        /// </summary>
        private void FitButtons()
        {
            // Runs only after the page has been scaled to the DPI, which happens before its handle exists.
            if (fittingButtons || !IsHandleCreated) return;
            fittingButtons = true;
            try
            {
                foreach (var button in labels.Keys) SetCompact(button, false);
                var shown = new List<Control> { duplicate, close };
                if (active?.Desktop != null) shown.InsertRange(0, new Control[] { reconnectButton, windowButton, fullScreenButton });
                else if (active != null) shown.InsertRange(0, new Control[] { files, snippetsButton });
                int available = info.ClientSize.Width - Theme.Scaled(this, 160);
                int Needed() => shown.Sum(button => button.Width + button.Margin.Horizontal);
                if (Needed() > available) SetCompact(duplicate, true);
                if (Needed() > available) foreach (var button in labels.Keys) SetCompact(button, true);
            }
            finally { fittingButtons = false; }
        }

        private void SetCompact(Button button, bool compact)
        {
            if (compact == (button.Text.Length == 0)) return;
            string label = labels[button];
            button.Text = compact ? "" : label;
            button.Width = Theme.Scaled(this, compact ? 38 : fullWidths[button]);
            button.AccessibleName = label;
            hints.SetToolTip(button, compact ? label : button == fullScreenButton ? FullScreenHint : button == windowButton ? WindowHint : null);
        }

        /// <summary>Starts SSH again in place of an ended session, keeping its number and label.</summary>
        private void Reconnect(TerminalSession old, bool automatic)
        {
            if (!sessions.Contains(old) || old.Desktop != null || old.IsOpen) return;
            old.ReconnectAt = null;
            try
            {
                var size = terminal.GridSize;
                var child = startProcess(old.Target, size);
                TerminalSession fresh;
                try { fresh = new TerminalSession(old.Target, child, old.Number, size) { Name = old.Name, LastUsed = old.LastUsed }; }
                catch { child.Dispose(); throw; }
                fresh.Attempts = automatic ? old.Attempts : 0;
                sessions[sessions.IndexOf(old)] = fresh;
                if (old == active)
                {
                    active = null;
                    Activate(fresh, true);
                }
                else RefreshList();
                old.Dispose();
                timer.Start();
                Changed?.Invoke();
            }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is ExternalException || ex is ArgumentException ||
                ex is System.Reflection.TargetInvocationException || ex is InvalidOperationException)
            {
                if (!automatic) Theme.ShowError(this, ex);
                RefreshDetails();
            }
        }

        private void RunDueReconnects()
        {
            var due = sessions.Where(session => session.ReconnectAt.HasValue && session.ReconnectAt <= DateTime.UtcNow).ToArray();
            foreach (var session in due) Reconnect(session, automatic: true);
            if (!sessions.Any(session => session.ReconnectAt.HasValue)) reconnectTimer.Stop();
        }

        /// <summary>«Команды»: saved commands, «Во все открытые сеансы», «Добавить…» and «Удалить».</summary>
        private void ShowSnippets()
        {
            foreach (var item in snippetMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
            var saved = Snippets?.Invoke() ?? new List<Snippet>();
            if (saved.Count == 0) snippetMenu.Items.Add(new ToolStripMenuItem("Сохранённых команд пока нет.") { Enabled = false });
            foreach (var snippet in saved)
            {
                var item = Theme.MenuItem(snippet.Name.Replace("&", "&&"), Glyph.Terminal, (sender, args) => Send(snippet));
                item.ToolTipText = snippet.Command.Length > 300 ? snippet.Command.Substring(0, 300) + "…" : snippet.Command;
                snippetMenu.Items.Add(item);
            }
            snippetMenu.Items.Add(new ToolStripSeparator());
            var everywhere = Theme.MenuItem("Во все открытые сеансы", Glyph.None, (sender, args) =>
            {
                broadcast = !broadcast;
                BeginInvoke(new Action(ShowSnippets));
            });
            everywhere.Checked = broadcast;
            snippetMenu.Items.Add(everywhere);
            snippetMenu.Items.Add(Theme.MenuItem("Добавить…", Glyph.Plus, (sender, args) => AddSnippet?.Invoke()));
            if (saved.Count > 0)
            {
                var remove = Theme.MenuItem("Удалить", Glyph.Delete);
                foreach (var snippet in saved)
                    remove.DropDownItems.Add(Theme.MenuItem(snippet.Name.Replace("&", "&&"), Glyph.None, (sender, args) => RemoveSnippet?.Invoke(snippet)));
                snippetMenu.Items.Add(remove);
            }
            snippetMenu.Show(snippetsButton, new Point(0, snippetsButton.Height));
        }

        private void Send(Snippet snippet)
        {
            var bytes = Encoding.UTF8.GetBytes(snippet.Command.Replace("\r\n", "\r").Replace("\n", "\r") + "\r");
            var targets = broadcast ? sessions.Where(session => session.IsOpen && session.Process != null).ToArray()
                : active != null && active.IsOpen && active.Process != null ? new[] { active } : new TerminalSession[0];
            foreach (var target in targets) target.Process.Send(bytes);
            FocusTerminal();
        }

        private void OpenFiles()
        {
            if (active != null && active.Desktop == null) ShowFiles?.Invoke(active.Target);
        }

        private void DrawSession(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0) return;
            var session = (TerminalSession)list.Items[args.Index];
            var bounds = args.Bounds;
            bool selected = (args.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(selected ? Theme.Selection : Theme.Surface)) args.Graphics.FillRectangle(fill, bounds);
            int S(int logical) => Theme.Scaled(list, logical);
            using (var dot = new SolidBrush(session.IsOpen ? session.Target.Color : Theme.Line)) args.Graphics.FillEllipse(dot, bounds.X + S(9), bounds.Y + S(12), S(7), S(7));
            var title = new Rectangle(bounds.X + S(20), bounds.Y + S(4), bounds.Width - S(26), S(21));
            var subtitle = new Rectangle(bounds.X + S(20), bounds.Y + S(24), bounds.Width - S(26), S(19));
            const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(args.Graphics, session.Title, Theme.Emphasis, title, Theme.Ink, flags);
            TextRenderer.DrawText(args.Graphics, session.Target.Protocol + " · " + (session.IsOpen ? session.Target.Address : "Сеанс завершён"),
                Theme.Body, subtitle, Theme.Muted, flags);
            args.DrawFocusRectangle();
        }

        private void DuplicateActive(bool withoutForwarding = false)
        {
            if (active == null) return;
            try { Open(withoutForwarding ? active.Target.SkipForwarding() : active.Target, true); }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is ExternalException || ex is ArgumentException ||
                ex is System.Reflection.TargetInvocationException || ex is InvalidOperationException)
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
                    SameTarget(item, session.Target.Key) && String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))))
                    throw new ArgumentException("Укажите уникальную метку для сеанса этого сервера.");
                session.Name = name;
            }))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) { RefreshList(); RefreshDetails(); }
            }
        }

        private void CloseActive()
        {
            if (active != null) CloseSession(active, confirm: active.IsOpen);
        }

        private void CloseSession(TerminalSession closing, bool confirm)
        {
            if (!sessions.Contains(closing)) return;
            if (confirm && MessageBox.Show(this, "Закрыть " + closing.Target.Protocol + "-сеанс «" + closing.Title + "»?", "Вы уверены?",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            sessions.Remove(closing);
            if (closing == active) Activate(sessions.OrderByDescending(session => session.LastUsed).FirstOrDefault(), true);
            else { RefreshList(); RefreshDetails(); }
            closing.Desktop?.Hide();
            // The overlay's own button can request the close, so release the ActiveX control after that click returns.
            BeginInvoke(new Action(closing.Dispose));
            Changed?.Invoke();
        }

        private static bool SameTarget(TerminalSession session, string key)
        {
            return String.Equals(session.Target.Key, key, StringComparison.OrdinalIgnoreCase);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (timer != null) timer.Dispose();
                reconnectTimer?.Dispose();
                snippetMenu?.Dispose();
                if (sessionMenu != null) sessionMenu.Dispose();
                if (hints != null) hints.Dispose();
                foreach (var session in sessions) session.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
