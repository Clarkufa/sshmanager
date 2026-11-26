using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class ConnectionsWindow : ThemedForm
    {
        private const string PortColumn = "Port", FilesColumn = "FilesAction", TerminalColumn = "TerminalAction";
        /// <summary>The «Избранное» tab; group ids are 32 hex digits, so this never collides with one.</summary>
        private const string FavoritesId = "favorites";
        private const int ActionOuterPadding = 5, ActionInnerPadding = 7, ActionIcon = 16, ActionGap = 6;
        private const TextFormatFlags ActionText = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;
        private readonly Dictionary<string, SftpWindow> fileWindows = new Dictionary<string, SftpWindow>(StringComparer.Ordinal);
        private readonly string sshDirectory;
        private readonly ConfigFile file;
        private readonly GroupFile groupFile;
        private readonly DataGridView grid;
        private readonly TabStrip tabs;
        private readonly TextBox search;
        private readonly Button add;
        private readonly Button edit;
        private readonly Button delete;
        private readonly Button sessionsButton;
        private readonly Button backupButton;
        private readonly SurfaceButton themeButton;
        private readonly SurfaceButton updateButton;
        private readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        private AppUpdate availableUpdate;
        private readonly Panel pages;
        private readonly TableLayoutPanel catalog;
        private readonly ContextMenuStrip backupMenu;
        private readonly BackupCommands backup;
        private readonly PathLabel source;
        private readonly ToolTip hints = new ToolTip();
        private readonly Label status;
        private readonly Label empty;
        private readonly Label catalogTitle;
        private readonly SurfaceButton sshTab, rdpTab, vncTab;
        private readonly string settingsPath;
        private readonly ContextMenuStrip serverMenu;
        private ContextMenuStrip desktopMenu;
        private readonly ContextMenuStrip tabMenu;
        private readonly ToolStripMenuItem tabColorMenu;
        private readonly ToolStripMenuItem moveTabLeft;
        private readonly ToolStripMenuItem moveTabRight;
        private readonly ToolStripMenuItem startupTab;
        private readonly ToolStripMenuItem moveMenu;
        private readonly ToolStripMenuItem favoriteItem;
        private readonly TextBox quick;
        private readonly SurfaceButton settingsButton;
        private readonly TableLayoutPanel catalogHeader;
        private Groups groups = new Groups();
        private Connection menuConnection;
        private ConfigDocument document;
        private string activeGroupId;
        private SessionsPage sessions;
        private CatalogKind kind = CatalogKind.Ssh;
        private bool showRdp => kind == CatalogKind.Rdp;

        public ConnectionsWindow(string sshDirectory, string groupsPath)
        {
            this.sshDirectory = sshDirectory;
            settingsPath = groupsPath;
            string path = Path.Combine(sshDirectory, "config");
            file = new ConfigFile(path);
            groupFile = new GroupFile(groupsPath);
            try { groups = groupFile.Load(); }
            catch (Exception ex) when (Theme.IsFileError(ex)) { /* Reload reports this when the window opens. */ }
            Theme.IsDark = groups.DarkTheme;
            backup = new BackupCommands(this, sshDirectory, groupsPath, () => Reload(openStartupTab: true));
            Text = "SSH Manager " + AppUpdates.CurrentVersion.ToString(3);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterScreen;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(980, 570);
            MinimumSize = new Size(800, 480);

            var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            pages = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            frame.Controls.Add(pages, 0, 0);
            Controls.Add(frame);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 15, 24, 0), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            catalog = layout;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pages.Controls.Add(layout);

            add = Theme.Button("Добавить сервер", 158, true, Glyph.Plus);
            add.Click += (sender, args) =>
            {
                if (kind == CatalogKind.Rdp) EditDesktop(null);
                else if (kind == CatalogKind.Vnc) EditVnc(null);
                else EditConnection(null);
            };
            var header = catalogHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, Margin = new Padding(0, 0, 0, 7) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 51));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new PictureBox { Image = Theme.AppMark(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(42, 42), Anchor = AnchorStyles.Left, Margin = new Padding(0) }, 0, 0);
            catalogTitle = new Label { Text = "SSH-подключения", Font = Theme.Title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            header.Controls.Add(catalogTitle, 1, 0);
            add.Anchor = AnchorStyles.Right;
            sessionsButton = Theme.Button("Сеансы", 116, glyph: Glyph.Terminal);
            sessionsButton.Anchor = AnchorStyles.Right;
            sessionsButton.Click += (sender, args) => ShowSessions();
            themeButton = Theme.Button("", 130);
            themeButton.Anchor = AnchorStyles.Right;
            Theme.RefreshSwitcher(themeButton);
            themeButton.Click += (sender, args) => ToggleTheme();
            settingsButton = Theme.Button("Настройки", 118, glyph: Glyph.Settings, quiet: true);
            settingsButton.Anchor = AnchorStyles.Right;
            settingsButton.Click += (sender, args) => EditSettings();
            header.Controls.Add(settingsButton, 2, 0);
            header.Controls.Add(themeButton, 3, 0);
            header.Controls.Add(sessionsButton, 4, 0);
            header.Controls.Add(add, 5, 0);
            layout.Controls.Add(header, 0, 0);
            header.Resize += (sender, args) => FitHeader();

            var protocols = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            sshTab = Theme.Button("SSH", 86, glyph: Glyph.Terminal);
            rdpTab = Theme.Button("RDP", 86, glyph: Glyph.Server);
            vncTab = Theme.Button("VNC", 86, glyph: Glyph.Server);
            sshTab.AccessibleRole = rdpTab.AccessibleRole = vncTab.AccessibleRole = AccessibleRole.PageTab;
            sshTab.Style = ButtonStyle.SelectedTab;
            rdpTab.Style = vncTab.Style = ButtonStyle.Tab;
            sshTab.Click += (sender, args) => SelectProtocol(CatalogKind.Ssh);
            rdpTab.Click += (sender, args) => SelectProtocol(CatalogKind.Rdp);
            vncTab.Click += (sender, args) => SelectProtocol(CatalogKind.Vnc);
            protocols.Controls.AddRange(new Control[] { sshTab, rdpTab, vncTab });
            var protocolRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            protocolRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            protocolRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
            protocolRow.Controls.Add(protocols, 0, 0);
            quick = new TextBox { AccessibleName = "Быстрое подключение", MaxLength = 300 };
            Theme.SetCue(quick, "Быстрое подключение: user@host, rdp:// или vnc://");
            hints.SetToolTip(quick, "Подключиться без сохранения профиля. Enter — подключиться.");
            quick.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Enter) { args.SuppressKeyPress = true; QuickConnect(); }
                else if (args.KeyCode == Keys.Escape && quick.TextLength > 0) { args.SuppressKeyPress = true; quick.Clear(); }
            };
            protocolRow.Controls.Add(Theme.Frame(quick, new Padding(0, 2, 0, 3)), 1, 0);
            layout.Controls.Add(protocolRow, 0, 1);

            var tabBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 1) };
            tabBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tabBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tabs = new TabStrip { Dock = DockStyle.Fill, Margin = new Padding(0) };
            tabs.ReorderRequested += (id, anchorId, before) =>
                UpdateGroups(items => items.PlaceTab(id, anchorId, before), activeGroupId, SelectedKey);
            tabBar.Controls.Add(tabs, 0, 0);
            var newTab = Theme.Button("Новая вкладка", 140, glyph: Glyph.Plus, quiet: true);
            newTab.Height = 31;
            newTab.Click += (sender, args) => EditGroup(null);
            tabBar.Controls.Add(newTab, 1, 0);
            layout.Controls.Add(tabBar, 0, 2);

            var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.Controls.Add(tools, 0, 3);

            var filter = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            var serverSearch = new ServerSearch();
            search = serverSearch.Input;
            search.TextChanged += (sender, args) => RefreshList(null);
            filter.Controls.Add(serverSearch);
            filter.Resize += (sender, args) =>
            {
                if (filter.IsHandleCreated)
                    serverSearch.Width = Math.Max(Theme.Scaled(this, 140), Math.Min(Theme.Scaled(this, 265), filter.ClientSize.Width - 2));
            };
            search.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Escape && search.TextLength > 0) { search.Clear(); args.SuppressKeyPress = true; }
                else if (args.KeyCode == Keys.Down && grid.Rows.Count > 0) { grid.Focus(); args.SuppressKeyPress = true; }
            };
            tools.Controls.Add(filter, 0, 0);

            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            edit = Theme.Button("Изменить", 105, glyph: Glyph.Edit, quiet: true);
            delete = Theme.Button("Удалить", 95, glyph: Glyph.Delete, danger: true, quiet: true);
            var refresh = Theme.Button("Обновить", 106, glyph: Glyph.Refresh, quiet: true);
            backupButton = Theme.Button("Резервные копии", 154, glyph: Glyph.Folder, quiet: true);
            backupMenu = Theme.Menu();
            backupMenu.Items.Add(Theme.MenuItem("Экспорт архива…", Glyph.Copy, async (sender, args) => await RunBackup(backup.Export)));
            backupMenu.Items.Add(Theme.MenuItem("Импорт архива…", Glyph.Folder, async (sender, args) => await RunBackup(backup.Import)));
            backupMenu.Items.Add(new ToolStripSeparator());
            backupMenu.Items.Add(Theme.MenuItem("Импорт из других программ…", Glyph.Folder, (sender, args) => ImportOther()));
            backupButton.Click += (sender, args) => backupMenu.Show(backupButton, new Point(0, backupButton.Height));
            edit.Click += (sender, args) => EditRow(grid.CurrentRow?.Tag);
            delete.Click += (sender, args) =>
            {
                if (kind == CatalogKind.Rdp) DeleteDesktop(SelectedDesktop);
                else if (kind == CatalogKind.Vnc) DeleteVnc(SelectedVnc);
                else DeleteConnection(Selected);
            };
            refresh.Click += (sender, args) => Reload();
            actions.Controls.AddRange(new Control[] { backupButton, refresh, edit, delete });
            hints.SetToolTip(add, "Ctrl+N");
            hints.SetToolTip(refresh, "Перечитать SSH config и настройки (F5)");
            hints.SetToolTip(edit, "Изменить выбранное подключение (F2)");
            hints.SetToolTip(delete, "Удалить выбранное подключение (Delete)");
            tools.Controls.Add(actions, 1, 0);

            var list = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(1), BackColor = Theme.Line };
            layout.Controls.Add(list, 0, 4);
            grid = MakeGrid(DeviceDpi);
            grid.SelectionChanged += (sender, args) => UpdateActions();
            grid.CellMouseClick += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left && args.RowIndex >= 0 && args.ColumnIndex >= 0)
                    RunGridAction(grid.Columns[args.ColumnIndex], grid.Rows[args.RowIndex].Tag);
            };
            grid.CellDoubleClick += (sender, args) =>
            {
                if (args.RowIndex >= 0 && args.ColumnIndex >= 0 && !(grid.Columns[args.ColumnIndex] is DataGridViewButtonColumn))
                    EditRow(grid.Rows[args.RowIndex].Tag);
            };
            grid.ColumnHeaderMouseClick += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left && args.ColumnIndex >= 0 && args.ColumnIndex <= 1) SortColumn(args.ColumnIndex);
            };
            grid.CellPainting += PaintGridCell;
            grid.MouseUp += (sender, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                var hit = grid.HitTest(args.X, args.Y);
                if (hit.RowIndex >= 0 && hit.ColumnIndex >= 0) ShowServerMenu(hit.RowIndex, args.Location);
            };
            grid.KeyDown += (sender, args) =>
            {
                if (grid.CurrentRow?.Tag == null) return;
                if (args.KeyCode == Keys.Delete && args.Modifiers == Keys.None) { args.Handled = true; delete.PerformClick(); return; }
                if (args.KeyCode == Keys.F2 && args.Modifiers == Keys.None) { args.Handled = true; EditRow(grid.CurrentRow.Tag); return; }
                if ((args.KeyCode == Keys.Enter || args.KeyCode == Keys.Space) &&
                    RunGridAction(grid.CurrentCell.OwningColumn, grid.CurrentRow.Tag))
                {
                    args.Handled = args.SuppressKeyPress = true;
                    return;
                }
                if (!IsMenuKey(args)) return;
                args.Handled = true;
                int row = grid.CurrentRow.Index;
                ShowServerMenu(row, new Point(12, grid.GetCellDisplayRectangle(0, row, true).Bottom));
            };
            list.Controls.Add(grid);

            moveMenu = Theme.MenuItem("Переместить во вкладку", Glyph.Move);
            favoriteItem = Theme.MenuItem("В избранное", Glyph.Star, (sender, args) => ToggleFavorite(Groups.Key(menuConnection.Alias), menuConnection.Alias));
            serverMenu = Theme.Menu();
            serverMenu.Items.AddRange(new ToolStripItem[]
            {
                Theme.MenuItem("Подключиться", Glyph.Terminal, (sender, args) => Connect(menuConnection)),
                Theme.MenuItem("Новый сеанс", Glyph.Plus, (sender, args) => Connect(menuConnection, forceNew: true)),
                Theme.MenuItem("Файлы (SFTP)", Glyph.Folder, (sender, args) => OpenFiles(menuConnection)),
                Theme.MenuItem("Открыть в Windows Terminal", Glyph.Terminal, (sender, args) => ConnectExternal(menuConnection)),
                Theme.MenuItem("Изменить", Glyph.Edit, (sender, args) => EditConnection(menuConnection)),
                favoriteItem,
                moveMenu,
                Theme.MenuItem("Копировать команду SSH", Glyph.Copy, (sender, args) => CopyCommand(menuConnection)),
                Theme.MenuItem("Скопировать ключ на сервер", Glyph.Key, (sender, args) => CopyKey(menuConnection)),
                new ToolStripSeparator(),
                Theme.MenuItem("Удалить", Glyph.Delete, (sender, args) => DeleteConnection(menuConnection))
            });
            tabMenu = Theme.Menu();
            tabColorMenu = Theme.MenuItem("Цветовая метка", Glyph.Dot);
            AddColorMenuItem(null);
            foreach (var color in TabColor.Palette) AddColorMenuItem(color);
            moveTabLeft = Theme.MenuItem("Переместить влево", Glyph.MoveLeft, (sender, args) => ShiftActiveTab(-1));
            moveTabRight = Theme.MenuItem("Переместить вправо", Glyph.Move, (sender, args) => ShiftActiveTab(1));
            startupTab = Theme.MenuItem("Открывать при запуске", Glyph.None, (sender, args) =>
            {
                string id = activeGroupId;
                if (groups.StartupTabId != id)
                    UpdateGroups(items => items.SetStartupTab(id), id, SelectedKey);
            });
            tabMenu.Items.AddRange(new ToolStripItem[]
            {
                Theme.MenuItem("Переименовать вкладку", Glyph.Edit, (sender, args) => EditGroup(activeGroupId)),
                tabColorMenu,
                moveTabLeft,
                moveTabRight,
                startupTab,
                new ToolStripSeparator(),
                Theme.MenuItem("Удалить вкладку", Glyph.Delete, (sender, args) => DeleteGroup())
            });

            empty = new Label
            {
                TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, BackColor = Theme.Surface,
                ForeColor = Theme.Muted, Padding = new Padding(16)
            };
            list.Controls.Add(empty);
            empty.BringToFront();

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty, Padding = new Padding(24, 0, 24, 5)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            source = new PathLabel { Text = path, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Margin = Padding.Empty };
            hints.SetToolTip(source, path);
            status = new Label { Anchor = AnchorStyles.Right, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(10, 0, 12, 0) };
            updateButton = Theme.Button("Обновления", 170, glyph: Glyph.Refresh);
            updateButton.Anchor = AnchorStyles.Right;
            updateButton.Click += async (sender, args) => await CheckUpdates(interactive: true);
            footer.Controls.Add(source, 0, 0);
            footer.Controls.Add(status, 1, 0);
            footer.Controls.Add(updateButton, 2, 0);
            frame.Controls.Add(footer, 0, 1);

            Theme.ScaleToDpi(this);
            ResumeLayout();
            Shown += (sender, args) => Reload(openStartupTab: true);
            Shown += async (sender, args) => await CheckUpdates(interactive: false);
        }

        private const string FavoritesHint = "Отметьте серверы звёздочкой: «В избранное» в меню строки.";

        /// <summary>The open user tab: null for «Все серверы» and «Избранное».</summary>
        private string ActiveTabId => activeGroupId == FavoritesId ? null : activeGroupId;

        private Connection Selected => grid.CurrentRow?.Tag as Connection;

        /// <summary>
        /// Keeps the page title whole: in a narrow window «Настройки» and then the theme switcher show only their
        /// icons, with the label in a tooltip.
        /// </summary>
        private void FitHeader()
        {
            if (!catalogHeader.IsHandleCreated) return;
            Theme.RefreshSwitcher(themeButton);
            var buttons = new[] { settingsButton, themeButton, sessionsButton, add };
            int Needed() => Theme.Scaled(this, 63) + catalogTitle.PreferredWidth + buttons.Sum(button => button.Width + button.Margin.Horizontal);
            void Compact(SurfaceButton button, bool compact, int logical)
            {
                string label = button.AccessibleName;
                button.Text = compact ? "" : label;
                button.Width = Theme.Scaled(this, compact ? 38 : logical);
                hints.SetToolTip(button, compact ? label : null);
            }
            Compact(settingsButton, false, 118);
            Compact(themeButton, false, 130);
            if (Needed() > catalogHeader.ClientSize.Width) Compact(settingsButton, true, 118);
            if (Needed() > catalogHeader.ClientSize.Width) Compact(themeButton, true, 130);
        }

        private void ApplyPreferences()
        {
            TrayApplication.CloseToTray = groups.Preferences.CloseToTray;
            if (sessions != null) sessions.Preferences = groups.Preferences;
        }

        private void RecordRecent(string key)
        {
            try { groups = groupFile.Update(items => items.RecordRecent(key)); }
            catch (Exception ex) when (Theme.IsFileError(ex)) { /* Recent servers are a convenience. */ }
        }

        private void ToggleFavorite(string key, string select)
        {
            string tab = activeGroupId == FavoritesId && groups.Favorites.Count == 1 && groups.IsFavorite(key) ? null : activeGroupId;
            UpdateGroups(items => items.ToggleFavorite(key), tab, select);
        }

        private void EditSettings()
        {
            using (var dialog = new SettingsDialog(groups.Preferences))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var chosen = dialog.Result;
                try { groups = groupFile.Update(items => items.Preferences = chosen); }
                catch (Exception ex) when (Theme.IsFileError(ex)) { Theme.ShowError(this, ex); return; }
                ApplyPreferences();
            }
        }

        private void AddSnippet()
        {
            using (var dialog = new SnippetDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var snippet = dialog.Result;
                try { groups = groupFile.Update(items => items.Snippets.Add(snippet)); }
                catch (Exception ex) when (Theme.IsFileError(ex)) { Theme.ShowError(this, ex); }
            }
        }

        private void RemoveSnippet(Snippet snippet)
        {
            if (MessageBox.Show(this, "Удалить команду «" + snippet.Name + "»?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                groups = groupFile.Update(items =>
                {
                    int index = items.Snippets.FindIndex(item => item.Name == snippet.Name && item.Command == snippet.Command);
                    if (index >= 0) items.Snippets.RemoveAt(index);
                });
            }
            catch (Exception ex) when (Theme.IsFileError(ex)) { Theme.ShowError(this, ex); }
        }

        /// <summary>«Быстрое подключение»: connects without a profile, then offers to save one (docs/ui-spec.md §7).</summary>
        private void QuickConnect()
        {
            QuickTarget target;
            try { target = new QuickTarget(quick.Text); }
            catch (ArgumentException error) { Theme.ShowError(this, error); return; }
            if (target.Vnc)
            {
                VncProfile vnc;
                try { vnc = new VncProfile(Guid.NewGuid().ToString("N"), target.Host, target.Host, target.Port, target.User, ActiveTabId); }
                catch (ArgumentException error) { Theme.ShowError(this, error); return; }
                quick.Clear();
                ConnectVnc(vnc, forceNew: true);
                if (!Ask("Сохранить подключение «" + vnc.Name + "»?")) return;
                UpdateGroups(items => items.SaveVnc(vnc, false), activeGroupId, vnc.Id);
                return;
            }
            if (target.Rdp)
            {
                RdpProfile profile;
                try { profile = new RdpProfile(Guid.NewGuid().ToString("N"), target.Host, target.Host, target.Port, target.User, "", ActiveTabId); }
                catch (ArgumentException error) { Theme.ShowError(this, error); return; }
                quick.Clear();
                ConnectDesktop(profile, forceNew: true);
                if (!Ask("Сохранить подключение «" + profile.Name + "»?")) return;
                UpdateGroups(items => items.SaveDesktop(profile, false), activeGroupId, profile.Id);
                return;
            }
            if (document == null) { Theme.ShowError(this, new IOException("Не удалось прочитать SSH config. Нажмите «Обновить».")); return; }
            try
            {
                SessionWorkspace().Open(new SessionTarget(target), true);
                ShowSessions();
            }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is ArgumentException || ex is ExternalException)
            {
                Theme.ShowError(this, ex);
                return;
            }
            quick.Clear();
            string alias = QuickTarget.Alias(target.Host, document.Connections().Select(item => item.Alias));
            if (!Ask("Сохранить подключение «" + alias + "»?")) return;
            try
            {
                var next = new ConfigDocument(document.ToString());
                next.Update(null, new ConnectionValues { Alias = alias, HostName = target.Host, User = target.User,
                    Port = target.Port == 22 ? "" : target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Keys = new List<Identity>() });
                file.Save(next);
                document = next;
                string tab = ActiveTabId;
                if (tab != null) groups = groupFile.Update(items => items.Move(alias, tab));
            }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is ArgumentException || ex is InvalidOperationException) { Theme.ShowError(this, ex); }
            RefreshList(alias);
        }

        private bool Ask(string question) => MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1) == DialogResult.Yes;

        /// <summary>«Импорт из других программ»: SSH entries go to the config, RDP entries to tabs.xml.</summary>
        private void ImportOther()
        {
            List<FoundConnection> chosen;
            using (var dialog = new ImportDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                chosen = dialog.Chosen;
            }
            if (chosen.Count == 0) return;
            int added = 0;
            var problems = new List<string>();
            string tab = ActiveTabId;
            var ssh = chosen.Where(item => !item.Rdp && !item.Vnc).ToList();
            if (ssh.Count > 0 && document != null)
            {
                var next = new ConfigDocument(document.ToString());
                var aliases = new List<string>();
                foreach (var item in ssh)
                {
                    string alias = QuickTarget.Alias(item.Name, next.Connections().Select(connection => connection.Alias));
                    try
                    {
                        next.Update(null, new ConnectionValues { Alias = alias, HostName = item.Host, User = item.User,
                            Port = item.Port == 22 ? "" : item.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), Keys = new List<Identity>() });
                        aliases.Add(alias);
                    }
                    catch (ArgumentException error) { problems.Add(item.Name + ": " + error.Message); }
                }
                try
                {
                    file.Save(next);
                    document = next;
                    added += aliases.Count;
                    if (tab != null && aliases.Count > 0) groups = groupFile.Update(items => { foreach (var alias in aliases) items.Move(alias, tab); });
                }
                catch (Exception ex) when (Theme.IsFileError(ex) || ex is InvalidOperationException) { problems.Add(ex.Message); }
            }
            var desktops = new List<RdpProfile>();
            foreach (var item in chosen.Where(item => item.Rdp))
            {
                try { desktops.Add(new RdpProfile(Guid.NewGuid().ToString("N"), item.Name.Length > 80 ? item.Name.Substring(0, 80) : item.Name,
                    item.Host, item.Port, item.User, item.Domain, tab)); }
                catch (ArgumentException error) { problems.Add(item.Name + ": " + error.Message); }
            }
            var vncProfiles = new List<VncProfile>();
            foreach (var item in chosen.Where(item => item.Vnc))
            {
                try { vncProfiles.Add(new VncProfile(Guid.NewGuid().ToString("N"), item.Name.Length > 80 ? item.Name.Substring(0, 80) : item.Name,
                    item.Host, item.Port, item.User, tab)); }
                catch (ArgumentException error) { problems.Add(item.Name + ": " + error.Message); }
            }
            if (desktops.Count > 0 || vncProfiles.Count > 0)
            {
                try
                {
                    groups = groupFile.Update(items =>
                    {
                        foreach (var profile in desktops) items.SaveDesktop(profile, false);
                        foreach (var profile in vncProfiles) items.SaveVnc(profile, false);
                    });
                    added += desktops.Count + vncProfiles.Count;
                }
                catch (Exception ex) when (Theme.IsFileError(ex) || ex is InvalidOperationException) { problems.Add(ex.Message); }
            }
            RefreshTabs(activeGroupId);
            RefreshList(null);
            string report = "Импортировано подключений: " + added + ".";
            if (problems.Count > 0) report += "\n\nНе импортированы:\n" + String.Join("\n", problems.Take(10));
            MessageBox.Show(this, report, "Импорт из других программ", MessageBoxButtons.OK,
                problems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        /// <summary>«Скопировать ключ на сервер»: appends a public key to ~/.ssh/authorized_keys over SSH.</summary>
        private void CopyKey(Connection connection)
        {
            if (connection == null) return;
            string publicKey = null;
            try
            {
                var keys = Directory.Exists(sshDirectory) ? Directory.GetFiles(sshDirectory, "*.pub") : new string[0];
                if (keys.Length == 1) publicKey = keys[0];
            }
            catch (Exception ex) when (Theme.IsFileError(ex)) { }
            if (publicKey == null)
            {
                using (var dialog = new OpenFileDialog { Title = "Публичный ключ для сервера", Filter = "Публичный ключ OpenSSH (*.pub)|*.pub|Все файлы|*.*" })
                {
                    if (Directory.Exists(sshDirectory)) dialog.InitialDirectory = sshDirectory;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    publicKey = dialog.FileName;
                }
            }
            try
            {
                var command = SshKeys.CopyCommand(publicKey);
                SessionWorkspace().Open(new SessionTarget(connection, groups.ForAlias(connection.Alias)).WithCommand(command), true);
                ShowSessions();
            }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is ArgumentException || ex is ExternalException) { Theme.ShowError(this, ex); }
        }
        private RdpProfile SelectedDesktop => grid.CurrentRow?.Tag as RdpProfile;
        private VncProfile SelectedVnc => grid.CurrentRow?.Tag as VncProfile;
        private string SelectedKey => SelectedDesktop?.Id ?? SelectedVnc?.Id ?? Selected?.Alias;

        private void SelectProtocol(CatalogKind next)
        {
            if (kind == next) return;
            kind = next;
            bool desktop = next != CatalogKind.Ssh;
            sshTab.Style = next == CatalogKind.Ssh ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            rdpTab.Style = next == CatalogKind.Rdp ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            vncTab.Style = next == CatalogKind.Vnc ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            catalogTitle.Text = next == CatalogKind.Rdp ? "RDP-подключения" : next == CatalogKind.Vnc ? "VNC-подключения" : "SSH-подключения";
            grid.Columns[0].HeaderText = desktop ? "Название" : "Псевдоним";
            grid.Columns[TerminalColumn].HeaderText = desktop ? "Рабочий стол" : "Терминал";
            grid.Columns[FilesColumn].Visible = !desktop;
            source.Text = desktop ? settingsPath : Path.Combine(sshDirectory, "config");
            hints.SetToolTip(source, source.Text);
            search.Clear();
            RefreshList(null);
        }

        private void SortColumn(int index)
        {
            string tab = ActiveTabId;
            var current = groups.Sort(tab, kind);
            var ascending = index == 0 ? ServerSort.Name : ServerSort.Address;
            var descending = index == 0 ? ServerSort.NameDescending : ServerSort.AddressDescending;
            var next = current == ascending ? descending : current == descending ? ServerSort.Original : ascending;
            var protocol = kind;
            UpdateGroups(items => items.SetSort(tab, protocol, next), activeGroupId, SelectedKey);
        }

        private async Task CheckUpdates(bool interactive)
        {
            if (!updateButton.Enabled || (!interactive && !groups.Preferences.CheckUpdates)) return;
            updateButton.Enabled = false;
            if (interactive) updateButton.Text = "Проверка…";
            var token = updateLifetime.Token;
            string staged = null;
            AppUpdate downloading = null;
            try
            {
                availableUpdate = await Task.Run(() =>
                {
                    if (!interactive) UpdateWorker.CleanOldStages();
                    return AppUpdates.Check(UpdateWorker.IsInstalled(Application.ExecutablePath), token);
                });
                if (IsDisposed || !interactive) return;
                if (availableUpdate == null)
                {
                    MessageBox.Show(this, "Установлена актуальная версия " + AppUpdates.CurrentVersion.ToString(3) + ".",
                        "Обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var update = availableUpdate;
                using (var dialog = new UpdateDialog(update))
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                downloading = update;
                var progress = new Progress<int>(percent =>
                {
                    if (!IsDisposed && !updateButton.Enabled) updateButton.Text = "Загрузка · " + percent + "%";
                });
                staged = await Task.Run(() => AppUpdates.Download(update, progress, token));
                if (IsDisposed) return;
                if (!backupButton.Enabled || Application.OpenForms.Cast<Form>().Any(window => window.Modal && window.Visible))
                    throw new IOException("Завершите текущее действие перед установкой обновления.");

                bool sessionsOpen = (sessions?.CountOpen() ?? 0) > 0 || fileWindows.Count > 0;
                string message = "Установить версию " + update.Version.ToString(3) + " и перезапустить SSH Manager?";
                if (sessionsOpen) message += "\n\nОткрытые сеансы SSH и окна SFTP будут закрыты.";
                if (MessageBox.Show(this, message, "Установить обновление", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

                UpdateWorker.Schedule(update, staged);
                staged = null;
                Application.Exit();
            }
            catch (OperationCanceledException) { }
            catch (GitHubRequestException ex)
            {
                if (interactive && !IsDisposed)
                {
                    string action = downloading == null ? "Не удалось проверить обновления." : "Не удалось скачать обновление.";
                    string offer = downloading == null ? "Открыть релизы GitHub в браузере?" : "Открыть загрузку в браузере?";
                    if (MessageBox.Show(this, action + "\n\n" + ex.Message + "\n\n" + offer, "Обновления",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        UpdateDialog.OpenLink(this, downloading?.DownloadUrl ?? AppUpdates.LatestRelease);
                }
            }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is InvalidDataException || ex is System.Runtime.Serialization.SerializationException ||
                ex is System.Xml.XmlException || ex is System.ComponentModel.Win32Exception)
            {
                if (interactive && !IsDisposed) Theme.ShowError(this, ex);
            }
            finally
            {
                if (staged != null) UpdateWorker.CleanStage(staged);
                if (!IsDisposed)
                {
                    updateButton.Text = availableUpdate == null ? "Обновления" : "Обновить до " + availableUpdate.Version.ToString(3);
                    updateButton.AccessibleName = updateButton.Text;
                    updateButton.Enabled = true;
                }
            }
        }

        private async Task RunBackup(Func<Task> action)
        {
            if (!backupButton.Enabled) return;
            backupButton.Enabled = false;
            try { await action(); }
            catch (Exception ex) when (BackupCommands.IsBackupError(ex)) { if (!IsDisposed) Theme.ShowError(this, ex); }
            finally { if (!IsDisposed) backupButton.Enabled = true; }
        }

        private void Reload(bool openStartupTab = false)
        {
            var selected = SelectedKey;
            var groupId = activeGroupId;
            try { document = file.Load(); }
            catch (Exception ex) when (Theme.IsFileError(ex))
            {
                document = null;
                Theme.ShowError(this, ex);
            }
            try { groups = groupFile.Load(); }
            catch (Exception ex) when (Theme.IsFileError(ex)) { Theme.ShowError(this, ex); }
            if (Theme.IsDark != groups.DarkTheme) ApplySelectedTheme();
            ApplyPreferences();
            RefreshTabs(openStartupTab ? groups.StartupTabId : groupId);
            RefreshList(selected);
        }

        private void ToggleTheme()
        {
            try { groups = groupFile.Update(settings => settings.DarkTheme = !Theme.IsDark); }
            catch (Exception ex) when (Theme.IsFileError(ex)) { Theme.ShowError(this, ex); return; }
            ApplySelectedTheme();
        }

        private void ApplySelectedTheme()
        {
            Theme.IsDark = groups.DarkTheme;
            Theme.Apply(this);
            Theme.RefreshSwitcher(themeButton);
            FitHeader();
            sessions?.RefreshThemeButton();
            foreach (var window in fileWindows.Values) Theme.Apply(window);
        }

        private void RefreshList(string selectAlias)
        {
            grid.Rows.Clear();
            var order = groups.Sort(ActiveTabId, kind);
            bool favorites = activeGroupId == FavoritesId;
            grid.Columns[0].HeaderCell.SortGlyphDirection = order == ServerSort.Name ? SortOrder.Ascending :
                order == ServerSort.NameDescending ? SortOrder.Descending : SortOrder.None;
            grid.Columns[1].HeaderCell.SortGlyphDirection = order == ServerSort.Address ? SortOrder.Ascending :
                order == ServerSort.AddressDescending ? SortOrder.Descending : SortOrder.None;
            var activeGroup = groups.Find(activeGroupId);
            if (kind == CatalogKind.Rdp) { RefreshDesktops(selectAlias, order, activeGroup); return; }
            if (kind == CatalogKind.Vnc) { RefreshVnc(selectAlias, order, activeGroup); return; }
            var connections = document == null ? new Connection[0] : document.Connections()
                .Where(connection => (activeGroup == null || activeGroup.Aliases.Contains(connection.Alias)) &&
                    (!favorites || groups.IsFavorite(Groups.Key(connection.Alias)))).ToArray();
            string query = search.Text.Trim();
            DataGridViewRow toSelect = null;
            foreach (var connection in ServerOrder.Apply(connections, order, item => item.Alias, item => HostOrAlias(item)))
            {
                var host = connection.Get("hostname");
                var user = connection.Get("user");
                var group = groups.ForAlias(connection.Alias);
                var groupName = group == null ? "" : group.Name;
                if (query.Length > 0 && !new[] { connection.Alias, host, user, groupName }.Any(text => text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                var port = connection.Get("port");
                var row = grid.Rows[grid.Rows.Add(connection.Alias, Dash(host), Dash(user), Dash(port), Dash(groupName),
                    "Файлы", SessionAction(connection.Alias))];
                row.Tag = connection;
                if (String.Equals(selectAlias, connection.Alias, StringComparison.Ordinal)) toSelect = row;
            }
            if (toSelect != null) grid.CurrentCell = toSelect.Cells[0];
            empty.Text = "По вашему запросу ничего не найдено.";
            if (document == null) empty.Text = "Не удалось прочитать SSH config. Нажмите «Обновить», чтобы повторить попытку.";
            else if (connections.Length == 0) empty.Text = favorites ? FavoritesHint : activeGroup == null
                ? "Добавьте сервер, чтобы создать первое подключение."
                : "Добавьте сервер или перенесите сюда подключение из «Все серверы».";
            UpdateListStatus(connections.Length);
        }

        private void RefreshDesktops(string selected, ServerSort order, ServerGroup activeGroup)
        {
            var desktops = groups.Desktops.Where(profile => (activeGroup == null || profile.GroupId == activeGroup.Id) &&
                (activeGroupId != FavoritesId || groups.IsFavorite(Groups.Key(profile)))).ToArray();
            var query = search.Text.Trim();
            foreach (var profile in ServerOrder.Apply(desktops, order, item => item.Name, item => item.Host))
            {
                var groupName = groups.Find(profile.GroupId)?.Name ?? "";
                if (!new[] { profile.Name, profile.Address, profile.Account, groupName }
                    .Any(value => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                var row = grid.Rows[grid.Rows.Add(profile.Name, profile.Host, Dash(profile.Account), profile.Port, Dash(groupName),
                    "", SessionAction(SessionTarget.RdpKey(profile)))];
                row.Tag = profile;
                if (profile.Id == selected) grid.CurrentCell = row.Cells[0];
            }
            empty.Text = desktops.Length > 0 ? "По вашему запросу ничего не найдено." : activeGroupId == FavoritesId ? FavoritesHint :
                "Добавьте RDP-подключение или импортируйте профиль .rdp.";
            UpdateListStatus(desktops.Length);
        }

        private void RefreshVnc(string selected, ServerSort order, ServerGroup activeGroup)
        {
            var profiles = groups.Vnc.Where(profile => (activeGroup == null || profile.GroupId == activeGroup.Id) &&
                (activeGroupId != FavoritesId || groups.IsFavorite(Groups.Key(profile)))).ToArray();
            var query = search.Text.Trim();
            foreach (var profile in ServerOrder.Apply(profiles, order, item => item.Name, item => item.Host))
            {
                var groupName = groups.Find(profile.GroupId)?.Name ?? "";
                if (!new[] { profile.Name, profile.Address, profile.User, groupName, profile.Via }
                    .Any(value => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                var row = grid.Rows[grid.Rows.Add(profile.Name, profile.Host, Dash(profile.User), profile.Port, Dash(groupName),
                    "", SessionAction(profile.Key))];
                row.Tag = profile;
                if (profile.Id == selected) grid.CurrentCell = row.Cells[0];
            }
            empty.Text = profiles.Length > 0 ? "По вашему запросу ничего не найдено." : activeGroupId == FavoritesId ? FavoritesHint :
                "Добавьте VNC-подключение.";
            UpdateListStatus(profiles.Length);
        }

        private void UpdateListStatus(int total)
        {
            empty.Visible = grid.Rows.Count == 0;
            status.Text = grid.Rows.Count == total ? "Подключений: " + total : "Найдено: " + grid.Rows.Count + " из " + total;
            UpdateActions();
        }

        private void UpdateActions()
        {
            add.Enabled = kind != CatalogKind.Ssh || document != null;
            edit.Enabled = delete.Enabled = grid.CurrentRow?.Tag != null;
        }

        private static string HostOrAlias(Connection connection)
        {
            string host = connection.Get("hostname");
            return host.Length == 0 ? connection.Alias : host;
        }

        private void EditRow(object entry)
        {
            if (entry is Connection connection) EditConnection(connection);
            else if (entry is RdpProfile profile) EditDesktop(profile);
            else if (entry is VncProfile vnc) EditVnc(vnc);
        }

        /// <summary>«Новое VNC-подключение» / «Изменить VNC-подключение»; the password lives in Credential Manager.</summary>
        private void EditVnc(VncProfile profile)
        {
            string selected = null;
            bool hasPassword = false;
            try { hasPassword = profile != null && VncPasswords.Exists(profile); }
            catch (IOException error) { Theme.ShowError(this, error); }
            var aliases = document == null ? new string[0] : document.Connections().Select(item => item.Alias).ToArray();
            using (var dialog = new VncDialog(profile, ActiveTabId, hasPassword, aliases, (replacement, password, forgetPassword) =>
            {
                groups = groupFile.Update(items => items.SaveVnc(replacement, profile != null));
                if (forgetPassword) VncPasswords.Set(replacement, null);
                else if (password.Length > 0) VncPasswords.Set(replacement, password);
                selected = replacement.Id;
            }))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || selected == null) return;
            }
            search.Clear();
            RefreshList(selected);
        }

        private void DeleteVnc(VncProfile profile)
        {
            if (profile == null || MessageBox.Show(this, "Удалить VNC-подключение «" + profile.Name + "»?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            UpdateGroups(items =>
            {
                items.Vnc.RemoveAll(item => item.Id == profile.Id);
                items.RenameKey(Groups.Key(profile), null);
            }, activeGroupId);
            if (groups.Vnc.Any(item => item.Id == profile.Id)) return;
            try { VncPasswords.Set(profile, null); }
            catch (IOException error) { Theme.ShowError(this, error); }
        }

        private void ConnectVnc(VncProfile profile, bool forceNew = false)
        {
            if (groups.Vnc.Any(item => item.Id == profile.Id)) RecordRecent(Groups.Key(profile));
            try
            {
                var workspace = SessionWorkspace();
                ShowSessions();
                workspace.Open(new SessionTarget(profile, groups.Find(profile.GroupId)), forceNew);
            }
            catch (Exception error) when (Theme.IsFileError(error) || error is InvalidOperationException || error is ArgumentException)
            {
                Theme.ShowError(this, error);
            }
        }

        private void ShowVncMenu(VncProfile profile, Point point)
        {
            desktopMenu?.Dispose();
            desktopMenu = Theme.Menu();
            var move = Theme.MenuItem("Переместить во вкладку", Glyph.Move);
            AddDestination(Groups.UngroupedName, null);
            foreach (var group in groups.Items) AddDestination(group.Name, group.Id);
            desktopMenu.Items.AddRange(new ToolStripItem[]
            {
                Theme.MenuItem("Подключиться", Glyph.Server, (sender, args) => ConnectVnc(profile)),
                Theme.MenuItem("Новый сеанс", Glyph.Plus, (sender, args) => ConnectVnc(profile, true)),
                Theme.MenuItem("Открыть в отдельном окне", Glyph.Move, (sender, args) => VncWindow.Open(profile, openFiles: password => OpenVncFiles(profile, password))),
                Theme.MenuItem("Файлы", Glyph.Folder, (sender, args) => OpenVncFiles(profile)),
                Theme.MenuItem("Изменить", Glyph.Edit, (sender, args) => EditVnc(profile)),
                Theme.MenuItem(groups.IsFavorite(Groups.Key(profile)) ? "Убрать из избранного" : "В избранное", Glyph.Star,
                    (sender, args) => ToggleFavorite(Groups.Key(profile), profile.Id)),
                move,
                new ToolStripSeparator(),
                Theme.MenuItem("Удалить", Glyph.Delete, (sender, args) => DeleteVnc(profile))
            });
            desktopMenu.Show(grid, point);

            void AddDestination(string name, string id)
            {
                var item = Theme.MenuItem(SafeMenuText(name), Glyph.Folder, (sender, args) =>
                    UpdateGroups(items => items.MoveVnc(profile.Id, id), activeGroupId, profile.Id));
                item.Checked = profile.GroupId == id;
                move.DropDownItems.Add(item);
            }
        }

        private void EditDesktop(RdpProfile profile)
        {
            string selected = null;
            bool import, hasPassword = false;
            try { hasPassword = profile != null && RdpPasswords.Exists(profile); }
            catch (IOException error) { Theme.ShowError(this, error); }
            using (var dialog = new RdpDialog(profile, ActiveTabId, hasPassword, (replacement, password, forgetPassword) =>
            {
                bool changePassword = forgetPassword || password.Length > 0 ||
                    (hasPassword && profile != null && profile.Account != replacement.Account);
                byte[] previous = changePassword ? RdpPasswords.ReadBytes(replacement.Id) : null;
                try
                {
                    if (forgetPassword) RdpPasswords.Set(replacement, null);
                    else if (password.Length > 0) RdpPasswords.Set(replacement, password);
                    else if (changePassword && previous != null) RdpPasswords.Set(replacement.Id, replacement.Account, previous);
                    try { groups = groupFile.Update(items => items.SaveDesktop(replacement, profile != null)); }
                    catch
                    {
                        if (changePassword)
                        {
                            try { RdpPasswords.Set(replacement.Id, profile?.Account ?? replacement.Account, previous); }
                            catch (IOException) { }
                        }
                        throw;
                    }
                }
                finally { SshPasswords.Clear(previous); }
                selected = replacement.Id;
            }))
            {
                dialog.ShowDialog(this);
                import = dialog.ImportRequested;
            }
            if (import) { TransferDesktop(null); return; }
            if (selected == null) return;
            search.Clear();
            RefreshList(selected);
        }

        private void DeleteDesktop(RdpProfile profile)
        {
            if (profile == null || MessageBox.Show(this, "Удалить RDP-подключение «" + profile.Name + "»?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            UpdateGroups(items =>
            {
                items.Desktops.RemoveAll(item => item.Id == profile.Id);
                items.RenameKey(Groups.Key(profile), null);
            }, activeGroupId);
            if (groups.Desktops.Any(item => item.Id == profile.Id)) return;
            try { RdpPasswords.Set(profile, null); }
            catch (IOException error) { Theme.ShowError(this, error); }
        }

        private void TransferDesktop(RdpProfile profile)
        {
            bool import = profile == null;
            using (FileDialog dialog = import ? (FileDialog)new OpenFileDialog() : new SaveFileDialog { FileName = "connection.rdp" })
            {
                dialog.Filter = "Профиль удалённого рабочего стола (*.rdp)|*.rdp";
                dialog.DefaultExt = "rdp";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (!import) profile.Export(dialog.FileName);
                    else
                    {
                        var imported = RdpProfile.Import(dialog.FileName, ActiveTabId);
                        groups = groupFile.Update(items => items.SaveDesktop(imported, false));
                        search.Clear();
                        RefreshList(imported.Id);
                    }
                }
                catch (Exception error) when (Theme.IsFileError(error) || error is ArgumentException || error is InvalidOperationException)
                { Theme.ShowError(this, error); }
            }
        }

        private void EditConnection(Connection connection)
        {
            Exception tabError = null;
            var previousPassword = PasswordTarget.From(connection);
            bool hasPassword;
            try { hasPassword = SshPasswords.Exists(previousPassword); }
            catch (IOException ex) { Theme.ShowError(this, ex); return; }
            using (var dialog = new ConnectionDialog(sshDirectory, connection, hasPassword, (values, password, forgetPassword) =>
            {
                var next = new ConfigDocument(document.ToString());
                next.Update(FindCopy(next, connection), values);
                var target = PasswordTarget.From(values.Alias, values.HostName, values.User, values.Port);
                if (password.Length > 0 && target == null)
                    throw new ArgumentException("Для входа по сохранённому паролю укажите SSH-пользователя и обычный DNS-адрес или IP без подстановок.");
                bool moved = previousPassword != null && (target == null || target.Id != previousPassword.Id);
                using (var changes = new PasswordUpdates())
                {
                    byte[] replacement = null;
                    try
                    {
                        if (password.Length > 0) replacement = SshPasswords.Encode(password);
                        else if (moved && hasPassword && !forgetPassword && previousPassword.SameDestination(target))
                            replacement = SshPasswords.Read(previousPassword);
                        if (replacement != null) changes.Add(target, replacement);
                        else if (forgetPassword && !moved) changes.Add(target, null);
                        if (moved && hasPassword && !ContainsPasswordTarget(next, previousPassword))
                            changes.Add(previousPassword, null);
                        file.Save(next, changes);
                    }
                    finally { SshPasswords.Clear(replacement); }
                }
                document = next;
                try
                {
                    if (connection != null && !String.Equals(connection.Alias, values.Alias, StringComparison.OrdinalIgnoreCase))
                        groups = groupFile.Update(items => items.RenameKey(Groups.Key(connection.Alias), Groups.Key(values.Alias)));
                    string groupId = ActiveTabId;
                    if (connection == null && groupId != null)
                        groups = groupFile.Update(items => items.Move(values.Alias, groupId));
                    else if (connection != null && connection.Alias != values.Alias && groups.ForAlias(connection.Alias) != null)
                        groups = groupFile.Update(items =>
                        {
                            var previous = items.ForAlias(connection.Alias);
                            if (previous == null) return;
                            items.Move(values.Alias, previous.Id);
                            if (!next.Connections().Any(item => String.Equals(item.Alias, connection.Alias, StringComparison.OrdinalIgnoreCase)))
                                previous.Aliases.Remove(connection.Alias);
                        });
                }
                catch (Exception ex) when (Theme.IsFileError(ex) || ex is InvalidOperationException) { tabError = ex; }
            }, groups.Preferences))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                search.Clear();
                RefreshTabs(activeGroupId);
                RefreshList(dialog.Alias);
                if (tabError != null) Theme.ShowError(this, new IOException("Подключение сохранено. Не удалось обновить вкладки.\n" + tabError.Message));
            }
        }

        private void DeleteConnection(Connection connection)
        {
            if (connection == null || MessageBox.Show(this, "Удалить подключение «" + connection.Alias + "» из SSH config?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                var next = new ConfigDocument(document.ToString());
                next.Delete(FindCopy(next, connection));
                using (var changes = new PasswordUpdates())
                {
                    var target = PasswordTarget.From(connection);
                    if (target != null && !ContainsPasswordTarget(next, target)) changes.Add(target, null);
                    file.Save(next, changes);
                }
                document = next;
            }
            catch (Exception ex) when (Theme.IsFileError(ex)) { Theme.ShowError(this, ex); return; }
            bool gone = !document.Connections().Any(item => String.Equals(item.Alias, connection.Alias, StringComparison.OrdinalIgnoreCase));
            if (gone && (groups.ForAlias(connection.Alias) != null || groups.IsFavorite(Groups.Key(connection.Alias)) ||
                groups.Recent.Any(entry => String.Equals(entry.Key, Groups.Key(connection.Alias), StringComparison.OrdinalIgnoreCase))))
                UpdateGroups(items =>
                {
                    items.Move(connection.Alias, null);
                    items.RenameKey(Groups.Key(connection.Alias), null);
                }, activeGroupId);
            else RefreshList(null);
        }

        private static bool ContainsPasswordTarget(ConfigDocument configuration, PasswordTarget target)
        {
            return configuration.Connections().Any(connection => PasswordTarget.From(connection)?.Id == target.Id);
        }

        private void RefreshTabs(string selected)
        {
            tabs.SuspendLayout();
            try
            {
                var previous = tabs.Controls.Cast<Control>().ToArray();
                tabs.Controls.Clear();
                foreach (var page in previous) page.Dispose();
                if (groups.Favorites.Count > 0) AddGroupTab("Избранное", FavoritesId, glyph: Glyph.Star);
                AddGroupTab(Groups.AllName, null);
                foreach (var group in groups.Items) AddGroupTab(group.Name, group.Id, TabColor.Find(group.ColorId));
            }
            finally { tabs.ResumeLayout(); }
            SelectGroup(selected == FavoritesId && groups.Favorites.Count > 0 ? FavoritesId : groups.Find(selected) == null ? null : selected);
        }

        private void AddGroupTab(string title, string id, TabColor color = null, Glyph glyph = Glyph.None)
        {
            int padding = color == null && glyph == Glyph.None ? 22 : 39;
            int desiredWidth = TextRenderer.MeasureText(title, Theme.Emphasis).Width + padding * DeviceDpi / 96;
            var tab = Theme.Button(title, Math.Min(250 * DeviceDpi / 96, desiredWidth));
            tab.Height = 30 * DeviceDpi / 96;
            tab.Margin = new Padding(0, 2, 5, 0);
            tab.Tag = id;
            tab.Symbol = glyph;
            tab.AccessibleRole = AccessibleRole.PageTab;
            if (color != null)
            {
                tab.Marker = color.Swatch;
                tab.AccessibleDescription = "Цветовая метка — " + color.Name.ToLowerInvariant();
            }
            tab.Click += (sender, args) => { SelectGroup(id); RefreshList(null); };
            tab.MouseUp += (sender, args) =>
            {
                if (args.Button == MouseButtons.Right) ShowTabMenu(tab, id, args.Location);
            };
            tab.KeyDown += (sender, args) =>
            {
                if (!IsMenuKey(args)) return;
                args.Handled = true;
                ShowTabMenu(tab, id, new Point(0, tab.Height));
            };
            tabs.AddTab(tab, id);
        }

        private void SelectGroup(string id)
        {
            activeGroupId = id;
            foreach (SurfaceButton tab in tabs.Controls)
            {
                bool selected = (string)tab.Tag == id;
                tab.Style = selected ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
                if (selected) tabs.ScrollControlIntoView(tab);
            }
            tabs.Invalidate(true);
        }

        private void ShowTabMenu(Control tab, string id, Point location)
        {
            if (id == FavoritesId) return;
            string alias = SelectedKey;
            SelectGroup(id);
            RefreshList(alias);
            var group = groups.Find(id);
            foreach (ToolStripItem item in tabMenu.Items)
                item.Available = item == startupTab || group != null;
            startupTab.Checked = groups.StartupTabId == id;
            if (group != null)
            {
                int index = groups.Items.IndexOf(group);
                moveTabLeft.Enabled = index > 0;
                moveTabRight.Enabled = index < groups.Items.Count - 1;
            }
            var color = TabColor.Find(group?.ColorId);
            foreach (ToolStripMenuItem item in tabColorMenu.DropDownItems)
                item.Checked = (string)item.Tag == (color == null ? null : color.Id);
            tabMenu.Show(tab, location);
        }

        private void ShiftActiveTab(int offset)
        {
            string id = activeGroupId;
            UpdateGroups(items => items.ShiftTab(id, offset), id, SelectedKey);
        }

        private void AddColorMenuItem(TabColor color)
        {
            string colorId = color == null ? null : color.Id;
            var swatch = color == null ? null : Theme.Icon(Glyph.Dot, color.Swatch);
            var item = new ToolStripMenuItem(color == null ? "Без метки" : color.Name, swatch) { Tag = colorId };
            item.Click += (sender, args) =>
            {
                string groupId = activeGroupId;
                UpdateGroups(items => items.Mark(groupId, colorId), groupId);
            };
            tabColorMenu.DropDownItems.Add(item);
        }

        private void EditGroup(string id)
        {
            var existing = groups.Find(id);
            var groupId = existing == null ? Guid.NewGuid().ToString("N") : existing.Id;
            using (var dialog = new GroupDialog(existing == null ? null : existing.Name, name =>
                groups = groupFile.Update(items =>
                {
                    if (existing != null && items.Find(groupId) == null) throw new InvalidOperationException("Эта вкладка уже удалена. Обновите список.");
                    items.NameGroup(groupId, name);
                })))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                search.Clear();
                RefreshTabs(groupId);
                RefreshList(null);
            }
        }

        private void DeleteGroup()
        {
            var group = groups.Find(activeGroupId);
            if (group == null || MessageBox.Show(this, "Удалить вкладку «" + group.Name + "»? Её подключения будут доступны во вкладке «Все серверы».",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            UpdateGroups(items => items.RemoveTab(group.Id), null);
        }

        private void ShowServerMenu(int index, Point point)
        {
            grid.CurrentCell = grid.Rows[index].Cells[0];
            if (SelectedDesktop is RdpProfile desktop) { ShowDesktopMenu(desktop, point); return; }
            if (SelectedVnc is VncProfile vnc) { ShowVncMenu(vnc, point); return; }
            menuConnection = Selected;
            if (menuConnection == null) return;
            var connection = menuConnection;
            favoriteItem.Text = groups.IsFavorite(Groups.Key(connection.Alias)) ? "Убрать из избранного" : "В избранное";
            foreach (ToolStripItem item in moveMenu.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
            var assigned = groups.ForAlias(connection.Alias);
            string current = assigned == null ? null : assigned.Id;
            AddMoveItem(connection, Groups.UngroupedName, null, current);
            foreach (var group in groups.Items) AddMoveItem(connection, group.Name, group.Id, current);
            moveMenu.DropDownItems.Add(new ToolStripSeparator());
            moveMenu.DropDownItems.Add(Theme.MenuItem("Новая вкладка…", Glyph.Plus, (sender, args) =>
            {
                string id = Guid.NewGuid().ToString("N");
                using (var dialog = new GroupDialog(null, name => groups = groupFile.Update(items =>
                {
                    items.NameGroup(id, name);
                    items.Move(connection.Alias, id);
                })))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    search.Clear();
                    RefreshTabs(id);
                    RefreshList(connection.Alias);
                }
            }));
            serverMenu.Show(grid, point);
        }

        private void ShowDesktopMenu(RdpProfile profile, Point point)
        {
            desktopMenu?.Dispose();
            desktopMenu = Theme.Menu();
            var move = Theme.MenuItem("Переместить во вкладку", Glyph.Move);
            AddDestination(Groups.UngroupedName, null);
            foreach (var group in groups.Items) AddDestination(group.Name, group.Id);
            desktopMenu.Items.AddRange(new ToolStripItem[]
            {
                Theme.MenuItem("Подключиться", Glyph.Server, (sender, args) => ConnectDesktop(profile)),
                Theme.MenuItem("Новый сеанс", Glyph.Plus, (sender, args) => ConnectDesktop(profile, true)),
                Theme.MenuItem("Открыть в отдельном окне", Glyph.Move, (sender, args) => ConnectDesktopWindow(profile)),
                Theme.MenuItem("Открыть в клиенте Windows", Glyph.Server, (sender, args) =>
                {
                    try { profile.OpenExternal(); }
                    catch (Exception error) when (Theme.IsFileError(error) || error is System.ComponentModel.Win32Exception)
                    { Theme.ShowError(this, error); }
                }),
                Theme.MenuItem("Изменить", Glyph.Edit, (sender, args) => EditDesktop(profile)),
                Theme.MenuItem(groups.IsFavorite(Groups.Key(profile)) ? "Убрать из избранного" : "В избранное", Glyph.Star,
                    (sender, args) => ToggleFavorite(Groups.Key(profile), profile.Id)),
                move,
                Theme.MenuItem("Экспорт .rdp…", Glyph.Copy, (sender, args) => TransferDesktop(profile)),
                new ToolStripSeparator(),
                Theme.MenuItem("Удалить", Glyph.Delete, (sender, args) => DeleteDesktop(profile))
            });
            desktopMenu.Show(grid, point);

            void AddDestination(string name, string id)
            {
                var item = Theme.MenuItem(SafeMenuText(name), Glyph.Folder, (sender, args) =>
                    UpdateGroups(items => items.MoveDesktop(profile.Id, id), activeGroupId, profile.Id));
                item.Checked = profile.GroupId == id;
                move.DropDownItems.Add(item);
            }
        }

        private void AddMoveItem(Connection connection, string name, string groupId, string current)
        {
            var item = Theme.MenuItem(SafeMenuText(name), Glyph.Folder, (sender, args) =>
                UpdateGroups(items => items.Move(connection.Alias, groupId), activeGroupId, connection.Alias));
            item.Checked = groupId == current;
            moveMenu.DropDownItems.Add(item);
        }

        private void UpdateGroups(Action<Groups> change, string selected, string alias = null)
        {
            try
            {
                groups = groupFile.Update(change);
                RefreshTabs(selected);
            }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is InvalidOperationException) { Theme.ShowError(this, ex); }
            RefreshList(alias);
        }

        private static string SafeMenuText(string name) { return name.Replace("&", "&&"); }

        private static bool IsMenuKey(KeyEventArgs args)
        {
            return args.KeyCode == Keys.Apps || (args.Shift && args.KeyCode == Keys.F10);
        }

        private void CopyCommand(Connection connection)
        {
            try { Clipboard.SetText("ssh " + connection.Alias); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Theme.ShowError(this, new IOException("Буфер обмена занят. Повторите попытку."));
            }
        }

        private static Connection FindCopy(ConfigDocument document, Connection connection)
        {
            return connection == null ? null : document.Connections().First(item => item.Index == connection.Index);
        }

        private void Connect(Connection connection, bool forceNew = false)
        {
            try
            {
                SessionWorkspace().Open(new SessionTarget(connection, groups.ForAlias(connection.Alias)), forceNew);
                ShowSessions();
                RecordRecent(Groups.Key(connection.Alias));
            }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is ArgumentException || ex is System.Runtime.InteropServices.ExternalException)
            {
                Theme.ShowError(this, ex);
            }
        }

        private void ConnectExternal(Connection connection)
        {
            try { Terminal.Connect(connection); }
            catch (IOException ex) { Theme.ShowError(this, ex); }
        }

        private void ConnectDesktop(RdpProfile profile, bool forceNew = false)
        {
            if (groups.Desktops.Any(item => item.Id == profile.Id)) RecordRecent(Groups.Key(profile));
            if (profile.Options.Multimon)
            {
                // The embedded client draws into one window; all monitors need the Windows client.
                try { profile.OpenExternal(); }
                catch (Exception error) when (Theme.IsFileError(error) || error is System.ComponentModel.Win32Exception) { Theme.ShowError(this, error); }
                return;
            }
            bool fromCatalog = catalog.Visible;
            try
            {
                var workspace = SessionWorkspace();
                ShowSessions();
                if (!workspace.Open(new SessionTarget(profile, groups.Find(profile.GroupId)), forceNew) && fromCatalog) ShowServers();
            }
            catch (Exception error) when (error is ExternalException || error is TargetInvocationException ||
                error is InvalidOperationException || error is ArgumentException)
            {
                if (fromCatalog) ShowServers();
                Theme.ShowError(this, new IOException("Не удалось открыть встроенный RDP-сеанс. Попробуйте «Открыть в клиенте Windows» в меню подключения.\n" +
                    (error.InnerException ?? error).Message, error));
            }
        }

        private void ConnectDesktopWindow(RdpProfile profile)
        {
            try { RdpWindow.Open(profile, owner: this); }
            catch (Exception error) when (error is ExternalException || error is TargetInvocationException ||
                error is InvalidOperationException || error is ArgumentException)
            {
                Theme.ShowError(this, new IOException("Не удалось открыть RDP-окно.\n" + (error.InnerException ?? error).Message, error));
            }
        }

        private bool RunGridAction(DataGridViewColumn column, object entry)
        {
            if (column.Name == FilesColumn && entry is Connection fileSource) OpenFiles(fileSource);
            else if (column.Name == TerminalColumn && entry is Connection connection) Connect(connection);
            else if (column.Name == TerminalColumn && entry is RdpProfile profile) ConnectDesktop(profile);
            else if (column.Name == TerminalColumn && entry is VncProfile vnc) ConnectVnc(vnc);
            else return false;
            return true;
        }

        private void OpenFiles(Connection connection)
        {
            if (connection != null) OpenFiles(new SessionTarget(connection, groups.ForAlias(connection.Alias)));
        }

        private void OpenVncFiles(VncProfile profile, string password = null)
        {
            var latest = groups.Vnc.FirstOrDefault(item => item.Id == profile.Id) ?? profile;
            var source = document?.Connections().FirstOrDefault(item =>
                String.Equals(item.Alias, latest.FilesVia, StringComparison.OrdinalIgnoreCase));
            Func<IRemoteFiles> fallback = null;
            if (source != null)
            {
                var ssh = new SessionTarget(source, groups.ForAlias(source.Alias));
                fallback = () => ConnectFileClient(ssh);
            }

            var target = new SessionTarget(latest, groups.Find(latest.GroupId));
            try
            {
                ShowFileWindow(target.Key, target, () => new TightVncFiles(latest, password ?? VncPasswords.Read(latest)), fallback);
            }
            catch (IOException error) { Theme.ShowError(this, error); }
        }

        private void OpenFiles(SessionTarget target)
        {
            ShowFileWindow(target.Alias + "\n" + target.Address, target, () => ConnectFileClient(target));
        }

        private void ShowFileWindow(string id, SessionTarget target, Func<IRemoteFiles> connect, Func<IRemoteFiles> fallback = null)
        {
            if (fileWindows.TryGetValue(id, out var open))
            {
                if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
                open.Show();
                open.Activate();
                return;
            }
            var window = new SftpWindow(target, connect, fallback);
            fileWindows.Add(id, window);
            window.FormClosed += (sender, args) => fileWindows.Remove(id);
            window.Show(this);
        }

        private SftpClient ConnectFileClient(SessionTarget target)
        {
            string config = Path.Combine(sshDirectory, "config");
            var entry = new ConfigFile(config).Load().Connections().FirstOrDefault(item =>
                String.Equals(item.Alias, target.Alias, StringComparison.OrdinalIgnoreCase) &&
                new SessionTarget(item, null).Address == target.Address);
            if (entry == null) throw new IOException("Профиль сервера изменился. Откройте «Файлы» из каталога подключений.");
            var configOverride = String.Equals(sshDirectory, Terminal.SshDirectory, StringComparison.OrdinalIgnoreCase) ? null : config;
            return new SftpClient(target.Alias, PasswordTarget.From(entry), configOverride);
        }

        private SessionsPage SessionWorkspace()
        {
            if (sessions != null) return sessions;
            sessions = new SessionsPage { Visible = false };
            Theme.ScaleToDpi(sessions);
            sessions.Preferences = groups.Preferences;
            sessions.Snippets = () => groups.Snippets;
            sessions.AddSnippet += AddSnippet;
            sessions.RemoveSnippet += RemoveSnippet;
            sessions.ThemeRequested += ToggleTheme;
            sessions.ShowFiles += OpenFiles;
            sessions.ShowVncFiles += OpenVncFiles;
            sessions.LoadServers += picker =>
                picker.UpdateServers(document == null ? new Connection[0] : document.Connections().ToArray(), groups);
            sessions.OpenServer += target =>
            {
                if (target.Vnc != null) ConnectVnc(target.Vnc);
                else if (target.Desktop != null) ConnectDesktop(target.Desktop);
                else if (document != null)
                {
                    var connection = document.Connections().FirstOrDefault(item =>
                        String.Equals(item.Alias, target.Alias, StringComparison.OrdinalIgnoreCase));
                    if (connection != null) Connect(connection);
                }
            };
            sessions.PickServer += () =>
            {
                var connections = document == null ? new Connection[0] : document.Connections().ToArray();
                using (var picker = new ServerPickerDialog(connections, groups, kind))
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    if (picker.SelectedVnc != null) ConnectVnc(picker.SelectedVnc, forceNew: true);
                    else if (picker.SelectedProfile != null) ConnectDesktop(picker.SelectedProfile, forceNew: true);
                    else Connect(picker.SelectedConnection, forceNew: true);
                }
            };
            sessions.ShowServers += ShowServers;
            sessions.Changed += () =>
            {
                int open = sessions.CountOpen();
                sessionsButton.Text = open == 0 ? "Сеансы" : "Сеансы · " + open;
                sessionsButton.AccessibleName = sessionsButton.Text;
                foreach (DataGridViewRow row in grid.Rows)
                {
                    if (row.Tag is Connection connection) row.Cells[TerminalColumn].Value = SessionAction(connection.Alias);
                    else if (row.Tag is RdpProfile profile) row.Cells[TerminalColumn].Value = SessionAction(SessionTarget.RdpKey(profile));
                    else if (row.Tag is VncProfile vnc) row.Cells[TerminalColumn].Value = SessionAction(vnc.Key);
                }
            };
            pages.Controls.Add(sessions);
            return sessions;
        }

        private void ShowSessions()
        {
            var workspace = SessionWorkspace();
            catalog.Hide();
            source.Hide();
            status.Hide();
            workspace.Show();
            workspace.BringToFront();
            workspace.FocusTerminal();
        }

        private void ShowServers()
        {
            sessions.Hide();
            catalog.Show();
            source.Show();
            status.Show();
            grid.Focus();
        }

        private string SessionAction(string key)
        {
            int count = sessions == null ? 0 : sessions.CountOpen(key);
            return count == 0 ? "Подключиться" : "К сеансу · " + count;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (catalog.Visible)
            {
                if (keyData == (Keys.Control | Keys.F)) { search.Focus(); search.SelectAll(); return true; }
                if (keyData == (Keys.Control | Keys.N)) { add.PerformClick(); return true; }
                if (keyData == (Keys.Control | Keys.L)) { quick.Focus(); quick.SelectAll(); return true; }
                if (keyData == Keys.F5) { Reload(); return true; }
            }
            if (sessions != null && sessions.Visible)
            {
                if (keyData == (Keys.Shift | Keys.Escape)) { ShowServers(); return true; }
                if (keyData == (Keys.Control | Keys.Tab)) { sessions.Cycle(); return true; }
                if (keyData == (Keys.Control | Keys.Shift | Keys.P)) { sessions.FocusSearch(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static string Dash(string value) { return value.Length == 0 ? "—" : value; }

        private static DataGridView MakeGrid(int dpi)
        {
            var list = new DataGridView
            {
                AccessibleName = "Список SSH-подключений", Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                BackgroundColor = Theme.Surface, GridColor = Theme.Line, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
                MultiSelect = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, EnableHeadersVisualStyles = false,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = GridPixels(37, dpi), RowTemplate = { Height = GridPixels(45, dpi) },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Canvas, ForeColor = Theme.Muted, Padding = new Padding(8, 0, 4, 0) },
                DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(8, 0, 4, 0) },
                RowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Ink,
                    SelectionBackColor = Theme.Selection, SelectionForeColor = Theme.Ink }
            };
            list.Columns.Add(MakeColumn("Псевдоним", 36, GridPixels(140, dpi)));
            list.Columns.Add(MakeColumn("Адрес", 30, GridPixels(100, dpi)));
            for (int column = 0; column <= 1; column++)
            {
                list.Columns[column].SortMode = DataGridViewColumnSortMode.Programmatic;
                list.Columns[column].HeaderCell.ToolTipText = "Нажмите для сортировки";
            }
            list.Columns.Add(MakeColumn("Пользователь", 21, GridPixels(85, dpi)));
            list.Columns.Add(new DataGridViewTextBoxColumn { Name = PortColumn, HeaderText = "Порт", Width = 64, SortMode = DataGridViewColumnSortMode.NotSortable });
            list.Columns.Add(MakeColumn("Вкладка", 20, GridPixels(65, dpi)));
            list.Columns.Add(new DataGridViewButtonColumn
            {
                Name = FilesColumn, HeaderText = "SFTP", Width = 100, FlatStyle = FlatStyle.Flat, Resizable = DataGridViewTriState.False
            });
            list.Columns.Add(new DataGridViewButtonColumn
            {
                Name = TerminalColumn, HeaderText = "Терминал",
                Width = 159, FlatStyle = FlatStyle.Flat, Resizable = DataGridViewTriState.False
            });
            list.HandleCreated += (sender, args) => FitGridColumns(list, list.DeviceDpi);
            list.FontChanged += (sender, args) => FitGridColumns(list, list.DeviceDpi);
            list.DpiChangedAfterParent += (sender, args) => FitGridColumns(list, list.DeviceDpi);
            return list;
        }

        private static int GridPixels(int logical, int dpi) => Math.Max(1, (logical * dpi + 48) / 96);

        private static void FitGridColumns(DataGridView list, int dpi)
        {
            if (!list.IsHandleCreated || list.IsDisposed) return;
            using (var graphics = list.CreateGraphics())
            {
                var unlimited = new Size(Int32.MaxValue, Int32.MaxValue);
                int portText = TextRenderer.MeasureText(graphics, "65535", list.Font, unlimited, TextFormatFlags.SingleLine).Width;
                int portWidth = Math.Max(GridPixels(64, dpi), portText + list.DefaultCellStyle.Padding.Horizontal + GridPixels(4, dpi));
                var port = list.Columns[PortColumn];
                bool standardWidth = port.Width <= port.MinimumWidth;
                port.MinimumWidth = portWidth;
                if (standardWidth) port.Width = portWidth;

                int padding = GridPixels(ActionIcon, dpi) + GridPixels(ActionGap, dpi) +
                    2 * (GridPixels(ActionOuterPadding, dpi) + GridPixels(ActionInnerPadding, dpi));
                int WidthFor(string text, int minimum) => Math.Max(GridPixels(minimum, dpi),
                    TextRenderer.MeasureText(graphics, text, Theme.Emphasis, unlimited, ActionText).Width + padding + GridPixels(2, dpi));
                list.Columns[FilesColumn].Width = WidthFor("Файлы", 100);
                list.Columns[TerminalColumn].Width = Math.Max(WidthFor("Подключиться", 159), WidthFor("К сеансу · 999", 159));
            }
        }

        private static DataGridViewTextBoxColumn MakeColumn(string label, float weight, int minimum)
        {
            return new DataGridViewTextBoxColumn { HeaderText = label, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable };
        }
        private void PaintGridCell(object sender, DataGridViewCellPaintingEventArgs args)
        {
            if (args.RowIndex < 0 || args.ColumnIndex < 0) return;
            var column = grid.Columns[args.ColumnIndex];
            if (args.ColumnIndex != 0 && !(column is DataGridViewButtonColumn)) return;
            args.Paint(args.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border);
            var box = args.CellBounds;
            var graphics = args.Graphics;
            var previous = graphics.Save();
            graphics.SetClip(box, CombineMode.Intersect);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int scale = DeviceDpi;
            int iconSize = GridPixels(ActionIcon, scale);
            int inset = GridPixels(9, scale);
            string content = Convert.ToString(args.FormattedValue);
            if (args.ColumnIndex == 0)
            {
                var tag = grid.Rows[args.RowIndex].Tag;
                bool favorite = tag is Connection item ? groups.IsFavorite(Groups.Key(item.Alias)) :
                    tag is RdpProfile desktop ? groups.IsFavorite(Groups.Key(desktop)) : tag is VncProfile vnc && groups.IsFavorite(Groups.Key(vnc));
                var icon = favorite ? Theme.Icon(Glyph.Star, TabColor.Find("amber").Swatch) : Theme.Icon(Glyph.Server, Theme.Teal);
                graphics.DrawImage(icon, new Rectangle(box.Left + inset, box.Top + (box.Height - iconSize) / 2, iconSize, iconSize));
                var text = new Rectangle(box.Left + iconSize + inset * 2, box.Top, box.Width - iconSize - inset * 3, box.Height);
                TextRenderer.DrawText(graphics, content, Theme.Emphasis, text, Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                int outside = GridPixels(ActionOuterPadding, scale), inside = GridPixels(ActionInnerPadding, scale),
                    gap = GridPixels(ActionGap, scale);
                var pill = Rectangle.Inflate(box, -outside, -(box.Height / 6));
                if (pill.Width > 2 && pill.Height > 2)
                {
                    int radius = Math.Min(GridPixels(7, scale), Math.Min(pill.Width, pill.Height) / 2);
                    using (var path = Theme.Round(pill, radius))
                    using (var fill = new SolidBrush(Theme.Mint)) graphics.FillPath(fill, path);
                    graphics.DrawImage(Theme.Icon(column.Name == FilesColumn ? Glyph.Folder : Glyph.Terminal, Theme.Teal),
                        new Rectangle(pill.Left + inside, pill.Top + (pill.Height - iconSize) / 2, iconSize, iconSize));
                    var text = new Rectangle(pill.Left + inside + iconSize + gap, pill.Top,
                        Math.Max(0, pill.Width - iconSize - gap - 2 * inside), pill.Height);
                    TextRenderer.DrawText(graphics, content, Theme.Emphasis, text, Theme.Teal, ActionText);
                }
            }
            graphics.Restore(previous);
            args.Paint(args.ClipBounds, DataGridViewPaintParts.Focus);
            args.Handled = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                updateLifetime.Cancel();
                if (serverMenu != null) serverMenu.Dispose();
                desktopMenu?.Dispose();
                if (backupMenu != null) backupMenu.Dispose();
                if (tabMenu != null) tabMenu.Dispose();
                hints.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
