using System;
using System.Drawing;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class GroupDialog : NameDialog
    {
        public GroupDialog(string existingName, Action<string> save)
            : base(existingName == null ? "Новая вкладка" : "Переименовать вкладку", "Название вкладки",
                existingName, existingName == null ? "Создать" : "Сохранить", save) { }
    }

    internal class NameDialog : ThemedForm
    {
        public NameDialog(string title, string labelText, string value, string commitText, Action<string> save)
        {
            Text = title;
            Font = Theme.Body;
            BackColor = Theme.Canvas;
            ForeColor = Theme.Ink;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            ClientSize = new Size(410, 150);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 14), ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            var label = new Label { Text = labelText, AutoSize = true, Font = Theme.Emphasis, Margin = new Padding(0) };
            var name = new TextBox { AccessibleName = labelText, Text = value ?? "", Dock = DockStyle.Fill, MaxLength = 32, Margin = new Padding(0) };
            layout.Controls.Add(label, 0, 0);
            layout.Controls.Add(Theme.Frame(name, new Padding(0, 0, 0, 4)), 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
            var commit = Theme.Button(commitText, 108, true);
            var cancel = Theme.Button("Отмена", 88);
            cancel.DialogResult = DialogResult.Cancel;
            commit.Click += (sender, args) =>
            {
                try
                {
                    save(name.Text.Trim());
                    DialogResult = DialogResult.OK;
                }
                catch (Exception ex) when (Theme.IsFileError(ex) || ex is ArgumentException || ex is InvalidOperationException)
                {
                    Theme.ShowError(this, ex);
                    name.SelectAll();
                    name.Focus();
                }
            };
            actions.Controls.AddRange(new Control[] { commit, cancel });
            layout.Controls.Add(actions, 0, 2);
            Controls.Add(layout);
            AcceptButton = commit;
            CancelButton = cancel;
            ActiveControl = name;
            name.SelectAll();
        }
    }
}
