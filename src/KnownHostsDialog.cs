using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SshManager
{
    /// <summary>«Известные хосты»: view, search, delete and add known_hosts entries (docs/ui-spec.md §9.2).</summary>
    internal sealed class KnownHostsDialog : ThemedForm
    {
        private sealed class FileChoice
        {
            public readonly string Path, Title;
            public FileChoice(string path, string title) { Path = path; Title = title; }
            public override string ToString() { return Title; }
        }

        private readonly ComboBox files;
        private readonly TextBox search;
        private readonly ListView list;
        private readonly Label status;
        private readonly Button add, remove, refresh;
        private KnownHostsFile current;
        private bool busy;

        /// <param name="profileFiles">UserKnownHostsFile values with the aliases that use them.</param>
        public KnownHostsDialog(IEnumerable<KeyValuePair<string, string[]>> profileFiles, string selectedPath = null, string query = "")
        {
            Text = "Известные хосты";
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = MinimizeBox = false;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(900, 540);
            MinimumSize = new Size(700, 420);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18, 14, 18, 12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(layout);

            var fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fileRow.Controls.Add(new Label { Text = "Файл", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            files = new ComboBox { AccessibleName = "Файл known_hosts", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Scaled(this, 19), Margin = new Padding(0, 6, 8, 0), MaxDropDownItems = 14 };
            files.DrawItem += Theme.DrawChoice;
            files.Items.Add(new FileChoice(KnownHostsFile.DefaultPath, Shorten(KnownHostsFile.DefaultPath) + " (по умолчанию)"));
            foreach (var pair in profileFiles)
            {
                string path;
                try { path = KnownHostsFile.Resolve(pair.Key); }
                catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) { continue; }
                if (files.Items.Cast<FileChoice>().Any(item => String.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                files.Items.Add(new FileChoice(path, Shorten(path) + " · " + String.Join(", ", pair.Value)));
            }
            fileRow.Controls.Add(files, 1, 0);
            var other = Theme.Button("Другой файл…", 136, glyph: Glyph.Browse);
            other.Anchor = AnchorStyles.Right;
            other.Click += (sender, args) => ChooseFile();
            fileRow.Controls.Add(other, 2, 0);
            var open = Theme.Button("Открыть в редакторе", 180, glyph: Glyph.Edit, quiet: true);
            open.Anchor = AnchorStyles.Right;
            open.Click += (sender, args) => OpenInEditor();
            fileRow.Controls.Add(open, 3, 0);
            layout.Controls.Add(fileRow, 0, 0);

            var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var filter = new ServerSearch("Найти хост", "Поиск в known_hosts") { Anchor = AnchorStyles.Left };
            search = filter.Input;
            search.Text = query ?? "";
            search.TextChanged += (sender, args) => Fill();
            tools.Controls.Add(filter, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            add = Theme.Button("Добавить…", 120, glyph: Glyph.Plus, quiet: true);
            add.Click += async (sender, args) => await AddKeys();
            remove = Theme.Button("Удалить", 98, glyph: Glyph.Delete, danger: true, quiet: true);
            remove.Click += (sender, args) => RemoveSelected();
            refresh = Theme.Button("Обновить", 108, glyph: Glyph.Refresh, quiet: true);
            refresh.Click += (sender, args) => LoadFile();
            actions.Controls.AddRange(new Control[] { add, refresh, remove });
            tools.Controls.Add(actions, 1, 0);
            layout.Controls.Add(tools, 0, 1);

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None, AccessibleName = "Записи known_hosts",
                BackColor = Theme.Surface, ForeColor = Theme.Ink };
            list.Columns.Add("Хост", 280);
            list.Columns.Add("Тип ключа", 170);
            list.Columns.Add("Отпечаток", 290);
            list.Columns.Add("Метка", 110);
            list.SelectedIndexChanged += (sender, args) => UpdateActions();
            list.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Delete) { args.SuppressKeyPress = true; RemoveSelected(); }
                else if (args.KeyData == (Keys.Control | Keys.A)) { foreach (ListViewItem item in list.Items) item.Selected = true; }
            };
            layout.Controls.Add(Theme.Frame(list, new Padding(0, 2, 0, 2)), 0, 2);
            status = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            layout.Controls.Add(status, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var close = Theme.Button("Закрыть", 100);
            close.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(close);
            layout.Controls.Add(buttons, 0, 4);
            CancelButton = close;

            int selected = selectedPath == null ? 0 : files.Items.Cast<FileChoice>().ToList()
                .FindIndex(item => String.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selectedPath != null && selected < 0)
            {
                files.Items.Add(new FileChoice(selectedPath, Shorten(selectedPath)));
                selected = files.Items.Count - 1;
            }
            files.SelectedIndex = Math.Max(0, selected);
            files.SelectedIndexChanged += (sender, args) => LoadFile();
            Theme.ScaleToDpi(this);
            ResumeLayout();
            Shown += (sender, args) => { LoadFile(); search.Focus(); search.SelectionStart = search.TextLength; };
        }

        private string SelectedPath => (files.SelectedItem as FileChoice)?.Path ?? KnownHostsFile.DefaultPath;

        private static string Shorten(string path)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase) ? "~\\" + path.Substring(home.Length + 1) : path;
        }

        private void LoadFile()
        {
            try { current = KnownHostsFile.Load(SelectedPath); }
            catch (Exception error) when (Theme.IsFileError(error))
            {
                current = null;
                list.Items.Clear();
                status.Text = "Не удалось прочитать файл. " + error.Message;
                UpdateActions();
                return;
            }
            Fill();
        }

        private void Fill()
        {
            if (current == null) return;
            var shown = current.Search(search.Text);
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var entry in shown)
                    list.Items.Add(new ListViewItem(new[] { entry.Display, entry.KeyType, entry.Fingerprint, entry.Marker }) { Tag = entry });
            }
            finally { list.EndUpdate(); }
            status.Text = current.Entries.Count == 0 ? (File.Exists(current.Path) ? "Записей нет." : "Файла пока нет: он появится после первой записи.")
                : search.Text.Trim().Length == 0 ? "Записей: " + current.Entries.Count : "Найдено: " + shown.Count + " из " + current.Entries.Count;
            UpdateActions();
        }

        private void UpdateActions()
        {
            remove.Enabled = !busy && current != null && list.SelectedItems.Count > 0;
            add.Enabled = refresh.Enabled = files.Enabled = !busy;
        }

        private void RemoveSelected()
        {
            var chosen = list.SelectedItems.Cast<ListViewItem>().Select(item => (KnownHostEntry)item.Tag).ToList();
            if (busy || current == null || chosen.Count == 0) return;
            if (MessageBox.Show(this, "Удалить записи (" + chosen.Count + ") из known_hosts?\n\n" + current.Path, Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                current.Remove(chosen);
                LoadFile();
                status.Text = "Удалено записей: " + chosen.Count + ". Прежняя версия: " + Path.GetFileName(current.Path) + ".old";
            }
            catch (Exception error) when (Theme.IsFileError(error)) { Theme.ShowError(this, error); }
        }

        private async Task AddKeys()
        {
            if (busy || current == null) return;
            string host;
            int port;
            using (var dialog = new HostKeyDialog(search.Text.Trim()))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                host = dialog.Host;
                port = dialog.Port;
            }
            busy = true;
            UpdateActions();
            status.Text = "Получение ключей " + KnownHostsFile.HostName(host, port) + "…";
            try
            {
                var keys = await Task.Run(() => KnownHostsFile.Scan(host, port));
                if (IsDisposed) return;
                string name = KnownHostsFile.HostName(host, port);
                string summary = String.Join("\n", keys.Select(KnownHostsFile.Describe).Where(entry => entry != null)
                    .Select(entry => entry.KeyType + " · " + entry.Fingerprint));
                if (MessageBox.Show(this, "Добавить ключи хоста в known_hosts? Прежние записи этого хоста будут заменены.\n\n" + name + "\n" + summary,
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    status.Text = "Ключи не добавлены.";
                    return;
                }
                current.Replace(name, keys);
                LoadFile();
                search.Text = name;
                status.Text = "Добавлено ключей: " + keys.Count + ".";
            }
            catch (Exception error) when (Theme.IsFileError(error) || error is ArgumentException || error is System.ComponentModel.Win32Exception)
            {
                if (!IsDisposed) status.Text = "Не удалось получить ключи. " + error.Message;
            }
            finally
            {
                busy = false;
                if (!IsDisposed) UpdateActions();
            }
        }

        private void ChooseFile()
        {
            using (var dialog = new OpenFileDialog { Title = "Файл known_hosts", CheckFileExists = false, Filter = "Все файлы|*.*",
                InitialDirectory = Path.GetDirectoryName(SelectedPath) })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                int index = files.Items.Cast<FileChoice>().ToList().FindIndex(item => String.Equals(item.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase));
                if (index < 0) { files.Items.Add(new FileChoice(dialog.FileName, Shorten(dialog.FileName))); index = files.Items.Count - 1; }
                files.SelectedIndex = index;
            }
        }

        private void OpenInEditor()
        {
            try
            {
                if (!File.Exists(SelectedPath)) throw new FileNotFoundException("Файла пока нет: он появится после первой записи.");
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + SelectedPath + "\"") { UseShellExecute = false });
            }
            catch (Exception error) when (Theme.IsFileError(error) || error is System.ComponentModel.Win32Exception) { Theme.ShowError(this, error); }
        }
    }

    /// <summary>«Добавить ключ хоста»: host and port for ssh-keyscan.</summary>
    internal sealed class HostKeyDialog : FormDialog
    {
        private readonly TextBox host, port;
        public string Host { get; private set; }
        public int Port { get; private set; }

        public HostKeyDialog(string suggested) : base("Добавить ключ хоста", new Size(470, 176), "Получить ключи", 90)
        {
            string name = suggested ?? "", number = "22";
            var match = System.Text.RegularExpressions.Regex.Match(name, @"^\[([^\]]+)\]:(\d{1,5})$");
            if (match.Success) { name = match.Groups[1].Value; number = match.Groups[2].Value; }
            host = AddField("Хост", name, 253, "server.example.com или 10.0.0.5");
            port = AddField("Порт", number, 5);
            Finish();
            ActiveControl = host;
        }

        protected override void Save()
        {
            string value = host.Text.Trim().Trim('[', ']');
            if (value.Length == 0) throw new ArgumentException("Укажите хост.");
            if (!Int32.TryParse(port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > 65535)
                throw new ArgumentException("Порт должен быть числом от 1 до 65535.");
            Host = value;
            Port = number;
        }
    }
}
