using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace SshManager
{
    /// <summary>«Новое VNC-подключение» / «Изменить VNC-подключение» (docs/ui-spec.md §8).</summary>
    internal sealed class VncDialog : FormDialog
    {
        private const string Direct = "Напрямую", NoFiles = "Не выбран";
        private readonly VncProfile profile;
        private readonly string groupId;
        private readonly Action<VncProfile, string, bool> save;
        private readonly TextBox name, host, port, user, password;
        private readonly CheckBox forgetPassword, viewOnly, clipboard, fullScreen;
        private readonly ComboBox via, filesVia, scale;

        public VncDialog(VncProfile profile, string groupId, bool hasPassword, IEnumerable<string> sshAliases,
            Action<VncProfile, string, bool> save)
            : base(profile == null ? "Новое VNC-подключение" : "Изменить VNC-подключение", new Size(565, 534), "Сохранить", 170)
        {
            this.profile = profile;
            this.groupId = groupId;
            this.save = save;
            name = AddField("Название", profile?.Name, 80);
            host = AddField("Адрес", profile?.Host, 253);
            port = AddField("Порт", (profile?.Port ?? VncProfile.DefaultPort).ToString(CultureInfo.InvariantCulture), 5);
            user = AddField("Пользователь", profile?.User, 128, "необязательно");
            password = AddField("Пароль", "", SshPasswords.MaxBytes);
            password.UseSystemPasswordChar = true;
            var passwordOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            passwordOptions.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 6, 12, 0),
                Text = hasPassword ? "Сохранён. Пустое поле — не менять." : "Необязательно. Хранится в Windows." });
            forgetPassword = new CheckBox { Text = "Удалить пароль", AutoSize = true, Visible = hasPassword, Margin = new Padding(0, 4, 0, 0) };
            forgetPassword.CheckedChanged += (sender, args) => password.Enabled = !forgetPassword.Checked;
            passwordOptions.Controls.Add(forgetPassword);
            AddRow(null, passwordOptions, 30);
            AddRow(null, new Label { AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left,
                Text = "VNC-пароль: учитываются первые 8 символов." }, 24);

            var aliases = sshAliases.OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase).ToArray();
            via = SshChoice("Через SSH-сервер", Direct, profile?.Via, aliases);
            filesVia = SshChoice("SSH-профиль для файлов", NoFiles, profile?.FilesVia, aliases);
            scale = Choice("Масштаб");
            scale.Items.AddRange(new object[] { "По размеру окна", "Исходный размер" });
            scale.SelectedIndex = profile?.Scale == VncProfile.NativeScale ? 1 : 0;
            viewOnly = AddCheck("Только просмотр", profile?.ViewOnly == true);
            clipboard = AddCheck("Общий буфер обмена", profile?.Clipboard ?? true);
            fullScreen = AddCheck("Открывать на весь экран", profile?.FullScreen == true);
            Finish();
            ActiveControl = name;
        }

        private ComboBox SshChoice(string label, string empty, string current, string[] aliases)
        {
            var box = Choice(label);
            box.Items.Add(empty);
            box.Items.AddRange(aliases);
            if (!String.IsNullOrEmpty(current) && !box.Items.Contains(current)) box.Items.Add(current);
            box.SelectedItem = String.IsNullOrEmpty(current) ? empty : current;
            return box;
        }

        private ComboBox Choice(string label)
        {
            var box = new ComboBox { AccessibleName = label, Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Scaled(this, 19), Margin = new Padding(0, 4, 0, 0), MaxDropDownItems = 14 };
            box.DrawItem += Theme.DrawChoice;
            AddRow(label, box, 34);
            return box;
        }

        protected override void Save()
        {
            if (!Int32.TryParse(port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException("Укажите порт от 1 до 65535.");
            if (password.Text.Length > 0) SshPasswords.Encode(password.Text);
            string chosenVia = (string)via.SelectedItem == Direct ? "" : (string)via.SelectedItem;
            var result = new VncProfile(profile?.Id ?? Guid.NewGuid().ToString("N"), name.Text, host.Text, number, user.Text,
                profile == null ? groupId : profile.GroupId, scale.SelectedIndex == 1 ? VncProfile.NativeScale : VncProfile.FitScale,
                viewOnly.Checked, clipboard.Checked, fullScreen.Checked, chosenVia, filesVia.SelectedIndex == 0 ? "" : (string)filesVia.SelectedItem);
            save(result, forgetPassword.Checked ? "" : password.Text, forgetPassword.Checked);
            password.Clear();
        }
    }

    /// <summary>«Вход: имя» when the VNC server asks for a password that is not saved (or was rejected).</summary>
    internal sealed class VncLogonDialog : FormDialog
    {
        private readonly VncProfile profile;
        private readonly TextBox password;
        private readonly CheckBox remember;
        public string Password { get; private set; }

        private VncLogonDialog(VncProfile profile, bool wrongPassword) : base("Вход: " + profile.Name, new Size(460, 214), "Подключиться", 120)
        {
            this.profile = profile;
            AddRow(null, new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = wrongPassword ? Theme.Danger : Theme.Muted,
                Text = wrongPassword ? "Неверный пароль VNC. Попробуйте ещё раз." : profile.Address }, 28);
            password = AddField("Пароль", "", SshPasswords.MaxBytes);
            password.UseSystemPasswordChar = true;
            remember = AddCheck("Сохранить пароль в Windows", false);
            Finish();
            ActiveControl = password;
        }

        protected override void Save()
        {
            if (password.Text.Length == 0) throw new ArgumentException("Введите пароль.");
            SshPasswords.Encode(password.Text);
            Password = password.Text;
            if (remember.Checked) VncPasswords.Set(profile, Password);
        }

        /// <summary>The entered password, or null when the user cancels.</summary>
        public static string Ask(IWin32Window owner, VncProfile profile, bool wrongPassword)
        {
            using (var dialog = new VncLogonDialog(profile, wrongPassword))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Password : null;
        }
    }
}
