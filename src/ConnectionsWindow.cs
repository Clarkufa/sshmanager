using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Dictionary<string, SftpWindow> fileWindows = new Dictionary<string, SftpWindow>(StringComparer.Ordinal);
        private readonly string sshDirectory;
        private readonly ConfigFile file;
        private readonly GroupFile groupFile;
        private readonly Button add;
        private readonly SurfaceButton serversButton, sessionsButton;
        private bool backupRunning;
        private readonly SurfaceButton themeButton;
        private readonly SurfaceButton updateButton;
        private readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        private AppUpdate availableUpdate;
        private readonly Panel pages;
        private readonly Control start;
        private Label startTitle, startDetail;
        private Button startRetry;
        private ContextMenuStrip columnMenu;
        private readonly BackupCommands backup;
        private readonly PathLabel source;
        private readonly ToolTip hints = new ToolTip();
        private readonly Label status;
        private readonly Label catalogTitle;
        private readonly string settingsPath;
        private readonly ContextMenuStrip serverMenu;
        private ContextMenuStrip desktopMenu;
        private readonly ContextMenuStrip tabMenu;
        private readonly ToolStripMenuItem tabColorMenu;
        private readonly ToolStripMenuItem moveTabLeft;
        private readonly ToolStripMenuItem moveTabRight;
        private readonly ToolStripMenuItem foldTab, renameTab, deleteTab;
        private readonly ToolStripSeparator tabSeparator;
        private readonly ToolStripMenuItem moveMenu;
        private readonly ToolStripMenuItem favoriteItem;
        private readonly TextBox quick;
        private readonly SurfaceButton settingsButton;
        private readonly TableLayoutPanel catalogHeader;
        private Groups groups = new Groups();
        private Connection menuConnection;
        private ConfigDocument document;
        /// <summary>The tab of the group header menu.</summary>
        private string menuGroupId;
        private SessionsPage sessions;
        /// <summary>The protocol chosen in the «Серверы» column: «Добавить сервер» creates one of this kind.</summary>
        private CatalogKind kind = CatalogKind.Ssh;

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
            backup = new BackupCommands(this, sshDirectory, groupsPath, () => Reload());
            Text = "SSH Manager " + AppUpdates.CurrentVersion.ToString(3);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterScreen;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(1280, 720);
            MinimumSize = new Size(800, 480);

            var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            pages = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            frame.Controls.Add(pages, 0, 1);
            Controls.Add(frame);

            add = Theme.Button("Добавить сервер", 158, true, Glyph.Plus);
            add.Click += (sender, args) =>
            {
                if (kind == CatalogKind.Rdp) EditDesktop(null);
                else if (kind == CatalogKind.Vnc) EditVnc(null);
                else EditConnection(null);
            };
            var header = catalogHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1, Margin = new Padding(10, 0, 10, 0) };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 3; i < header.ColumnCount; i++) header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new PictureBox { Image = Theme.AppMark(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(32, 32), Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 0, 0);
            catalogTitle = new Label { Text = "SSH Manager", Font = Theme.Emphasis, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) };
            header.Controls.Add(catalogTitle, 1, 0);
            add.Anchor = AnchorStyles.Right;
            serversButton = Theme.Button("Серверы", 94, glyph: Glyph.Server);
            serversButton.Click += (sender, args) => SessionWorkspace().ToggleServers();
            sessionsButton = Theme.Button("Сеансы", 116, glyph: Glyph.Terminal);
            sessionsButton.Click += (sender, args) => SessionWorkspace().ToggleSessions();
            themeButton = Theme.Button("", 130);
            Theme.RefreshSwitcher(themeButton);
            themeButton.Click += (sender, args) => ToggleTheme();
            settingsButton = Theme.Button("Настройки", 118, glyph: Glyph.Settings, quiet: true);
            settingsButton.Click += (sender, args) => EditSettings();
            var headerButtons = new Button[] { serversButton, sessionsButton, settingsButton, themeButton, add };
            for (int i = 0; i < headerButtons.Length; i++)
            {
                headerButtons[i].Anchor = AnchorStyles.Right;
                header.Controls.Add(headerButtons[i], i + 3, 0);
            }
            frame.Controls.Add(header, 0, 0);
            header.Resize += (sender, args) => FitHeader();

            quick = new TextBox { AccessibleName = "Быстрое подключение", MaxLength = 300 };
            Theme.SetCue(quick, "Быстрое подключение: user@host, rdp:// или vnc://");
            hints.SetToolTip(quick, "Подключиться без сохранения профиля. Enter — подключиться.");
            quick.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Enter) { args.SuppressKeyPress = true; QuickConnect(); }
                else if (args.KeyCode == Keys.Escape && quick.TextLength > 0) { args.SuppressKeyPress = true; quick.Clear(); }
            };
            // The quick-connect field sits in the middle of the free space, at most 460 wide (docs/ui-spec.md §2.1).
            var quickFrame = Theme.Frame(quick, Padding.Empty);
            quickFrame.Dock = DockStyle.None;
            var quickHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(6, 0, 10, 0) };
            quickHost.Controls.Add(quickFrame);
            quickHost.Resize += (sender, args) =>
            {
                int width = Math.Min(quickHost.ClientSize.Width, Theme.Scaled(this, 460));
                int height = Theme.Scaled(this, 32);
                quickFrame.Bounds = new Rectangle((quickHost.ClientSize.Width - width) / 2, (quickHost.ClientSize.Height - height) / 2, width, height);
            };
            header.Controls.Add(quickHost, 2, 0);
            hints.SetToolTip(add, "Ctrl+N");

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
                Theme.MenuItem("Известные хосты…", Glyph.Key, (sender, args) => OpenKnownHosts(menuConnection)),
                new ToolStripSeparator(),
                Theme.MenuItem("Удалить", Glyph.Delete, (sender, args) => DeleteConnection(menuConnection))
            });
            // Header of a group in the «Серверы» column (docs/ui-spec.md §2.2).
            tabMenu = Theme.Menu();
            tabColorMenu = Theme.MenuItem("Цветовая метка", Glyph.Dot);
            AddColorMenuItem(null);
            foreach (var color in TabColor.Palette) AddColorMenuItem(color);
            foldTab = Theme.MenuItem("Свернуть", Glyph.None, (sender, args) => ToggleSection(menuGroupId));
            renameTab = Theme.MenuItem("Переименовать вкладку", Glyph.Edit, (sender, args) => EditGroup(menuGroupId));
            moveTabLeft = Theme.MenuItem("Переместить выше", Glyph.MoveLeft, (sender, args) => ShiftMenuTab(-1));
            moveTabRight = Theme.MenuItem("Переместить ниже", Glyph.Move, (sender, args) => ShiftMenuTab(1));
            deleteTab = Theme.MenuItem("Удалить вкладку", Glyph.Delete, (sender, args) => DeleteGroup(menuGroupId));
            tabSeparator = new ToolStripSeparator();
            tabMenu.Items.AddRange(new ToolStripItem[]
            {
                foldTab, renameTab, tabColorMenu, moveTabLeft, moveTabRight, tabSeparator,
                Theme.MenuItem("Новая вкладка…", Glyph.Plus, (sender, args) => EditGroup(null)),
                deleteTab
            });
            start = MakeStartPage();

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
            frame.Controls.Add(footer, 0, 2);

            SessionWorkspace().ShowStart();
            Theme.ScaleToDpi(this);
            ResumeLayout();
            Shown += (sender, args) => Reload();
            Shown += async (sender, args) => await CheckUpdates(interactive: false);
        }

        /// <summary>A new server goes to the tab of the server or group chosen in the «Серверы» column.</summary>
        private string TargetGroupId => sessions?.Servers.SelectedGroupId;

        /// <summary>
        /// Keeps the page title whole: in a narrow window «Настройки» and then the theme switcher show only their
        /// icons, with the label in a tooltip.
        /// </summary>
        private void FitHeader()
        {
            if (!catalogHeader.IsHandleCreated) return;
            Theme.RefreshSwitcher(themeButton);
            var buttons = new Button[] { serversButton, settingsButton, themeButton, sessionsButton, add };
            int Needed() => Theme.Scaled(this, 330) + catalogTitle.PreferredWidth + buttons.Sum(button => button.Width + button.Margin.Horizontal);
            void Compact(SurfaceButton button, bool compact, int logical)
            {
                string label = button.AccessibleName;
                button.Text = compact ? "" : label;
                button.Width = Theme.Scaled(this, compact ? 38 : logical);
                hints.SetToolTip(button, compact ? label : null);
            }
            Compact(settingsButton, false, 118);
            Compact(themeButton, false, 130);
            Compact(serversButton, false, 94);
            Compact(sessionsButton, false, 116);
            if (Needed() > catalogHeader.ClientSize.Width) Compact(settingsButton, true, 118);
            if (Needed() > catalogHeader.ClientSize.Width) Compact(themeButton, true, 130);
            if (Needed() > catalogHeader.ClientSize.Width) Compact(serversButton, true, 94);
            if (Needed() > catalogHeader.ClientSize.Width) Compact(sessionsButton, true, 116);
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
            UpdateGroups(items => items.ToggleFavorite(key), select);
        }

        /// <summary>«Известные хосты» (docs/ui-spec.md §9.2): for a server — its file and its host in the search.</summary>
        private void OpenKnownHosts(Connection focus)
        {
            var connections = document?.Connections().ToList() ?? new List<Connection>();
            var profileFiles = connections.Where(item => item.Get("userknownhostsfile").Length > 0)
                .GroupBy(item => item.Get("userknownhostsfile"), StringComparer.OrdinalIgnoreCase)
                .Select(group => new KeyValuePair<string, string[]>(group.Key, group.Select(item => item.Alias).ToArray()));
            string path = null, query = "";
            if (focus != null)
            {
                try { path = KnownHostsFile.Resolve(focus.Get("userknownhostsfile")); }
                catch (Exception error) when (error is ArgumentException || error is NotSupportedException) { path = null; }
                string host = focus.Get("hostname").Length > 0 ? focus.Get("hostname") : focus.Alias;
                int port = Int32.TryParse(focus.Get("port"), out var number) && number > 0 && number <= 65535 ? number : 22;
                query = KnownHostsFile.HostName(host, port);
            }
            using (var dialog = new KnownHostsDialog(profileFiles, path, query)) dialog.ShowDialog(this);
        }

        private void EditSettings()
        {
            using (var dialog = new SettingsDialog(groups.Preferences, () => OpenKnownHosts(null)))
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
                try { vnc = new VncProfile(Guid.NewGuid().ToString("N"), target.Host, target.Host, target.Port, target.User, TargetGroupId); }
                catch (ArgumentException error) { Theme.ShowError(this, error); return; }
                quick.Clear();
                ConnectVnc(vnc, forceNew: true);
                if (!Ask("Сохранить подключение «" + vnc.Name + "»?")) return;
                UpdateGroups(items => items.SaveVnc(vnc, false), vnc.Id);
                return;
            }
            if (target.Rdp)
            {
                RdpProfile profile;
                try { profile = new RdpProfile(Guid.NewGuid().ToString("N"), target.Host, target.Host, target.Port, target.User, "", TargetGroupId); }
                catch (ArgumentException error) { Theme.ShowError(this, error); return; }
                quick.Clear();
                ConnectDesktop(profile, forceNew: true);
                if (!Ask("Сохранить подключение «" + profile.Name + "»?")) return;
                UpdateGroups(items => items.SaveDesktop(profile, false), profile.Id);
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
                string tab = TargetGroupId;
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
            string tab = TargetGroupId;
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
                if (backupRunning || Application.OpenForms.Cast<Form>().Any(window => window.Modal && window.Visible))
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
            if (backupRunning) return;
            backupRunning = true;
            try { await action(); }
            catch (Exception ex) when (BackupCommands.IsBackupError(ex)) { if (!IsDisposed) Theme.ShowError(this, ex); }
            finally { backupRunning = false; }
        }

        private void Reload()
        {
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
            RefreshList(null);
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
        }

        /// <summary>Refills the «Серверы» column (selecting a saved server) and the footer.</summary>
        private void RefreshList(string select)
        {
            sessions?.RefreshServers(select);
            UpdateStatus();
        }

        /// <summary>Footer: the file of the chosen protocol and its number of servers; the start page text.</summary>
        private void UpdateStatus()
        {
            string path = kind == CatalogKind.Ssh ? Path.Combine(sshDirectory, "config") : settingsPath;
            source.Text = path;
            hints.SetToolTip(source, path);
            int count = sessions?.Servers.Count ?? 0;
            status.Text = "Подключений: " + count;
            add.Enabled = kind != CatalogKind.Ssh || document != null;
            if (startTitle == null) return;
            bool none = document != null && !document.Connections().Any() && groups.Desktops.Count == 0 && groups.Vnc.Count == 0;
            startTitle.Text = document == null ? "Не удалось прочитать SSH config" : none ? "Добавьте первый сервер" : "Выберите сервер слева";
            startDetail.Text = document == null ? "Проверьте файл " + Path.Combine(sshDirectory, "config") + " и нажмите «Обновить»."
                : none ? "SSH-серверы сохраняются в ~/.ssh/config, RDP и VNC — в настройках приложения. Можно перенести подключения из PuTTY, mRemoteNG или MobaXterm."
                : "Двойной щелчок или Enter открывает сеанс. Правая кнопка — изменить, открыть файлы, перенести во вкладку. Быстрое подключение — Ctrl+K.";
            startRetry.Visible = document == null;
        }

        /// <summary>The workspace without a session (docs/ui-spec.md §2.3).</summary>
        private Control MakeStartPage()
        {
            var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            startTitle = new Label { Font = Theme.Title, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 8) };
            startDetail = new Label { AutoSize = true, Anchor = AnchorStyles.None, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleCenter,
                MaximumSize = new Size(Theme.Scaled(this, 560), 0), Margin = new Padding(0, 0, 0, 18) };
            var actions = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = Padding.Empty };
            var addServer = Theme.Button("Добавить сервер", 170, true, Glyph.Plus);
            addServer.Click += (sender, args) => add.PerformClick();
            var import = Theme.Button("Импорт из других программ…", 272, glyph: Glyph.Folder);
            import.Click += (sender, args) => ImportOther();
            startRetry = Theme.Button("Обновить", 120, glyph: Glyph.Refresh);
            startRetry.Click += (sender, args) => Reload();
            actions.Controls.AddRange(new Control[] { addServer, import, startRetry });
            page.Controls.Add(startTitle, 0, 1);
            page.Controls.Add(startDetail, 0, 2);
            page.Controls.Add(actions, 0, 3);
            return page;
        }

        /// <summary>«⋯» of the «Серверы» column: tabs, order, refresh, backups and import (docs/ui-spec.md §2.2).</summary>
        private void ShowColumnMenu(Control at, Point point)
        {
            columnMenu?.Dispose();
            columnMenu = Theme.Menu();
            var order = Theme.MenuItem("Сортировка", Glyph.Move);
            var current = groups.Sort(null, kind);
            foreach (var pair in new[] { Tuple.Create(ServerSort.Original, "Исходный порядок"), Tuple.Create(ServerSort.Name, "По названию ↑"),
                Tuple.Create(ServerSort.NameDescending, "По названию ↓"), Tuple.Create(ServerSort.Address, "По адресу ↑"),
                Tuple.Create(ServerSort.AddressDescending, "По адресу ↓") })
            {
                var sort = pair.Item1;
                var protocol = kind;
                var item = Theme.MenuItem(pair.Item2, Glyph.None, (sender, args) => UpdateGroups(items => items.SetSort(null, protocol, sort)));
                item.Checked = current == sort;
                order.DropDownItems.Add(item);
            }
            columnMenu.Items.AddRange(new ToolStripItem[]
            {
                Theme.MenuItem("Новая вкладка…", Glyph.Plus, (sender, args) => EditGroup(null)),
                order,
                new ToolStripSeparator(),
                Theme.MenuItem("Обновить список", Glyph.Refresh, (sender, args) => Reload()),
                Theme.MenuItem("Экспорт архива…", Glyph.Copy, async (sender, args) => await RunBackup(backup.Export)),
                Theme.MenuItem("Импорт архива…", Glyph.Folder, async (sender, args) => await RunBackup(backup.Import)),
                Theme.MenuItem("Импорт из других программ…", Glyph.Folder, (sender, args) => ImportOther()),
                Theme.MenuItem("Импорт .rdp…", Glyph.Server, (sender, args) => TransferDesktop(null)),
                new ToolStripSeparator(),
                Theme.MenuItem("Открыть SSH config", Glyph.Edit, (sender, args) => OpenConfig()),
                Theme.MenuItem("Известные хосты…", Glyph.Key, (sender, args) => OpenKnownHosts(null))
            });
            foreach (ToolStripItem item in columnMenu.Items)
                if (item.Text == "Обновить список") item.ToolTipText = "F5";
            columnMenu.Show(at, point);
        }

        private void OpenConfig()
        {
            string path = Path.Combine(sshDirectory, "config");
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("Файл ~/.ssh/config появится после добавления первого сервера.");
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = false });
            }
            catch (Exception error) when (Theme.IsFileError(error) || error is System.ComponentModel.Win32Exception) { Theme.ShowError(this, error); }
        }

        /// <summary>Menu of a group header: fold, rename, color, order and delete for a tab; only folding for the others.</summary>
        private void ShowSectionMenu(string key, Control at, Point point)
        {
            menuGroupId = key;
            var group = groups.Find(key);
            foldTab.Text = groups.Collapsed.Contains(key) ? "Развернуть" : "Свернуть";
            foreach (var item in new ToolStripItem[] { renameTab, tabColorMenu, moveTabLeft, moveTabRight, deleteTab }) item.Available = group != null;
            if (group != null)
            {
                int index = groups.Items.IndexOf(group);
                moveTabLeft.Enabled = index > 0;
                moveTabRight.Enabled = index < groups.Items.Count - 1;
                var color = TabColor.Find(group.ColorId);
                foreach (ToolStripMenuItem item in tabColorMenu.DropDownItems)
                    item.Checked = (string)item.Tag == (color == null ? null : color.Id);
            }
            tabMenu.Show(at, point);
        }

        private void ShiftMenuTab(int offset)
        {
            string id = menuGroupId;
            UpdateGroups(items => items.ShiftTab(id, offset));
        }

        private void ToggleSection(string key)
        {
            try { groups = groupFile.Update(items => items.ToggleCollapsed(key)); }
            catch (Exception error) when (Theme.IsFileError(error) || error is System.Xml.XmlException) { Theme.ShowError(this, error); }
            sessions?.RefreshServers();
        }

        private void DeleteEntry(object entry)
        {
            if (entry is Connection connection) DeleteConnection(connection);
            else if (entry is RdpProfile profile) DeleteDesktop(profile);
            else if (entry is VncProfile vnc) DeleteVnc(vnc);
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
            using (var dialog = new VncDialog(profile, TargetGroupId, hasPassword, aliases, (replacement, password, forgetPassword) =>
            {
                groups = groupFile.Update(items => items.SaveVnc(replacement, profile != null));
                if (forgetPassword) VncPasswords.Set(replacement, null);
                else if (password.Length > 0) VncPasswords.Set(replacement, password);
                selected = replacement.Id;
            }))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || selected == null) return;
            }
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
            });
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

        private void ShowVncMenu(VncProfile profile, Control at, Point point)
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
            desktopMenu.Show(at, point);

            void AddDestination(string name, string id)
            {
                var item = Theme.MenuItem(SafeMenuText(name), Glyph.Folder, (sender, args) =>
                    UpdateGroups(items => items.MoveVnc(profile.Id, id), profile.Id));
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
            using (var dialog = new RdpDialog(profile, TargetGroupId, hasPassword, (replacement, password, forgetPassword) =>
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
            });
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
                        var imported = RdpProfile.Import(dialog.FileName, TargetGroupId);
                        groups = groupFile.Update(items => items.SaveDesktop(imported, false));
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
                    string groupId = TargetGroupId;
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
                });
            else RefreshList(null);
        }

        private static bool ContainsPasswordTarget(ConfigDocument configuration, PasswordTarget target)
        {
            return configuration.Connections().Any(connection => PasswordTarget.From(connection)?.Id == target.Id);
        }

        private void AddColorMenuItem(TabColor color)
        {
            string colorId = color == null ? null : color.Id;
            var swatch = color == null ? null : Theme.Icon(Glyph.Dot, color.Swatch);
            var item = new ToolStripMenuItem(color == null ? "Без метки" : color.Name, swatch) { Tag = colorId };
            item.Click += (sender, args) =>
            {
                string groupId = menuGroupId;
                UpdateGroups(items => items.Mark(groupId, colorId));
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
                RefreshList(null);
            }
        }

        private void DeleteGroup(string id)
        {
            var group = groups.Find(id);
            if (group == null || MessageBox.Show(this, "Удалить вкладку «" + group.Name + "»? Её подключения перейдут в «Без вкладки».",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            UpdateGroups(items => items.RemoveTab(group.Id));
        }

        /// <summary>The SSH row menu for the catalog table and the «Серверы» column (docs/ui-spec.md §2.7).</summary>
        private void ShowConnectionMenu(Connection connection, Control at, Point point)
        {
            menuConnection = connection;
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
                    RefreshList(connection.Alias);
                }
            }));
            serverMenu.Show(at, point);
        }

        private void ShowDesktopMenu(RdpProfile profile, Control at, Point point)
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
            desktopMenu.Show(at, point);

            void AddDestination(string name, string id)
            {
                var item = Theme.MenuItem(SafeMenuText(name), Glyph.Folder, (sender, args) =>
                    UpdateGroups(items => items.MoveDesktop(profile.Id, id), profile.Id));
                item.Checked = profile.GroupId == id;
                move.DropDownItems.Add(item);
            }
        }

        private void AddMoveItem(Connection connection, string name, string groupId, string current)
        {
            var item = Theme.MenuItem(SafeMenuText(name), Glyph.Folder, (sender, args) =>
                UpdateGroups(items => items.Move(connection.Alias, groupId), connection.Alias));
            item.Checked = groupId == current;
            moveMenu.DropDownItems.Add(item);
        }

        /// <summary>Changes tabs.xml and refills the column, selecting <paramref name="select"/> (an alias or a profile id).</summary>
        private void UpdateGroups(Action<Groups> change, string select = null)
        {
            try { groups = groupFile.Update(change); }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is InvalidOperationException) { Theme.ShowError(this, ex); }
            RefreshList(select);
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
            bool fromStart = sessions == null || sessions.ShowingStart;
            try
            {
                var workspace = SessionWorkspace();
                ShowSessions();
                if (!workspace.Open(new SessionTarget(profile, groups.Find(profile.GroupId)), forceNew) && fromStart) workspace.ShowStart();
            }
            catch (Exception error) when (error is ExternalException || error is TargetInvocationException ||
                error is InvalidOperationException || error is ArgumentException)
            {
                if (fromStart) sessions?.ShowStart();
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
            // «Файлы» open in the workspace of this window and are listed under «Открытые файлы» (docs/ui-spec.md §3).
            if (fileWindows.TryGetValue(id, out var open))
            {
                SessionWorkspace().ShowFilesPanel(open);
                return;
            }
            var window = new SftpWindow(target, connect, fallback);
            window.Embed();
            fileWindows.Add(id, window);
            window.FormClosed += (sender, args) => fileWindows.Remove(id);
            SessionWorkspace().AddFiles(window);
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
            sessions = new SessionsPage();
            sessions.AttachStart(start);
            sessions.ServerColumnMenuRequested += ShowColumnMenu;
            sessions.Servers.SectionMenuRequested += ShowSectionMenu;
            sessions.Servers.EditRequested += EditRow;
            sessions.Servers.DeleteRequested += DeleteEntry;
            sessions.Servers.ProtocolChanged += next => { kind = next; UpdateStatus(); };
            sessions.Preferences = groups.Preferences;
            sessions.Snippets = () => groups.Snippets;
            sessions.AddSnippet += AddSnippet;
            sessions.RemoveSnippet += RemoveSnippet;
            sessions.ShowFiles += OpenFiles;
            sessions.ShowVncFiles += OpenVncFiles;
            sessions.LoadServers += picker =>
                picker.UpdateServers(document == null ? new Connection[0] : document.Connections().ToArray(), groups);
            sessions.ServerSectionToggled += ToggleSection;
            sessions.ServerMenuRequested += (profile, at, point) =>
            {
                if (profile is RdpProfile desktop) ShowDesktopMenu(desktop, at, point);
                else if (profile is VncProfile vnc) ShowVncMenu(vnc, at, point);
                else if (profile is Connection connection) ShowConnectionMenu(connection, at, point);
            };
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
            sessions.Changed += () =>
            {
                int open = sessions.CountOpen();
                sessionsButton.AccessibleName = open == 0 ? "Сеансы" : "Сеансы · " + open;
                FitHeader();
                sessions.RefreshServers();
            };
            pages.Controls.Add(sessions);
            return sessions;
        }

        private void ShowSessions()
        {
            SessionWorkspace().ShowSession();
            sessions.FocusTerminal();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Ctrl+F, Ctrl+N, Ctrl+L and Ctrl+K belong to the shell (search, next command, clear screen, kill line)
            // while the terminal or a remote desktop has the focus.
            bool sessionKeys = sessions.SessionHasFocus;
            if (keyData == (Keys.Control | Keys.F) && !sessionKeys) { sessions.FocusServerSearch(); return true; }
            if (keyData == (Keys.Control | Keys.N) && !sessionKeys) { add.PerformClick(); return true; }
            if ((keyData == (Keys.Control | Keys.L) || keyData == (Keys.Control | Keys.K)) && !sessionKeys) { quick.Focus(); quick.SelectAll(); return true; }
            if (keyData == Keys.F5 && !sessionKeys) { Reload(); return true; }
            if (keyData == (Keys.Control | Keys.Tab)) { sessions.Cycle(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.P)) { sessions.FocusSearch(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                updateLifetime.Cancel();
                if (serverMenu != null) serverMenu.Dispose();
                desktopMenu?.Dispose();
                columnMenu?.Dispose();
                if (tabMenu != null) tabMenu.Dispose();
                hints.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
