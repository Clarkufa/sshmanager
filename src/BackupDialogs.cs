using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace SshManager
{
    internal sealed class BackupCommands
    {
        private const string Filter = "Защищённая копия (*.sshbackup)|*.sshbackup|Архив ZIP (*.zip)|*.zip";
        private static readonly CatalogKind[] BackupProtocols = { CatalogKind.Ssh, CatalogKind.Rdp, CatalogKind.Vnc };
        private readonly Form owner;
        private readonly string sshDirectory, settingsPath;
        private readonly Action reload;

        public BackupCommands(Form owner, string sshDirectory, string settingsPath, Action reload)
        {
            this.owner = owner;
            this.sshDirectory = sshDirectory;
            this.settingsPath = settingsPath;
            this.reload = reload;
        }

        public async Task Export()
        {
            string path;
            using (var dialog = new SaveFileDialog { Title = "Экспорт резервной копии", Filter = Filter,
                FileName = "ssh-manager-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), AddExtension = true, OverwritePrompt = true })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return;
                path = dialog.FileName;
            }
            var extension = Path.GetExtension(path);
            bool protect = extension.Equals(".sshbackup", StringComparison.OrdinalIgnoreCase);
            if (!protect && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Укажите расширение .sshbackup или .zip.");

            string password = null;
            if (protect)
            {
                using (var dialog = new BackupPasswordDialog(creating: true))
                {
                    if (dialog.ShowDialog(owner) != DialogResult.OK) return;
                    password = dialog.Password;
                }
            }
            int passwordCount = 0, desktopPasswordCount = 0, vncPasswordCount = 0;
            var snapshot = await Task.Run(() =>
            {
                var copy = new BackupSnapshot(sshDirectory, settingsPath);
                try
                {
                    if (protect)
                    {
                        passwordCount = copy.AddPasswords();
                        desktopPasswordCount = copy.AddDesktopPasswords(out vncPasswordCount);
                    }
                    BackupArchive.Write(path, copy.Files, password);
                    return copy;
                }
                finally { BackupSnapshot.ClearPasswords(copy.Files); }
            });
            string result = "Резервная копия сохранена.\n\n" + path + "\n\nФайлов ключей и сертификатов: " + snapshot.KeyCount;
            if (snapshot.OmittedCount > 0) result += "\nСсылок на файлы, которые не удалось включить: " + snapshot.OmittedCount;
            result += protect ? "\nСохранённых паролей SSH: " + passwordCount + ", RDP: " + desktopPasswordCount + ", VNC: " + vncPasswordCount
                : "\nZIP сохраняет подключения без паролей SSH, RDP и VNC.";
            if (!owner.IsDisposed) MessageBox.Show(owner, result, "Экспорт резервной копии", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public async Task Import()
        {
            string path;
            using (var dialog = new OpenFileDialog { Title = "Импорт резервной копии", Filter = Filter,
                CheckFileExists = true, Multiselect = false })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return;
                path = dialog.FileName;
            }

            string password = null;
            if (await Task.Run(() => BackupArchive.IsProtected(path)))
            {
                if (owner.IsDisposed) return;
                using (var dialog = new BackupPasswordDialog(creating: false))
                {
                    if (dialog.ShowDialog(owner) != DialogResult.OK) return;
                    password = dialog.Password;
                }
            }
            using (var plan = await Task.Run(() =>
            {
                var entries = BackupArchive.Read(path, password);
                try { return new BackupRestore(entries, sshDirectory, settingsPath); }
                finally { BackupSnapshot.ClearPasswords(entries); }
            }))
            {
                if (owner.IsDisposed) return;
                if (plan.ChangeCount == 0 && plan.PasswordCount == 0)
                {
                    MessageBox.Show(owner, "Данные из резервной копии уже восстановлены.", "Импорт резервной копии", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var summary = "Импортировать резервную копию?\n\nНовых файлов: " + (plan.ChangeCount - plan.ReplaceCount) +
                    "\nЗаменяемых файлов: " + plan.ReplaceCount;
                foreach (var kind in BackupProtocols)
                {
                    int count = plan.CountPasswords(kind);
                    if (count == 0) continue;
                    int replaced = plan.CountReplacedPasswords(kind);
                    string protocol = kind.ToString().ToUpperInvariant();
                    summary += "\nНовых " + protocol + "-паролей: " + (count - replaced) +
                        "\nЗаменяемых " + protocol + "-паролей: " + replaced;
                }
                if (plan.ReplaceCount > 0)
                {
                    summary += "\n\nПредыдущие версии этих файлов будут сохранены:\n" + String.Join("\n", plan.Replacements.Take(7));
                    if (plan.ReplaceCount > 7) summary += "\nИ ещё " + (plan.ReplaceCount - 7);
                }
                if (MessageBox.Show(owner, summary, "Импорт резервной копии", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

                var previous = await Task.Run(() => plan.Apply());
                if (owner.IsDisposed) return;
                reload();
                string result = "Резервная копия восстановлена.";
                if (plan.PasswordCount > 0)
                    result += "\nВосстановлено паролей " + String.Join(", ", BackupProtocols.Select(kind =>
                        kind.ToString().ToUpperInvariant() + ": " + plan.CountPasswords(kind)));
                if (previous.Count > 0) result += "\n\nПредыдущие файлы сохранены здесь:\n" + String.Join("\n", previous);
                MessageBox.Show(owner, result, "Импорт резервной копии", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        public static bool IsBackupError(Exception error)
        {
            return Theme.IsFileError(error) || error is InvalidDataException || error is CryptographicException ||
                error is ArgumentException || error is NotSupportedException || error is XmlException;
        }
    }

    internal sealed class BackupPasswordDialog : ThemedForm
    {
        private readonly TextBox password;

        public BackupPasswordDialog(bool creating)
        {
            Text = creating ? "Защита резервной копии" : "Пароль резервной копии";
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = MaximizeBox = MinimizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(460, creating ? 221 : 133);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 14, 20, 12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            password = AddField(layout, "Пароль");
            TextBox confirmation = creating ? AddField(layout, "Повторите пароль") : null;
            if (creating)
            {
                int row = layout.RowStyles.Count;
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                layout.Controls.Add(new Label { Text = "Защищённая копия включает сохранённые пароли SSH, RDP и VNC.",
                    AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0) }, 0, row);
            }
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var submit = Theme.Button(creating ? "Сохранить" : "Продолжить", 116, true);
            var cancel = Theme.Button("Отмена", 90);
            cancel.DialogResult = DialogResult.Cancel;
            submit.Click += (sender, args) =>
            {
                if (password.Text.Length == 0 || (confirmation != null && password.Text != confirmation.Text))
                {
                    var message = password.Text.Length == 0 ? "Введите пароль." : "Пароли не совпадают.";
                    MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.AddRange(new Control[] { submit, cancel });
            layout.Controls.Add(buttons, 0, layout.RowStyles.Count);
            AcceptButton = submit;
            CancelButton = cancel;
            ActiveControl = password;
            Theme.ScaleToDpi(this);
            ResumeLayout();
        }

        public string Password { get { return password.Text; } }

        private static TextBox AddField(TableLayoutPanel layout, string label)
        {
            int row = layout.RowStyles.Count;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0) }, 0, row);
            var field = new TextBox { AccessibleName = label, UseSystemPasswordChar = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            layout.Controls.Add(Theme.Frame(field, new Padding(0, 0, 0, 5)), 0, row + 1);
            return field;
        }
    }
}
