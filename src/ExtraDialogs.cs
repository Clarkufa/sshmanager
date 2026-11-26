using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace SshManager
{
    /// <summary>Shared look of the small stage 2 dialogs: label column, framed fields, buttons at the bottom.</summary>
    internal abstract class FormDialog : ThemedForm
    {
        protected readonly TableLayoutPanel Grid;
        protected readonly SurfaceButton Commit;
        private int row;

        protected FormDialog(string title, Size size, string commit, int labelWidth = 190)
        {
            Text = title;
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = MinimizeBox = MaximizeBox = false;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = size;
            Grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18, 14, 18, 12) };
            Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(Grid);
            Commit = Theme.Button(commit, 128, true);
            Commit.Click += (sender, args) =>
            {
                try { Save(); DialogResult = DialogResult.OK; }
                catch (ArgumentException error) { Theme.ShowError(this, error); }
                catch (Exception error) when (Theme.IsFileError(error) || error is InvalidOperationException) { Theme.ShowError(this, error); }
            };
            AcceptButton = Commit;
        }

        protected abstract void Save();

        protected TextBox AddField(string label, string value, int limit, string cue = null)
        {
            var field = new TextBox { AccessibleName = label, Text = value ?? "", MaxLength = limit };
            if (cue != null) Theme.SetCue(field, cue);
            AddRow(label, Theme.Frame(field, new Padding(0, 2, 0, 2)), 32);
            return field;
        }

        protected CheckBox AddCheck(string label, bool value)
        {
            var check = new CheckBox { Text = label, Checked = value, AutoSize = true, Anchor = AnchorStyles.Left };
            AddRow(null, check, 28);
            return check;
        }

        protected void AddRow(string label, Control control, int height, SizeType type = SizeType.Absolute)
        {
            Grid.RowStyles.Add(new RowStyle(type, height));
            if (label != null) Grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            if (label == null && control.Dock == DockStyle.Fill && height > 40) { Grid.Controls.Add(control, 0, row); Grid.SetColumnSpan(control, 2); }
            else Grid.Controls.Add(control, 1, row);
            row++;
        }

        protected void Finish(params Control[] extra)
        {
            Grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row++;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var cancel = Theme.Button("Отмена", 90);
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.AddRange(new Control[] { Commit, cancel });
            buttons.Controls.AddRange(extra);
            Grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Grid.Controls.Add(buttons, 0, row);
            Grid.SetColumnSpan(buttons, 2);
            CancelButton = cancel;
            Theme.ScaleToDpi(this);
            ResumeLayout();
        }
    }

    /// <summary>«Настройки» (docs/ui-spec.md §7).</summary>
    internal sealed class SettingsDialog : FormDialog
    {
        private readonly Preferences preferences;
        private readonly ComboBox font;
        private readonly TextBox size, scrollback, user, port;
        private readonly CheckBox reconnect, tray, updates;
        public Preferences Result { get; private set; }

        public SettingsDialog(Preferences current) : base("Настройки", new Size(590, 392), "Сохранить", 230)
        {
            preferences = current.Copy();
            font = new ComboBox { AccessibleName = "Шрифт терминала", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Scaled(this, 19), Margin = new Padding(0, 4, 0, 0), MaxDropDownItems = 14 };
            font.DrawItem += Theme.DrawChoice;
            font.Items.Add("Consolas (по умолчанию)");
            foreach (var family in MonospaceFamilies()) font.Items.Add(family);
            int index = font.Items.IndexOf(preferences.TerminalFont);
            font.SelectedIndex = index < 0 ? 0 : index;
            AddRow("Шрифт терминала", font, 34);
            size = AddField("Размер шрифта", preferences.TerminalSize.ToString(CultureInfo.InvariantCulture), 2);
            scrollback = AddField("Строк прокрутки", preferences.Scrollback.ToString(CultureInfo.InvariantCulture), 6);
            user = AddField("Пользователь по умолчанию", preferences.DefaultUser, 128);
            port = AddField("Порт по умолчанию", preferences.DefaultPort, 5, "22");
            reconnect = AddCheck("Переподключать SSH автоматически", preferences.AutoReconnect);
            tray = AddCheck("Сворачивать в трей при закрытии окна", preferences.CloseToTray);
            updates = AddCheck("Проверять обновления при запуске", preferences.CheckUpdates);
            Finish();
        }

        protected override void Save()
        {
            int Number(TextBox box, int min, int max, string message)
            {
                if (!Int32.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
                    throw new ArgumentException(message);
                return value;
            }
            var next = preferences.Copy();
            next.TerminalFont = font.SelectedIndex <= 0 ? "" : (string)font.SelectedItem;
            next.TerminalSize = Number(size, 6, 48, "Размер шрифта — число от 6 до 48.");
            next.Scrollback = Number(scrollback, 100, 100000, "Строк прокрутки — число от 100 до 100000.");
            next.DefaultUser = user.Text.Trim();
            if (next.DefaultUser.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || "#'\"".Contains(ch)))
                throw new ArgumentException("Укажите имя пользователя без пробелов и кавычек.");
            next.DefaultPort = port.Text.Trim().Length == 0 ? "" :
                Number(port, 1, 65535, "Порт должен быть числом от 1 до 65535.").ToString(CultureInfo.InvariantCulture);
            next.AutoReconnect = reconnect.Checked;
            next.CloseToTray = tray.Checked;
            next.CheckUpdates = updates.Checked;
            Result = next;
        }

        /// <summary>Installed fixed-pitch families: "i" and "W" have the same width.</summary>
        private static IEnumerable<string> MonospaceFamilies()
        {
            var found = new List<string>();
            foreach (var family in FontFamily.Families)
            {
                if (!family.IsStyleAvailable(FontStyle.Regular) || family.Name.StartsWith("@")) continue;
                try
                {
                    using (var probe = new Font(family, 10))
                    {
                        var flags = TextFormatFlags.NoPadding;
                        int narrow = TextRenderer.MeasureText("iiiii", probe, Size.Empty, flags).Width;
                        int wide = TextRenderer.MeasureText("WWWWW", probe, Size.Empty, flags).Width;
                        if (narrow == wide && family.Name != "Consolas") found.Add(family.Name);
                    }
                }
                catch (ArgumentException) { }
            }
            return found.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);
        }
    }

    /// <summary>«Импорт из других программ»: PuTTY sessions or a mRemoteNG / MobaXterm file, with checkboxes.</summary>
    internal sealed class ImportDialog : FormDialog
    {
        private readonly ListView list;
        private readonly Label status;
        public List<FoundConnection> Chosen { get; private set; } = new List<FoundConnection>();

        public ImportDialog() : base("Импорт из других программ", new Size(680, 440), "Импортировать", 0)
        {
            Grid.ColumnStyles[0].Width = 0;
            var sources = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var putty = Theme.Button("PuTTY", 100, glyph: Glyph.Terminal);
            putty.Margin = new Padding(0, 1, 6, 0);
            putty.Click += (sender, args) => ShowFound(SafeRead(Importers.PuTTY));
            var file = Theme.Button("Файл mRemoteNG или MobaXterm…", 260, glyph: Glyph.Folder);
            file.Click += (sender, args) => ChooseFile();
            sources.Controls.AddRange(new Control[] { putty, file });
            AddRow(null, sources, 40);
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None, AccessibleName = "Найденные подключения",
                BackColor = Theme.Surface, ForeColor = Theme.Ink };
            list.Columns.Add("Протокол", 80);
            list.Columns.Add("Название", 170);
            list.Columns.Add("Адрес", 250);
            list.Columns.Add("Источник", 110);
            list.ItemChecked += (sender, args) => Commit.Enabled = list.CheckedItems.Count > 0;
            var frame = Theme.Frame(list, new Padding(0, 4, 0, 4));
            frame.Dock = DockStyle.Fill;
            AddRow(null, frame, 260);
            status = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft,
                Text = "Выберите источник. Пароли из других программ не переносятся." };
            AddRow(null, status, 28);
            Commit.Enabled = false;
            Finish();
        }

        protected override void Save()
        {
            Chosen = list.CheckedItems.Cast<ListViewItem>().Select(item => (FoundConnection)item.Tag).ToList();
        }

        private List<FoundConnection> SafeRead(Func<List<FoundConnection>> read)
        {
            try { return read(); }
            catch (Exception error) when (Theme.IsFileError(error) || error is System.Xml.XmlException ||
                error is System.Security.SecurityException || error is ArgumentException)
            {
                status.Text = "Не удалось прочитать подключения. " + error.Message;
                return null;
            }
        }

        private void ChooseFile()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Файл подключений",
                Filter = "mRemoteNG (*.xml)|*.xml|MobaXterm (*.mxtsessions, MobaXterm.ini)|*.mxtsessions;*.ini|Все файлы|*.*"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ShowFound(SafeRead(() => Importers.Read(dialog.FileName)));
            }
        }

        private void ShowFound(List<FoundConnection> found)
        {
            if (found == null) return;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var item in found)
                    list.Items.Add(new ListViewItem(new[] { item.Rdp ? "RDP" : "SSH", item.Name, item.Address, item.Source })
                    { Checked = true, Tag = item });
            }
            finally { list.EndUpdate(); }
            Commit.Enabled = list.CheckedItems.Count > 0;
            status.Text = found.Count == 0 ? "Подключения не найдены." : "Найдено подключений: " + found.Count;
        }
    }

    /// <summary>«Новая команда» for the session's «Команды» menu.</summary>
    internal sealed class SnippetDialog : FormDialog
    {
        private readonly TextBox name, command;
        public Snippet Result { get; private set; }

        public SnippetDialog() : base("Новая команда", new Size(520, 270), "Сохранить", 110)
        {
            name = AddField("Название", "", 80);
            command = new TextBox { AccessibleName = "Команда", Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
                MaxLength = 4000, Font = new Font("Consolas", 10) };
            AddRow("Команда", Theme.Frame(command, new Padding(0, 4, 0, 4)), 120);
            var hint = new Label { AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left,
                Text = "Каждая строка отправляется в терминал с Enter." };
            AddRow(null, hint, 24);
            Finish();
            ActiveControl = name;
        }

        protected override void Save() { Result = new Snippet(name.Text, command.Text.TrimEnd('\r', '\n')); }
    }
}
