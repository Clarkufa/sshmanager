using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class ConnectionDialog : ThemedForm
    {
        private readonly string sshDirectory;
        private readonly TextBox alias;
        private readonly TextBox host;
        private readonly TextBox user;
        private readonly TextBox port;
        private readonly TextBox password;
        private readonly ComboBox available;
        private readonly ListBox assigned;
        private readonly Button addKey;
        private readonly Button replaceKey;
        private readonly Button removeKey;
        private readonly TextBox proxyJump, knownHosts;
        private readonly ListBox forwards;
        private readonly ComboBox forwardKind;
        private readonly TextBox forwardValue;
        private static readonly string[] ForwardTitles = { "L — локальный", "R — удалённый", "D — SOCKS" };

        public ConnectionDialog(string sshDirectory, Connection connection, bool hasPassword, Action<ConnectionValues, string, bool> save,
            Preferences defaults = null)
        {
            this.sshDirectory = sshDirectory;
            Text = connection == null ? "Новое подключение" : "Изменить подключение";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(724, 716);
            MinimumSize = new Size(720, 739);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 16, Padding = new Padding(22, 14, 22, 14) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            Controls.Add(layout);

            alias = Field(layout, 0, "&Псевдоним", connection == null ? "" : connection.Alias);
            host = Field(layout, 1, "&Хост", connection == null ? "" : connection.Get("hostname"));
            user = Field(layout, 2, "По&льзователь", connection == null ? defaults?.DefaultUser ?? "" : connection.Get("user"));
            port = Field(layout, 3, "П&орт", connection == null ? defaults?.DefaultPort ?? "" : connection.Get("port"));
            password = Field(layout, 4, "Па&роль", "");
            password.UseSystemPasswordChar = true;
            password.MaxLength = SshPasswords.MaxBytes;
            var passwordOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            var forgetPassword = new CheckBox { Text = "Удалить пароль", AutoSize = true, Visible = hasPassword,
                Margin = new Padding(16, 0, 0, 0) };
            var passwordHint = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 2, 0, 0) };
            var existingTarget = PasswordTarget.From(connection);
            void UpdatePasswordHint()
            {
                var target = PasswordTarget.From(alias.Text.Trim(), host.Text.Trim(), user.Text.Trim(), port.Text.Trim());
                passwordHint.Text = !hasPassword ? "Пароль необязателен. Хранится в Windows." :
                    existingTarget.SameDestination(target) ? "Пароль сохранён. Пустое поле оставит прежний." :
                    "Для нового адреса, порта или пользователя введите пароль заново.";
                bool canForget = hasPassword && existingTarget.SameDestination(target);
                if (!canForget) forgetPassword.Checked = false;
                forgetPassword.Visible = canForget;
            }
            foreach (var field in new[] { alias, host, user, port }) field.TextChanged += (sender, args) => UpdatePasswordHint();
            forgetPassword.CheckedChanged += (sender, args) => password.Enabled = !forgetPassword.Checked;
            passwordOptions.Controls.AddRange(new Control[] { passwordHint, forgetPassword });
            layout.Controls.Add(passwordOptions, 1, 5);
            UpdatePasswordHint();
            var hint = new Label
            {
                Text = "Пустые поля используют настройки OpenSSH.", AutoSize = true, Anchor = AnchorStyles.Left,
                ForeColor = Theme.Muted, Margin = new Padding(0)
            };
            layout.Controls.Add(hint, 1, 6);

            proxyJump = Field(layout, 7, "Через сервер", connection == null ? "" : connection.Get("proxyjump"));
            proxyJump.AccessibleName = "Через сервер (ProxyJump)";
            Theme.SetCue(proxyJump, "ProxyJump: bastion или user@host:port");
            knownHosts = Field(layout, 8, "Файл known_hosts", connection == null ? "" : connection.Get("userknownhostsfile"));
            knownHosts.AccessibleName = "Файл known_hosts (UserKnownHostsFile)";
            Theme.SetCue(knownHosts, "~/.ssh/known_hosts (по умолчанию)");
            LabelFor(layout, 9, "Пробросы портов");
            forwards = new ListBox { AccessibleName = "Пробросы портов", Dock = DockStyle.Fill, IntegralHeight = false, ForeColor = Theme.Ink };
            if (connection != null) forwards.Items.AddRange(connection.Forwards.Cast<object>().ToArray());
            layout.Controls.Add(Theme.Frame(forwards, new Padding(0, 1, 0, 3)), 1, 9);
            var forwardRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0) };
            forwardRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            forwardRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            forwardRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            forwardRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            forwardKind = new ComboBox { AccessibleName = "Тип проброса", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Scaled(this, 19), Margin = new Padding(0, 5, 6, 0) };
            forwardKind.DrawItem += Theme.DrawChoice;
            forwardKind.Items.AddRange(ForwardTitles);
            forwardKind.SelectedIndex = 0;
            forwardValue = new TextBox { AccessibleName = "Адрес проброса" };
            Theme.SetCue(forwardValue, "8080 localhost:80");
            forwardKind.SelectedIndexChanged += (sender, args) => Theme.SetCue(forwardValue, forwardKind.SelectedIndex == 2 ? "1080" : "8080 localhost:80");
            var addForward = Theme.Button("Добавить", 104, glyph: Glyph.Plus);
            var removeForward = Theme.Button("Убрать", 88, glyph: Glyph.Delete);
            addForward.Click += (sender, args) => AddForward();
            removeForward.Click += (sender, args) =>
            {
                int index = forwards.SelectedIndex;
                if (index >= 0) forwards.Items.RemoveAt(index);
                if (forwards.Items.Count > 0) forwards.SelectedIndex = Math.Min(index, forwards.Items.Count - 1);
            };
            forwardValue.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                AddForward();
            };
            forwardRow.Controls.Add(forwardKind, 0, 0);
            forwardRow.Controls.Add(Theme.Frame(forwardValue, new Padding(0, 4, 0, 4)), 1, 0);
            forwardRow.Controls.Add(addForward, 2, 0);
            forwardRow.Controls.Add(removeForward, 3, 0);
            layout.Controls.Add(forwardRow, 1, 10);

            LabelFor(layout, 11, "&Доступные ключи");
            var picker = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
            picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            picker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.Controls.Add(picker, 1, 11);
            available = new ComboBox
            {
                AccessibleName = "Доступные ключи", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 5, 0, 0), MaxDropDownItems = 12,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Scaled(this, 19)
            };
            available.DrawItem += Theme.DrawChoice;
            picker.Controls.Add(available, 0, 0);
            var browse = Theme.Button("Обзор…", 100, glyph: Glyph.Browse);
            browse.Click += (sender, args) => Browse();
            picker.Controls.Add(browse, 1, 0);

            LabelFor(layout, 12, "&Выбранные ключи");
            assigned = new ListBox
            {
                AccessibleName = "Выбранные ключи", Dock = DockStyle.Fill, IntegralHeight = false,
                HorizontalScrollbar = true, ForeColor = Theme.Ink
            };
            if (connection != null) assigned.Items.AddRange(connection.Keys.Cast<object>().ToArray());
            layout.Controls.Add(Theme.Frame(assigned, new Padding(0, 1, 0, 3)), 1, 12);

            var keyActions = new FlowLayoutPanel { WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            addKey = Theme.Button("Добавить ключ", 142, glyph: Glyph.Key);
            addKey.Margin = new Padding(0, 1, 0, 0);
            replaceKey = Theme.Button("Заменить", 106, glyph: Glyph.Edit);
            removeKey = Theme.Button("Убрать", 88, glyph: Glyph.Delete);
            addKey.Click += (sender, args) => ApplyKey(false);
            replaceKey.Click += (sender, args) => ApplyKey(true);
            removeKey.Click += (sender, args) =>
            {
                int index = assigned.SelectedIndex;
                if (index < 0) return;
                assigned.Items.RemoveAt(index);
                assigned.SelectedIndex = Math.Min(index, assigned.Items.Count - 1);
                UpdateKeyActions();
            };
            assigned.SelectedIndexChanged += (sender, args) => UpdateKeyActions();
            available.SelectedIndexChanged += (sender, args) => UpdateKeyActions();
            var createKey = Theme.Button("Создать ключ…", 140, glyph: Glyph.Plus);
            createKey.Click += (sender, args) => CreateKey();
            keyActions.Controls.AddRange(new Control[] { addKey, replaceKey, removeKey, createKey });
            layout.Controls.Add(keyActions, 1, 13);

            var keyHint = new Label { AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            layout.Controls.Add(keyHint, 1, 14);
            try
            {
                foreach (string path in KeyFiles.Find(sshDirectory)) available.Items.Add(path);
                keyHint.Text = available.Items.Count == 0 ? "В папке .ssh ключи не найдены. Можно указать файл через «Обзор»." : "Ключи найдены в папке .ssh.";
            }
            catch (Exception ex) when (Theme.IsFileError(ex))
            {
                keyHint.Text = "Не удалось прочитать папку .ssh. Можно выбрать файл вручную.";
            }
            if (available.Items.Count > 0) available.SelectedIndex = 0;
            if (assigned.Items.Count > 0) assigned.SelectedIndex = 0;
            UpdateKeyActions();

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 7, 0, 0) };
            var commit = Theme.Button("Сохранить", 113, true);
            var cancel = Theme.Button("Отмена", 92);
            cancel.DialogResult = DialogResult.Cancel;
            commit.Click += (sender, args) =>
            {
                try
                {
                    save(new ConnectionValues
                    {
                        Alias = Alias, HostName = host.Text.Trim(), User = user.Text.Trim(), Port = port.Text.Trim(),
                        Keys = assigned.Items.Cast<Identity>().ToList(), ProxyJump = proxyJump.Text.Trim(), KnownHostsFile = knownHosts.Text.Trim(),
                        Forwards = forwards.Items.Cast<string>().ToList()
                    }, forgetPassword.Checked ? "" : password.Text, forgetPassword.Checked);
                    password.Clear();
                    DialogResult = DialogResult.OK;
                }
                catch (Exception ex) when (Theme.IsFileError(ex) || ex is ArgumentException) { Theme.ShowError(this, ex); }
            };
            actions.Controls.AddRange(new Control[] { commit, cancel });
            layout.Controls.Add(actions, 1, 15);
            AcceptButton = commit;
            CancelButton = cancel;
            ActiveControl = alias;
            Theme.ScaleToDpi(this);
            ResumeLayout();
        }

        public string Alias { get { return alias.Text.Trim(); } }

        private void AddForward()
        {
            var value = System.Text.RegularExpressions.Regex.Replace(forwardValue.Text.Trim(), @"\s+", " ");
            if (value.Length == 0) return;
            var forward = ConfigDocument.ForwardNames[Math.Max(0, forwardKind.SelectedIndex)] + " " + value;
            if (!ConfigDocument.IsForward(forward))
            {
                Theme.ShowError(this, new ArgumentException(forwardKind.SelectedIndex == 2
                    ? "Для SOCKS-проброса укажите локальный порт, например 1080."
                    : "Укажите проброс как «локальный_порт хост:порт», например 8080 localhost:80."));
                return;
            }
            if (!forwards.Items.Contains(forward)) forwards.Items.Add(forward);
            forwards.SelectedItem = forward;
            forwardValue.Clear();
        }

        /// <summary>«Создать ключ…»: ssh-keygen ed25519 into the .ssh folder, then selects the new key.</summary>
        private void CreateKey()
        {
            string created = null;
            string suggested = Alias.Length > 0 && ConfigDocument.IsAlias(Alias) ? "id_ed25519_" + Alias : "id_ed25519";
            using (var dialog = new NameDialog("Создать ключ", "Имя файла в папке .ssh", suggested, "Создать",
                name => created = SshKeys.Generate(sshDirectory, name), 64))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || created == null) return;
            }
            string path = KeyFiles.ConfigPath(sshDirectory, created);
            int index = available.Items.IndexOf(path);
            if (index < 0) index = available.Items.Add(path);
            available.SelectedIndex = index;
            ApplyKey(false);
            MessageBox.Show(this, "Ключ создан: " + created + "\n\nПубличную часть можно скопировать на сервер через меню строки «Скопировать ключ на сервер».",
                "Создать ключ", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Browse()
        {
            using (var dialog = new OpenFileDialog { Title = "Выбрать файл SSH-ключа", Filter = "Все файлы|*.*", CheckFileExists = true })
            {
                if (Directory.Exists(sshDirectory)) dialog.InitialDirectory = sshDirectory;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string path = KeyFiles.ConfigPath(sshDirectory, dialog.FileName);
                int index = available.Items.IndexOf(path);
                if (index < 0) index = available.Items.Add(path);
                available.SelectedIndex = index;
                addKey.Focus();
            }
        }

        private void ApplyKey(bool replace)
        {
            var value = available.SelectedItem as string;
            if (value == null) return;
            int index = replace ? assigned.SelectedIndex : -1;
            var duplicate = assigned.Items.Cast<Identity>().Any(key => key.Value == value);
            if (duplicate) return;

            if (index < 0) index = assigned.Items.Add(new Identity(value));
            else assigned.Items[index] = new Identity(value, ((Identity)assigned.Items[index]).LineIndex);
            assigned.SelectedIndex = index;
            UpdateKeyActions();
        }

        private void UpdateKeyActions()
        {
            var value = available.SelectedItem as string;
            bool canAssign = value != null && assigned.Items.Cast<Identity>().All(key => key.Value != value);
            addKey.Enabled = canAssign;
            replaceKey.Enabled = canAssign && assigned.SelectedIndex >= 0;
            removeKey.Enabled = assigned.SelectedIndex >= 0;
        }

        private static void LabelFor(TableLayoutPanel layout, int row, string label)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left }, 0, row);
        }

        private static TextBox Field(TableLayoutPanel layout, int row, string label, string value)
        {
            LabelFor(layout, row, label);
            var field = new TextBox { Text = value, AccessibleName = label.Replace("&", "") };
            layout.Controls.Add(Theme.Frame(field, new Padding(0, 4, 0, 4)), 1, row);
            return field;
        }
    }

    internal static class KeyFiles
    {
        public static IEnumerable<string> Find(string directory)
        {
            if (!Directory.Exists(directory)) return new string[0];
            return BackupPaths.LocalFiles(directory).Where(IsPrivateKey).Select(path => ConfigPath(directory, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static string ConfigPath(string directory, string file)
        {
            string relative = BackupPaths.RelativeTo(directory, file);
            string path = relative == null ? file.Replace('\\', '/') : "~/.ssh/" + relative;
            return path.Replace("%", "%%");
        }

        internal static bool IsPrivateKey(string path)
        {
            return IsPrivateHeader(ReadHeader(path));
        }

        internal static bool IsKey(string path)
        {
            var header = ReadHeader(path);
            return IsPrivateHeader(header) || System.Text.RegularExpressions.Regex.IsMatch(header,
                @"\A(?:ssh-|ecdsa-sha2-|sk-)[^ \t]+[ \t]+[A-Za-z0-9+/]{16,}");
        }

        private static bool IsPrivateHeader(string header)
        {
            return header.StartsWith("-----BEGIN ", StringComparison.Ordinal) && header.EndsWith("PRIVATE KEY-----", StringComparison.Ordinal);
        }

        private static string ReadHeader(string path)
        {
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[160];
                    int count = stream.Read(buffer, 0, buffer.Length);
                    var content = System.Text.Encoding.ASCII.GetString(buffer, 0, count);
                    int end = content.IndexOfAny(new[] { '\r', '\n' });
                    return end < 0 ? content : content.Substring(0, end);
                }
            }
            catch (Exception ex) when (Theme.IsFileError(ex)) { return ""; }
        }
    }
}
