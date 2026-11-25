using System;
using System.Drawing;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class RdpDialog : ThemedForm
    {
        public bool ImportRequested { get; private set; }

        public RdpDialog(RdpProfile profile, string groupId, bool hasPassword, Action<RdpProfile, string, bool> save)
        {
            Text = profile == null ? "Новое RDP-подключение" : "Изменить RDP-подключение";
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = MinimizeBox = MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(545, 450);
            string id = profile?.Id ?? Guid.NewGuid().ToString("N");

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 12, Padding = new Padding(18, 14, 18, 12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 10; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(layout);

            var name = Field(layout, 0, "Название", profile?.Name, 80);
            var host = Field(layout, 1, "Адрес", profile?.Host, 253);
            var port = Field(layout, 2, "Порт", (profile?.Port ?? 3389).ToString(), 5);
            var user = Field(layout, 3, "Пользователь", profile?.User, 128);
            var domain = Field(layout, 4, "Домен", profile?.Domain, 128);
            var password = Field(layout, 5, "Па&роль", "", SshPasswords.MaxBytes);
            password.UseSystemPasswordChar = true;
            var passwordOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var passwordHint = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 6, 12, 0),
                Text = hasPassword ? "Сохранён. Пустое поле — не менять." : "Необязательно. Хранится в Windows." };
            var forgetPassword = new CheckBox { Text = "Удалить пароль", AutoSize = true, Visible = hasPassword, Margin = new Padding(0, 4, 0, 0) };
            forgetPassword.CheckedChanged += (sender, args) => password.Enabled = !forgetPassword.Checked;
            passwordOptions.Controls.AddRange(new Control[] { passwordHint, forgetPassword });
            layout.Controls.Add(passwordOptions, 1, 6);
            var width = Field(layout, 7, "Ширина экрана", profile?.Width > 0 ? profile.Width.ToString() : "", 4);
            var height = Field(layout, 8, "Высота экрана", profile?.Height > 0 ? profile.Height.ToString() : "", 4);
            foreach (var size in new[] { width, height })
                size.AccessibleDescription = "Оставьте пустым, чтобы разрешение подстраивалось под размер окна.";
            var clipboard = new CheckBox { Text = "Общий буфер обмена", Checked = profile?.Clipboard == true, AutoSize = true, Anchor = AnchorStyles.Left };
            layout.Controls.Add(clipboard, 1, 9);
            var fullScreen = new CheckBox { Text = "Открывать на весь экран", Checked = profile?.FullScreen == true, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left };
            layout.Controls.Add(fullScreen, 1, 10);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var cancel = Theme.Button("Отмена", 90);
            cancel.DialogResult = DialogResult.Cancel;
            var commit = Theme.Button("Сохранить", 116, true);
            commit.Click += (sender, args) =>
            {
                try
                {
                    if (password.Text.Length > 0) SshPasswords.Encode(password.Text);
                    if (password.Text.Length > 0 && user.Text.Trim().Length == 0)
                        throw new ArgumentException("Укажите пользователя, чтобы сохранить пароль RDP.");
                    save(new RdpProfile(id, name.Text, host.Text, Number(port, 3389),
                        user.Text, domain.Text, profile == null ? groupId : profile.GroupId, Number(width, 0), Number(height, 0),
                        clipboard.Checked, fullScreen.Checked), forgetPassword.Checked ? "" : password.Text, forgetPassword.Checked);
                    password.Clear();
                    DialogResult = DialogResult.OK;
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || Theme.IsFileError(error))
                { Theme.ShowError(this, error); }
            };
            buttons.Controls.AddRange(new Control[] { commit, cancel });
            if (profile == null)
            {
                var fromFile = Theme.Button("Импорт .rdp…", 136, glyph: Glyph.Folder);
                fromFile.Click += (sender, args) => { ImportRequested = true; DialogResult = DialogResult.Cancel; };
                buttons.Controls.Add(fromFile);
            }
            layout.Controls.Add(buttons, 0, 11);
            layout.SetColumnSpan(buttons, 2);
            AcceptButton = commit;
            CancelButton = cancel;
        }

        private static int Number(TextBox input, int fallback)
        {
            var text = input.Text.Trim();
            if (text.Length == 0) return fallback;
            if (Int32.TryParse(text, out var number)) return number;
            throw new ArgumentException("Укажите числовой порт и размеры экрана.");
        }

        private static TextBox Field(TableLayoutPanel layout, int row, string label, string value, int limit)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            var field = new TextBox { AccessibleName = label, Text = value ?? "", MaxLength = limit,
                Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Ink };
            layout.Controls.Add(field, 1, row);
            return field;
        }
    }
    internal sealed class RdpLogonDialog : ThemedForm
    {
        private readonly TextBox account, password;
        private readonly CheckBox remember;

        private RdpLogonDialog(RdpProfile profile)
        {
            Text = "Вход: " + profile.Name;
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = MinimizeBox = MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(460, 214);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(18, 14, 18, 12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var target = new Label { Text = profile.Address, AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left };
            layout.Controls.Add(target, 0, 0);
            layout.SetColumnSpan(target, 2);
            account = AddField(layout, 1, "Пользователь", profile.Account);
            password = AddField(layout, 2, "Па&роль", "");
            password.UseSystemPasswordChar = true;
            password.MaxLength = SshPasswords.MaxBytes;
            remember = new CheckBox { Text = "Сохранить пароль в Windows", AutoSize = true, Anchor = AnchorStyles.Left };
            layout.Controls.Add(remember, 1, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var cancel = Theme.Button("Отмена", 90);
            cancel.DialogResult = DialogResult.Cancel;
            var commit = Theme.Button("Подключиться", 136, true);
            commit.Click += (sender, args) =>
            {
                try
                {
                    if (password.Text.Length > 0) SshPasswords.Encode(password.Text);
                    DialogResult = DialogResult.OK;
                }
                catch (ArgumentException error) { Theme.ShowError(this, error); }
            };
            buttons.Controls.AddRange(new Control[] { commit, cancel });
            layout.Controls.Add(buttons, 0, 4);
            layout.SetColumnSpan(buttons, 2);
            AcceptButton = commit;
            CancelButton = cancel;
            Shown += (sender, args) => (account.Text.Length == 0 ? account : password).Focus();
        }

        private static TextBox AddField(TableLayoutPanel layout, int row, string label, string value)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            var field = new TextBox { AccessibleName = label.Replace("&", ""), Text = value ?? "", MaxLength = 257,
                Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Ink };
            layout.Controls.Add(field, 1, row);
            return field;
        }

        /// <summary>Returns the saved credential, asks for one when none is saved, or null when the user cancels.</summary>
        public static RdpCredential Resolve(IWin32Window owner, RdpProfile profile)
        {
            string saved;
            try { saved = RdpPasswords.Read(profile); }
            catch (System.IO.IOException error) { Theme.ShowError(owner, error); saved = null; }
            // A saved password always goes with the user and domain of this exact profile (host and port).
            if (saved != null && profile.User.Length > 0) return new RdpCredential(profile.User, profile.Domain, saved);

            using (var dialog = new RdpLogonDialog(profile))
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
                var credential = RdpCredential.FromAccount(dialog.account.Text, profile.Domain, dialog.password.Text);
                dialog.password.Clear();
                if (dialog.remember.Checked && credential.Password.Length > 0)
                {
                    if (credential.User != profile.User || credential.Domain != profile.Domain)
                        Theme.ShowError(owner, new ArgumentException("Пароль сохраняется только для пользователя из настроек подключения. " +
                            "Измените пользователя в профиле, чтобы запомнить пароль для другой учётной записи."));
                    else
                    {
                        try { RdpPasswords.Set(profile, credential.Password); }
                        catch (System.IO.IOException error) { Theme.ShowError(owner, error); }
                    }
                }
                return credential;
            }
        }
    }
}
