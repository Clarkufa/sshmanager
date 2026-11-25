using System;
using System.Drawing;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class RdpDialog : ThemedForm
    {
        public bool ImportRequested { get; private set; }

        public RdpDialog(RdpProfile profile, string groupId, Action<RdpProfile> save)
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
            ClientSize = new Size(545, 386);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 10, Padding = new Padding(18, 14, 18, 12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(layout);

            var name = Field(layout, 0, "Название", profile?.Name, 80);
            var host = Field(layout, 1, "Адрес", profile?.Host, 253);
            var port = Field(layout, 2, "Порт", (profile?.Port ?? 3389).ToString(), 5);
            var user = Field(layout, 3, "Пользователь", profile?.User, 128);
            var domain = Field(layout, 4, "Домен", profile?.Domain, 128);
            var width = Field(layout, 5, "Ширина экрана", profile?.Width > 0 ? profile.Width.ToString() : "", 4);
            var height = Field(layout, 6, "Высота экрана", profile?.Height > 0 ? profile.Height.ToString() : "", 4);
            var clipboard = new CheckBox { Text = "Общий буфер обмена", Checked = profile?.Clipboard == true, AutoSize = true, Anchor = AnchorStyles.Left };
            layout.Controls.Add(clipboard, 1, 7);
            var fullScreen = new CheckBox { Text = "Полноэкранный режим", Checked = profile?.FullScreen == true, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left };
            layout.Controls.Add(fullScreen, 1, 8);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var cancel = Theme.Button("Отмена", 90);
            cancel.DialogResult = DialogResult.Cancel;
            var commit = Theme.Button("Сохранить", 116, true);
            commit.Click += (sender, args) =>
            {
                try
                {
                    save(new RdpProfile(profile?.Id ?? Guid.NewGuid().ToString("N"), name.Text, host.Text, Number(port, 3389),
                        user.Text, domain.Text, profile == null ? groupId : profile.GroupId, Number(width, 0), Number(height, 0),
                        clipboard.Checked, fullScreen.Checked));
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
            layout.Controls.Add(buttons, 0, 9);
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
}
