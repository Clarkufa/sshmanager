using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class SftpWindow : ThemedForm
    {
        private readonly SessionTarget target;
        private readonly Func<SftpClient> connect;
        private readonly FilePane local, remote;
        private readonly Button upload, download, cancel, reconnect;
        private readonly Label status, progressText;
        private readonly ProgressBar progressBar;
        private readonly ContextMenuStrip remoteMenu;
        private readonly System.Windows.Forms.Timer connectionWatch;
        private SftpClient client;
        private CancellationTokenSource operation;

        public SftpWindow(SessionTarget target, Func<SftpClient> connect)
        {
            this.target = target;
            this.connect = connect;
            Text = "Файлы — " + target.Alias + " · " + target.Address;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(1090, 620);
            MinimumSize = new Size(800, 440);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 12), ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            Controls.Add(layout);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = new Label { Text = "Файлы · " + target.Alias, Font = Theme.Title, AutoEllipsis = true,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            header.Controls.Add(title, 0, 0);
            reconnect = Theme.Button("Переподключить", 156, glyph: Glyph.Refresh);
            reconnect.Anchor = AnchorStyles.Right;
            reconnect.Click += async (sender, args) => await ConnectRemote();
            header.Controls.Add(reconnect, 1, 0);
            layout.Controls.Add(header, 0, 0);
            layout.SetColumnSpan(header, 2);

            local = new FilePane("Компьютер", "Путь на компьютере", new Padding(0, 0, 7, 0));
            remote = new FilePane(target.Address, "Путь на сервере", new Padding(7, 0, 0, 0));
            local.BrowseButton("Выбрать папку…", 142, Glyph.Browse, BrowseLocal);
            remote.BrowseButton("Новая папка", 122, Glyph.Plus, MakeDirectory);
            layout.Controls.Add(local, 0, 1);
            layout.Controls.Add(remote, 1, 1);
            local.Navigate += path => NavigateLocal(path);
            remote.Navigate += async path => await NavigateRemote(path);
            local.Up += () => NavigateLocal(Path.GetDirectoryName(local.DirectoryPath) ?? local.DirectoryPath);
            remote.Up += async () => await NavigateRemote(SftpClient.Join(remote.DirectoryPath, ".."));
            local.SelectionChanged += UpdateActions;
            remote.SelectionChanged += UpdateActions;
            local.Open += file => { if (file.Metadata.Directory) NavigateLocal(Path.Combine(local.DirectoryPath, file.Name)); };
            remote.Open += async file =>
            {
                if (file.Metadata.Directory || file.Metadata.Link) await NavigateRemote(SftpClient.Join(remote.DirectoryPath, file.Name));
            };
            remote.Files.AllowDrop = true;
            remote.Files.DragEnter += (sender, args) => args.Effect = Ready && args.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            remote.Files.DragOver += (sender, args) => args.Effect = Ready && args.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            remote.Files.DragDrop += async (sender, args) =>
            {
                if (Ready && args.Data.GetData(DataFormats.FileDrop) is string[] paths)
                    await Upload(paths);
            };
            local.Files.MouseDown += (sender, args) => TrackDrag(args);
            local.Files.MouseMove += (sender, args) => DragLocal(args);

            remoteMenu = Theme.Menu();
            remoteMenu.Items.Add(Theme.MenuItem("Скачать", Glyph.MoveLeft, async (sender, args) => await Download()));
            remoteMenu.Items.Add(Theme.MenuItem("Переименовать", Glyph.Edit, async (sender, args) => await Rename()));
            remoteMenu.Items.Add(Theme.MenuItem("Удалить", Glyph.Delete, async (sender, args) => await Delete()));
            remoteMenu.Opening += (sender, args) =>
            {
                int selected = remote.Files.SelectedRows.Count;
                args.Cancel = !Ready || selected == 0;
                remoteMenu.Items[1].Enabled = selected == 1;
            };
            remote.Files.ContextMenuStrip = remoteMenu;
            remote.Files.CellMouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Right || args.RowIndex < 0 || remote.Files.Rows[args.RowIndex].Selected) return;
                remote.Files.ClearSelection();
                remote.Files.Rows[args.RowIndex].Selected = true;
            };

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            upload = Theme.Button("На сервер", 140, primary: true, glyph: Glyph.Move);
            download = Theme.Button("На компьютер", 151, glyph: Glyph.MoveLeft);
            cancel = Theme.Button("Отменить", 106);
            upload.Margin = new Padding(0, 1, 0, 0);
            upload.Click += async (sender, args) => await Upload(LocalSelection());
            download.Click += async (sender, args) => await Download();
            cancel.Click += async (sender, args) =>
            {
                var current = operation;
                if (current == null) return;
                current.Cancel();
                cancel.Enabled = false;
                status.Text = "Отмена операции…";
                if (remote.DirectoryPath.Length == 0) { Disconnect(); return; }
                await Task.Delay(2000);
                if (!IsDisposed && operation == current) Disconnect();
            };
            progressText = new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted,
                Width = 325, Height = 34, Margin = new Padding(14, 1, 0, 0) };
            actions.Controls.AddRange(new Control[] { upload, download, cancel, progressText });
            layout.Controls.Add(actions, 0, 2);
            layout.SetColumnSpan(actions, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            progressBar = new ProgressBar { Anchor = AnchorStyles.Right, Size = new Size(155, 10), Maximum = 100, Visible = false };
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(progressBar, 1, 0);
            layout.Controls.Add(footer, 0, 3);
            layout.SetColumnSpan(footer, 2);

            Shown += async (sender, args) =>
            {
                NavigateLocal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                await ConnectRemote();
            };
            connectionWatch = new System.Windows.Forms.Timer { Interval = 1000 };
            connectionWatch.Tick += (sender, args) =>
            {
                if (operation != null || client == null || client.Connected) return;
                Disconnect();
                status.Text = "Подключение закрыто. Нажмите «Переподключить».";
                UpdateActions();
            };
            connectionWatch.Start();
            UpdateActions();
        }

        private bool Ready => operation == null && client != null && client.Connected && remote.DirectoryPath.Length > 0;

        private void UpdateActions()
        {
            if (upload == null) return;
            upload.Enabled = Ready && local.Files.SelectedRows.Count > 0;
            download.Enabled = Ready && local.DirectoryPath.Length > 0 && remote.Files.SelectedRows.Count > 0;
            cancel.Enabled = operation != null && !operation.IsCancellationRequested;
            reconnect.Enabled = operation == null;
            local.Enabled = operation == null;
            remote.Enabled = Ready;
        }

        private async Task ConnectRemote()
        {
            Disconnect();
            remote.Load("", new RemoteFile[0]);
            try { client = connect(); }
            catch (Exception ex) when (IsFileError(ex))
            {
                status.Text = ex.Message;
                Theme.ShowError(this, ex);
                UpdateActions();
                return;
            }
            var connection = client;
            await Run("Подключение к " + target.Alias + "…", token =>
            {
                connection.Initialize();
                return ReadRemote(connection, ".", token);
            }, current =>
            {
                remote.Load(current.Path, current.Items);
                status.Text = "Подключено к " + target.Alias;
            });
            if (!IsDisposed && remote.DirectoryPath.Length == 0)
            {
                Disconnect();
                UpdateActions();
            }
        }

        private void NavigateLocal(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                local.Load(full, ReadLocal(full));
            }
            catch (Exception ex) when (IsFileError(ex)) { Theme.ShowError(this, ex); }
        }

        private void BrowseLocal()
        {
            using (var picker = new FolderBrowserDialog { SelectedPath = local.DirectoryPath, ShowNewFolderButton = true })
                if (picker.ShowDialog(this) == DialogResult.OK) NavigateLocal(picker.SelectedPath);
        }

        private async Task NavigateRemote(string path)
        {
            if (!Ready || String.IsNullOrWhiteSpace(path)) return;
            if (!path.StartsWith("/", StringComparison.Ordinal)) path = SftpClient.Join(remote.DirectoryPath, path);
            var connection = client;
            await Run("Чтение папки…", token => ReadRemote(connection, path, token), current =>
            {
                remote.Load(current.Path, current.Items);
                status.Text = "Подключено к " + target.Alias;
            });
        }

        private async void MakeDirectory()
        {
            if (!Ready) return;
            string name = AskName("Новая папка на сервере", null, "Создать");
            if (name != null) await ChangeRemote((connection, path, token) => connection.CreateDirectory(SftpClient.Join(path, name)));
        }

        private async Task Rename()
        {
            var selection = remote.Selection();
            if (!Ready || selection.Length != 1) return;
            var item = selection[0];
            string name = AskName("Переименовать на сервере", item.Name, "Сохранить");
            if (name == null || name == item.Name) return;
            await ChangeRemote((connection, path, token) => connection.Move(SftpClient.Join(path, item.Name), SftpClient.Join(path, name)));
        }

        private async Task Delete()
        {
            var selection = remote.Selection();
            if (!Ready || selection.Length == 0) return;
            var message = selection.Length == 1 ? "Удалить «" + selection[0].Name + "»?" : "Удалить выбранные элементы (" + selection.Length + ")?";
            if (MessageBox.Show(this, message + "\n\nСервер: " + target.Alias + "\nПапка: " + remote.DirectoryPath +
                "\nУдалить можно файлы и пустые каталоги.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await ChangeRemote((connection, path, token) =>
            {
                foreach (var item in selection)
                {
                    token.ThrowIfCancellationRequested();
                    var full = SftpClient.Join(path, item.Name);
                    var current = connection.Info(full);
                    if (current != null) connection.Delete(full, current.Directory && !current.Link);
                }
            });
        }

        private string AskName(string caption, string current, string action)
        {
            string name = null;
            using (var dialog = new NameDialog(caption, "Имя", current, action, value =>
            {
                if (String.IsNullOrWhiteSpace(value) || value == "." || value == ".." ||
                    value.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0 || value.Any(Char.IsControl))
                    throw new ArgumentException("Укажите имя файла или папки без разделителей пути.");
                name = value;
            }, maxLength: 255))
                return dialog.ShowDialog(this) == DialogResult.OK ? name : null;
        }

        private async Task ChangeRemote(Action<SftpClient, string, CancellationToken> action)
        {
            var connection = client;
            string path = remote.DirectoryPath;
            await Run("Обновление папки…", token =>
            {
                action(connection, path, token);
                return connection.List(path, token);
            }, items =>
            {
                remote.Load(path, items);
                status.Text = "Папка обновлена.";
            });
        }

        private string[] LocalSelection() => local.Selection().Select(item => Path.Combine(local.DirectoryPath, item.Name)).ToArray();

        private async Task Upload(string[] paths)
        {
            if (!Ready || paths.Length == 0) return;
            string directory = remote.DirectoryPath;
            await Transfer("Загрузка на сервер", engine => engine.Upload(paths, directory), refreshRemote: true);
        }

        private async Task Download()
        {
            var selection = remote.Selection();
            if (!Ready || local.DirectoryPath.Length == 0 || selection.Length == 0) return;
            string source = remote.DirectoryPath, destination = local.DirectoryPath;
            await Transfer("Скачивание на компьютер", engine => engine.Download(source, selection, destination), refreshRemote: false);
        }

        private async Task Transfer(string caption, Action<SftpTransfers> copy, bool refreshRemote)
        {
            var connection = client;
            var reports = new Progress<TransferUpdate>(update =>
            {
                if (operation == null || operation.IsCancellationRequested || IsDisposed) return;
                progressText.Text = update.Name + " · " + SizeText(update.Done) +
                    (update.Total >= 0 ? " / " + SizeText(update.Total) : "");
                progressBar.Style = update.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                progressBar.Value = update.Total > 0 ? (int)Math.Min(100, update.Done * 100.0 / update.Total) : 0;
            });
            SftpTransfers engine = null;
            string directory = remote.DirectoryPath, localPath = local.DirectoryPath;
            await Run(caption, token =>
            {
                engine = new SftpTransfers(connection, token, AskConflict,
                    (name, done, total) => ((IProgress<TransferUpdate>)reports).Report(new TransferUpdate(name, done, total)));
                copy(engine);
                return refreshRemote ? connection.List(directory, token) : ReadLocal(localPath);
            }, items =>
            {
                if (refreshRemote) remote.Load(directory, items);
                else local.Load(localPath, items);
                status.Text = "Передано файлов: " + engine.Completed + (engine.Skipped == 0 ? "" : " · пропущено: " + engine.Skipped);
            }, transfer: true);
        }

        private FileConflict AskConflict(string destination, bool canReplace)
        {
            try
            {
                return (FileConflict)Invoke((Func<FileConflict>)(() =>
                {
                    if (IsDisposed || operation == null || operation.IsCancellationRequested) return FileConflict.Cancel;
                    using (var dialog = new FileConflictDialog(destination, canReplace))
                    {
                        dialog.ShowDialog(this);
                        return dialog.Choice;
                    }
                }));
            }
            catch (InvalidOperationException) { return FileConflict.Cancel; }
        }

        private async Task Run<T>(string message, Func<CancellationToken, T> action, Action<T> done, bool transfer = false)
        {
            if (operation != null) return;
            using (var current = new CancellationTokenSource())
            {
                operation = current;
                status.Text = message;
                progressBar.Visible = transfer;
                progressBar.Style = ProgressBarStyle.Marquee;
                UpdateActions();
                try
                {
                    T result = await Task.Run(() => action(current.Token));
                    if (!IsDisposed)
                    {
                        current.Token.ThrowIfCancellationRequested();
                        done(result);
                    }
                }
                catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Операция отменена."; }
                catch (Exception ex) when (IsFileError(ex))
                {
                    if (!IsDisposed)
                    {
                        status.Text = current.IsCancellationRequested ? "Операция отменена." : ex.Message;
                        if (ex is SftpProtocolException) Disconnect();
                        if (!current.IsCancellationRequested) Theme.ShowError(this, ex);
                    }
                }
                finally
                {
                    operation = null;
                    if (!IsDisposed)
                    {
                        progressBar.Visible = false;
                        progressText.Text = "";
                        UpdateActions();
                    }
                }
            }
        }

        private void Disconnect()
        {
            client?.Dispose();
            client = null;
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            if (operation != null && progressBar.Visible && args.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "Прервать передачу и закрыть окно?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) args.Cancel = true;
            base.OnFormClosing(args);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                operation?.Cancel();
                Disconnect();
                remoteMenu?.Dispose();
                connectionWatch?.Dispose();
            }
            base.Dispose(disposing);
        }

        private Rectangle dragStart;
        private void TrackDrag(MouseEventArgs args)
        {
            dragStart = Rectangle.Empty;
            if (args.Button != MouseButtons.Left || local.Files.HitTest(args.X, args.Y).RowIndex < 0) return;
            var size = SystemInformation.DragSize;
            dragStart = new Rectangle(args.X - size.Width / 2, args.Y - size.Height / 2, size.Width, size.Height);
        }

        private void DragLocal(MouseEventArgs args)
        {
            if (!Ready || args.Button != MouseButtons.Left || dragStart.IsEmpty || dragStart.Contains(args.Location)) return;
            dragStart = Rectangle.Empty;
            string[] paths = LocalSelection();
            if (paths.Length == 0) return;
            var data = new DataObject(DataFormats.FileDrop, paths);
            local.Files.DoDragDrop(data, DragDropEffects.Copy);
        }

        private static bool IsFileError(Exception ex) => Theme.IsFileError(ex) || ex is ArgumentException ||
            ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException;

        private static IEnumerable<RemoteFile> ReadLocal(string path)
        {
            var directory = new DirectoryInfo(path);
            return directory.EnumerateFileSystemInfos().Select(item =>
            {
                var attributes = item.Attributes;
                bool link = SftpTransfers.IsLink(attributes);
                return new RemoteFile(item.Name, new FileMetadata(SftpTransfers.IsDirectory(attributes), link,
                    !link && item is FileInfo file ? file.Length : -1, item.LastWriteTime));
            }).ToArray();
        }

        private static DirectoryContents ReadRemote(SftpClient connection, string path, CancellationToken token)
        {
            var canonical = connection.CanonicalPath(path);
            return new DirectoryContents(canonical, connection.List(canonical, token));
        }

        private static string SizeText(long bytes)
        {
            if (bytes < 0) return "—";
            if (bytes < 1024) return bytes + " Б";
            double amount = bytes;
            string[] units = { "КБ", "МБ", "ГБ", "ТБ", "ПБ" };
            int unit = -1;
            do { amount /= 1024; unit++; } while (amount >= 1024 && unit < units.Length - 1);
            return amount.ToString("0.##") + " " + units[unit];
        }

        private sealed class DirectoryContents
        {
            public readonly string Path;
            public readonly IEnumerable<RemoteFile> Items;
            public DirectoryContents(string path, IEnumerable<RemoteFile> items) { Path = path; Items = items; }
        }

        private sealed class TransferUpdate
        {
            public readonly string Name;
            public readonly long Done, Total;
            public TransferUpdate(string name, long done, long total) { Name = name; Done = done; Total = total; }
        }

        private sealed class FilePane : TableLayoutPanel
        {
            private readonly TextBox path;
            private readonly FlowLayoutPanel tools;
            public readonly DataGridView Files;
            public string DirectoryPath { get; private set; } = "";
            public event Action<string> Navigate;
            public event Action Up;
            public event Action<RemoteFile> Open;
            public event Action SelectionChanged;

            public FilePane(string title, string addressName, Padding margin)
            {
                Dock = DockStyle.Fill;
                Margin = margin;
                ColumnCount = 1;
                RowCount = 4;
                RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
                RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
                RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                Controls.Add(new Label { Text = title, AutoEllipsis = true, Font = Theme.Emphasis, Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
                path = new TextBox { AccessibleName = addressName };
                path.KeyDown += (sender, args) =>
                {
                    if (args.KeyCode != Keys.Enter) return;
                    args.SuppressKeyPress = true;
                    Navigate?.Invoke(path.Text);
                };
                Controls.Add(Theme.Frame(path, new Padding(0, 1, 0, 7)), 0, 1);
                tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
                BrowseButton("Вверх", 82, Glyph.MoveLeft, () => Up?.Invoke());
                BrowseButton("Обновить", 104, Glyph.Refresh, () => Navigate?.Invoke(DirectoryPath));
                Controls.Add(tools, 0, 2);

                Files = new DataGridView
                {
                    Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackgroundColor = Theme.Surface,
                    GridColor = Theme.Line, ReadOnly = true, EditMode = DataGridViewEditMode.EditProgrammatically,
                    RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                    EnableHeadersVisualStyles = false, MultiSelect = true,
                    CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersHeight = 33,
                    ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                    DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(6, 0, 5, 0) },
                    RowTemplate = { Height = 30 }, AccessibleName = title
                };
                Files.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Имя", MinimumWidth = 85, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
                Files.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Размер", Width = 79 });
                Files.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Изменён", Width = 122 });
                foreach (DataGridViewColumn column in Files.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
                Files.SelectionChanged += (sender, args) => SelectionChanged?.Invoke();
                Files.CellDoubleClick += (sender, args) => { if (args.RowIndex >= 0) Open?.Invoke((RemoteFile)Files.Rows[args.RowIndex].Tag); };
                Files.KeyDown += (sender, args) =>
                {
                    if (args.KeyCode != Keys.Enter || Files.CurrentRow == null) return;
                    args.SuppressKeyPress = true;
                    Open?.Invoke((RemoteFile)Files.CurrentRow.Tag);
                };
                Files.CellPainting += DrawName;
                var border = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(1), BackColor = Theme.Line };
                border.Controls.Add(Files);
                Controls.Add(border, 0, 3);
            }

            public void BrowseButton(string label, int width, Glyph icon, Action action)
            {
                var button = Theme.Button(label, width, glyph: icon, quiet: true);
                button.Height = 31;
                if (tools.Controls.Count == 0) button.Margin = new Padding(0, 1, 0, 0);
                button.Click += (sender, args) => action();
                tools.Controls.Add(button);
            }

            public void Load(string directory, IEnumerable<RemoteFile> items)
            {
                DirectoryPath = path.Text = directory;
                Files.SuspendLayout();
                try
                {
                    Files.Rows.Clear();
                    var rows = items.OrderByDescending(item => item.Metadata.Directory)
                        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(item =>
                        {
                            var row = (DataGridViewRow)Files.RowTemplate.Clone();
                            row.CreateCells(Files, item.Name,
                                item.Metadata.Directory || item.Metadata.Link ? "—" : SizeText(item.Metadata.Size),
                                item.Metadata.Modified?.ToString("dd.MM.yy HH:mm") ?? "—");
                            row.Tag = item;
                            return row;
                        }).ToArray();
                    if (rows.Length != 0) Files.Rows.AddRange(rows);
                    Files.ClearSelection();
                }
                finally { Files.ResumeLayout(); }
            }

            public RemoteFile[] Selection() => Files.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index)
                .Select(row => (RemoteFile)row.Tag).Where(item => item != null).ToArray();

            private void DrawName(object sender, DataGridViewCellPaintingEventArgs args)
            {
                if (args.RowIndex < 0 || args.ColumnIndex != 0 || !(Files.Rows[args.RowIndex].Tag is RemoteFile item)) return;
                args.Paint(args.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border);
                var box = args.CellBounds;
                int icon = 16 * DeviceDpi / 96, inset = 8 * DeviceDpi / 96;
                var symbol = item.Metadata.Link ? Glyph.Move : item.Metadata.Directory ? Glyph.Folder : Glyph.Copy;
                args.Graphics.DrawImage(Theme.Icon(symbol, item.Metadata.Directory ? Theme.Teal : Theme.Muted),
                    new Rectangle(box.Left + inset, box.Top + (box.Height - icon) / 2, icon, icon));
                var text = new Rectangle(box.Left + icon + inset * 2, box.Top, Math.Max(0, box.Width - icon - inset * 3), box.Height);
                TextRenderer.DrawText(args.Graphics, item.Name, Theme.Body, text, Theme.Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                args.Paint(args.ClipBounds, DataGridViewPaintParts.Focus);
                args.Handled = true;
            }
        }

        private sealed class FileConflictDialog : ThemedForm
        {
            public FileConflict Choice { get; private set; } = FileConflict.Cancel;
            public FileConflictDialog(string destination, bool canReplace)
            {
                Text = "Файл уже существует";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                ShowInTaskbar = MinimizeBox = MaximizeBox = false;
                Font = Theme.Body;
                ForeColor = Theme.Ink;
                BackColor = Theme.Canvas;
                ClientSize = new Size(570, 153);
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 13, 18, 12), ColumnCount = 1 };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
                layout.Controls.Add(new Label { Text = "Элемент с таким именем уже есть:\n" + destination,
                    AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
                var abort = Theme.Button("Отмена", 85);
                abort.DialogResult = DialogResult.Cancel;
                CancelButton = abort;
                buttons.Controls.Add(abort);
                Button Add(string text, int width, FileConflict choice)
                {
                    var button = Theme.Button(text, width, choice == FileConflict.KeepBoth);
                    button.Click += (sender, args) => { Choice = choice; DialogResult = DialogResult.OK; };
                    buttons.Controls.Add(button);
                    return button;
                }
                Add("Пропустить", 111, FileConflict.Skip);
                AcceptButton = Add("Сохранить оба", 132, FileConflict.KeepBoth);
                if (canReplace) Add("Заменить", 104, FileConflict.Replace);
                layout.Controls.Add(buttons, 0, 1);
                Controls.Add(layout);
            }
        }
    }
}
