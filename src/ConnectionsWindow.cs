using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class ConnectionsWindow : ThemedForm
    {
        private const string FilesColumn = "FilesAction", TerminalColumn = "TerminalAction";
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
        private readonly TableLayoutPanel catalog;
        private readonly ContextMenuStrip backupMenu;
        private readonly BackupCommands backup;
        private readonly Label status;
        private readonly Label empty;
        private readonly ContextMenuStrip serverMenu;
        private readonly ContextMenuStrip tabMenu;
        private readonly ToolStripMenuItem tabColorMenu;
        private readonly ToolStripMenuItem moveTabLeft;
        private readonly ToolStripMenuItem moveTabRight;
        private readonly ToolStripMenuItem startupTab;
        private readonly ToolStripMenuItem moveMenu;
        private Groups groups = new Groups();
        private Connection menuConnection;
        private ConfigDocument document;
        private string activeGroupId;
        private SessionsPage sessions;

        public ConnectionsWindow(string sshDirectory, string groupsPath)
        {
            this.sshDirectory = sshDirectory;
            string path = Path.Combine(sshDirectory, "config");
            file = new ConfigFile(path);
            groupFile = new GroupFile(groupsPath);
            try { groups = groupFile.Load(); }
            catch (Exception ex) when (Theme.IsFileError(ex)) { /* Reload reports this when the window opens. */ }
            Theme.IsDark = groups.DarkTheme;
            backup = new BackupCommands(this, sshDirectory, groupsPath, () => Reload(openStartupTab: true));
            Text = "SSH Manager";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(980, 570);
            MinimumSize = new Size(800, 480);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 15, 24, 13), ColumnCount = 1, RowCount = 5 };
            catalog = layout;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            Controls.Add(layout);

            add = Theme.Button("Добавить сервер", 158, true, Glyph.Plus);
            add.Click += (sender, args) => EditConnection(null);
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Margin = new Padding(0, 0, 0, 7) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 51));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new PictureBox { Image = Theme.AppMark(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(42, 42), Anchor = AnchorStyles.Left, Margin = new Padding(0) }, 0, 0);
            header.Controls.Add(new Label { Text = "SSH-подключения", Font = Theme.Title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0) }, 1, 0);
            add.Anchor = AnchorStyles.Right;
            sessionsButton = Theme.Button("Сеансы", 116, glyph: Glyph.Terminal);
            sessionsButton.Anchor = AnchorStyles.Right;
            sessionsButton.Click += (sender, args) => ShowSessions();
            themeButton = Theme.Button("", 130);
            themeButton.Anchor = AnchorStyles.Right;
            Theme.RefreshSwitcher(themeButton);
            themeButton.Click += (sender, args) => ToggleTheme();
            header.Controls.Add(themeButton, 2, 0);
            header.Controls.Add(sessionsButton, 3, 0);
            header.Controls.Add(add, 4, 0);
            layout.Controls.Add(header, 0, 0);

            var tabBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 1) };
            tabBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tabBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tabs = new TabStrip { Dock = DockStyle.Fill, Margin = new Padding(0) };
            tabs.ReorderRequested += (id, anchorId, before) =>
                UpdateGroups(items => items.PlaceTab(id, anchorId, before), activeGroupId, Selected?.Alias);
            tabBar.Controls.Add(tabs, 0, 0);
            var newTab = Theme.Button("Новая вкладка", 140, glyph: Glyph.Plus, quiet: true);
            newTab.Height = 31;
            newTab.Click += (sender, args) => EditGroup(null);
            tabBar.Controls.Add(newTab, 1, 0);
            layout.Controls.Add(tabBar, 0, 1);

            var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.Controls.Add(tools, 0, 2);

            var filter = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            var serverSearch = new ServerSearch();
            search = serverSearch.Input;
            search.TextChanged += (sender, args) => RefreshList(null);
            filter.Controls.Add(serverSearch);
            tools.Controls.Add(filter, 0, 0);

            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            edit = Theme.Button("Изменить", 105, glyph: Glyph.Edit, quiet: true);
            delete = Theme.Button("Удалить", 95, glyph: Glyph.Delete, danger: true, quiet: true);
            var refresh = Theme.Button("Обновить", 106, glyph: Glyph.Refresh, quiet: true);
            backupButton = Theme.Button("Резервные копии", 154, glyph: Glyph.Folder, quiet: true);
            backupMenu = Theme.Menu();
            backupMenu.Items.Add(Theme.MenuItem("Экспорт архива…", Glyph.Copy, async (sender, args) => await RunBackup(backup.Export)));
            backupMenu.Items.Add(Theme.MenuItem("Импорт архива…", Glyph.Folder, async (sender, args) => await RunBackup(backup.Import)));
            backupButton.Click += (sender, args) => backupMenu.Show(backupButton, new Point(0, backupButton.Height));
            edit.Click += (sender, args) => { if (Selected != null) EditConnection(Selected); };
            delete.Click += (sender, args) => DeleteConnection(Selected);
            refresh.Click += (sender, args) => Reload();
            actions.Controls.AddRange(new Control[] { backupButton, refresh, edit, delete });
            tools.Controls.Add(actions, 1, 0);

            var list = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(1), BackColor = Theme.Line };
            layout.Controls.Add(list, 0, 3);
            grid = MakeGrid();
            grid.SelectionChanged += (sender, args) => UpdateActions();
            grid.CellMouseClick += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left && args.RowIndex >= 0 && args.ColumnIndex >= 0)
                    RunGridAction(grid.Columns[args.ColumnIndex], (Connection)grid.Rows[args.RowIndex].Tag);
            };
            grid.CellDoubleClick += (sender, args) =>
            {
                if (args.RowIndex >= 0 && args.ColumnIndex >= 0 && !(grid.Columns[args.ColumnIndex] is DataGridViewButtonColumn))
                    EditConnection((Connection)grid.Rows[args.RowIndex].Tag);
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
                if (Selected == null) return;
                if ((args.KeyCode == Keys.Enter || args.KeyCode == Keys.Space) &&
                    RunGridAction(grid.CurrentCell.OwningColumn, Selected))
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
            serverMenu = Theme.Menu();
            serverMenu.Items.AddRange(new ToolStripItem[]
            {
                Theme.MenuItem("Подключиться", Glyph.Terminal, (sender, args) => Connect(menuConnection)),
                Theme.MenuItem("Новый сеанс", Glyph.Plus, (sender, args) => Connect(menuConnection, forceNew: true)),
                Theme.MenuItem("Файлы (SFTP)", Glyph.Folder, (sender, args) => OpenFiles(menuConnection)),
                Theme.MenuItem("Открыть в Windows Terminal", Glyph.Terminal, (sender, args) => ConnectExternal(menuConnection)),
                Theme.MenuItem("Изменить", Glyph.Edit, (sender, args) => EditConnection(menuConnection)),
                moveMenu,
                Theme.MenuItem("Копировать команду SSH", Glyph.Copy, (sender, args) => CopyCommand(menuConnection)),
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
                    UpdateGroups(items => items.SetStartupTab(id), id, Selected?.Alias);
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

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var source = new Label { Text = path, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, ForeColor = Theme.Muted, Margin = new Padding(0) };
            status = new Label { Anchor = AnchorStyles.Bottom | AnchorStyles.Right, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(10, 0, 0, 0) };
            updateButton = Theme.Button("Обновления", 170, glyph: Glyph.Refresh, quiet: true);
            updateButton.Height = 28;
            updateButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            updateButton.Click += async (sender, args) => await CheckUpdates(interactive: true);
            footer.Controls.Add(source, 0, 0);
            footer.Controls.Add(updateButton, 1, 0);
            footer.Controls.Add(status, 2, 0);
            layout.Controls.Add(footer, 0, 4);

            Shown += (sender, args) => Reload(openStartupTab: true);
            Shown += async (sender, args) => await CheckUpdates(interactive: false);
        }

        private Connection Selected { get { return grid.CurrentRow == null ? null : (Connection)grid.CurrentRow.Tag; } }

        private async Task CheckUpdates(bool interactive)
        {
            if (!updateButton.Enabled) return;
            updateButton.Enabled = false;
            if (interactive) updateButton.Text = "Проверка…";
            var token = updateLifetime.Token;
            string staged = null;
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
            var selected = Selected;
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
            RefreshTabs(openStartupTab ? groups.StartupTabId : groupId);
            RefreshList(selected == null ? null : selected.Alias);
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
            sessions?.RefreshThemeButton();
            foreach (var window in fileWindows.Values) Theme.Apply(window);
        }

        private void RefreshList(string selectAlias)
        {
            grid.Rows.Clear();
            var activeGroup = groups.Find(activeGroupId);
            var connections = document == null ? new Connection[0] : document.Connections()
                .Where(connection => activeGroup == null || activeGroup.Aliases.Contains(connection.Alias)).ToArray();
            string query = search.Text.Trim();
            DataGridViewRow toSelect = null;
            foreach (var connection in connections)
            {
                var host = connection.Get("hostname");
                var user = connection.Get("user");
                var group = groups.ForAlias(connection.Alias);
                var groupName = group == null ? "" : group.Name;
                if (query.Length > 0 && !new[] { connection.Alias, host, user, groupName }.Any(text => text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                var port = connection.Get("port");
                var row = grid.Rows[grid.Rows.Add(connection.Alias, Dash(host), Dash(user), Dash(port), Dash(groupName),
                    "Файлы", SessionAction(connection))];
                row.Tag = connection;
                if (String.Equals(selectAlias, connection.Alias, StringComparison.Ordinal)) toSelect = row;
            }
            if (toSelect != null) grid.CurrentCell = toSelect.Cells[0];
            empty.Text = "По вашему запросу ничего не найдено.";
            if (document == null) empty.Text = "Не удалось прочитать SSH config. Нажмите «Обновить», чтобы повторить попытку.";
            else if (connections.Length == 0) empty.Text = activeGroup == null
                ? "Добавьте сервер, чтобы создать первое подключение."
                : "Добавьте сервер или перенесите сюда подключение из «Все серверы».";
            empty.Visible = grid.Rows.Count == 0;
            status.Text = grid.Rows.Count == connections.Length ? "Подключений: " + connections.Length : "Найдено: " + grid.Rows.Count + " из " + connections.Length;
            UpdateActions();
        }

        private void UpdateActions()
        {
            add.Enabled = document != null;
            edit.Enabled = Selected != null;
            delete.Enabled = Selected != null;
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
                    string groupId = activeGroupId;
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
            }))
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
            if (groups.ForAlias(connection.Alias) != null && !document.Connections().Any(item => String.Equals(item.Alias, connection.Alias, StringComparison.OrdinalIgnoreCase)))
                UpdateGroups(items => items.Move(connection.Alias, null), activeGroupId);
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
                AddGroupTab(Groups.AllName, null);
                foreach (var group in groups.Items) AddGroupTab(group.Name, group.Id, TabColor.Find(group.ColorId));
            }
            finally { tabs.ResumeLayout(); }
            SelectGroup(groups.Find(selected) == null ? null : selected);
        }

        private void AddGroupTab(string title, string id, TabColor color = null)
        {
            int padding = color == null ? 22 : 39;
            int desiredWidth = TextRenderer.MeasureText(title, Theme.Emphasis).Width + padding * DeviceDpi / 96;
            var tab = Theme.Button(title, Math.Min(250 * DeviceDpi / 96, desiredWidth));
            tab.Height = 30 * DeviceDpi / 96;
            tab.Margin = new Padding(0, 2, 5, 0);
            tab.Tag = id;
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
            string alias = Selected?.Alias;
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
            UpdateGroups(items => items.ShiftTab(id, offset), id, Selected?.Alias);
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
            menuConnection = Selected;
            if (menuConnection == null) return;
            var connection = menuConnection;
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

        private bool RunGridAction(DataGridViewColumn column, Connection connection)
        {
            if (column.Name == FilesColumn) OpenFiles(connection);
            else if (column.Name == TerminalColumn) Connect(connection);
            else return false;
            return true;
        }

        private void OpenFiles(Connection connection)
        {
            if (connection != null) OpenFiles(new SessionTarget(connection, groups.ForAlias(connection.Alias)));
        }

        private void OpenFiles(SessionTarget target)
        {
            string id = target.Alias + "\n" + target.Address;
            if (fileWindows.TryGetValue(id, out var open))
            {
                if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
                open.Show();
                open.Activate();
                return;
            }
            string config = Path.Combine(sshDirectory, "config");
            var window = new SftpWindow(target, () =>
            {
                var entry = new ConfigFile(config).Load().Connections().FirstOrDefault(item =>
                    String.Equals(item.Alias, target.Alias, StringComparison.OrdinalIgnoreCase) &&
                    new SessionTarget(item, null).Address == target.Address);
                if (entry == null) throw new IOException("Профиль сервера изменился. Откройте «Файлы» из каталога подключений.");
                var configOverride = String.Equals(sshDirectory, Terminal.SshDirectory, StringComparison.OrdinalIgnoreCase) ? null : config;
                return new SftpClient(target.Alias, PasswordTarget.From(entry), configOverride);
            });
            fileWindows.Add(id, window);
            window.FormClosed += (sender, args) => fileWindows.Remove(id);
            window.Show(this);
        }

        private SessionsPage SessionWorkspace()
        {
            if (sessions != null) return sessions;
            sessions = new SessionsPage { Visible = false };
            sessions.ThemeRequested += ToggleTheme;
            sessions.ShowFiles += OpenFiles;
            sessions.PickServer += () =>
            {
                var connections = document == null ? new Connection[0] : document.Connections().ToArray();
                using (var picker = new ServerPickerDialog(connections, groups.ForAlias))
                    if (picker.ShowDialog(this) == DialogResult.OK) Connect(picker.SelectedConnection, forceNew: true);
            };
            sessions.ShowServers += () =>
            {
                sessions.Hide();
                catalog.Show();
                grid.Focus();
            };
            sessions.Changed += () =>
            {
                int open = sessions.CountOpen();
                sessionsButton.Text = open == 0 ? "Сеансы" : "Сеансы · " + open;
                sessionsButton.AccessibleName = sessionsButton.Text;
                foreach (DataGridViewRow row in grid.Rows)
                    if (row.Tag is Connection) row.Cells[TerminalColumn].Value = SessionAction((Connection)row.Tag);
            };
            Controls.Add(sessions);
            return sessions;
        }

        private void ShowSessions()
        {
            var workspace = SessionWorkspace();
            catalog.Hide();
            workspace.Show();
            workspace.BringToFront();
            workspace.FocusTerminal();
        }

        private string SessionAction(Connection connection)
        {
            int count = sessions == null ? 0 : sessions.CountOpen(connection.Alias);
            return count == 0 ? "Подключиться" : "К сеансу · " + count;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (sessions != null && sessions.Visible)
            {
                if (keyData == (Keys.Control | Keys.Tab)) { sessions.Cycle(); return true; }
                if (keyData == (Keys.Control | Keys.Shift | Keys.P)) { sessions.FocusSearch(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static string Dash(string value) { return value.Length == 0 ? "—" : value; }

        private static DataGridView MakeGrid()
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
                ColumnHeadersHeight = 37, RowTemplate = { Height = 45 },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Canvas, ForeColor = Theme.Muted, Padding = new Padding(8, 0, 4, 0) },
                DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(8, 0, 4, 0) },
                RowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Ink,
                    SelectionBackColor = Theme.Selection, SelectionForeColor = Theme.Ink }
            };
            list.Columns.Add(MakeColumn("Псевдоним", 30, 103));
            list.Columns.Add(MakeColumn("Адрес", 34, 105));
            list.Columns.Add(MakeColumn("Пользователь", 23, 103));
            list.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Порт", Width = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
            list.Columns.Add(MakeColumn("Вкладка", 22, 79));
            list.Columns.Add(new DataGridViewButtonColumn
            {
                Name = FilesColumn, HeaderText = "SFTP", Width = 100, FlatStyle = FlatStyle.Flat, Resizable = DataGridViewTriState.False
            });
            list.Columns.Add(new DataGridViewButtonColumn
            {
                Name = TerminalColumn, HeaderText = "Терминал",
                Width = 159, FlatStyle = FlatStyle.Flat, Resizable = DataGridViewTriState.False
            });
            return list;
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
            var mode = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int scale = DeviceDpi;
            int iconSize = 16 * scale / 96;
            int inset = 9 * scale / 96;
            string content = Convert.ToString(args.FormattedValue);
            if (args.ColumnIndex == 0)
            {
                graphics.DrawImage(Theme.Icon(Glyph.Server, Theme.Teal), new Rectangle(box.Left + inset, box.Top + (box.Height - iconSize) / 2, iconSize, iconSize));
                var text = new Rectangle(box.Left + iconSize + inset * 2, box.Top, box.Width - iconSize - inset * 3, box.Height);
                TextRenderer.DrawText(graphics, content, Theme.Emphasis, text, Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                var pill = Rectangle.Inflate(box, -inset, -(box.Height / 6));
                using (var path = Theme.Round(pill, Math.Max(3, inset - 2)))
                using (var fill = new SolidBrush(Theme.Mint)) graphics.FillPath(fill, path);
                graphics.DrawImage(Theme.Icon(column.Name == FilesColumn ? Glyph.Folder : Glyph.Terminal, Theme.Teal),
                    new Rectangle(pill.Left + inset, box.Top + (box.Height - iconSize) / 2, iconSize, iconSize));
                var text = new Rectangle(pill.Left + iconSize + inset * 2, pill.Top, pill.Width - iconSize - inset * 2, pill.Height);
                TextRenderer.DrawText(graphics, content, Theme.Emphasis, text, Theme.Teal, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            graphics.SmoothingMode = mode;
            args.Paint(args.ClipBounds, DataGridViewPaintParts.Focus);
            args.Handled = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                updateLifetime.Cancel();
                if (serverMenu != null) serverMenu.Dispose();
                if (backupMenu != null) backupMenu.Dispose();
                if (tabMenu != null) tabMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
