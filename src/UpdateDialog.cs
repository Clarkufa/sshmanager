using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class UpdateDialog : ThemedForm
    {
        public UpdateDialog(AppUpdate update)
        {
            Text = "Обновление SSH Manager";
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = MinimizeBox = MaximizeBox = false;
            ClientSize = new Size(460, 190);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18, 13, 17, 13) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            var title = new Label { Text = "Доступна версия " + update.Version.ToString(3), AutoSize = true, Font = Theme.Title };
            var detail = new Label { Text = "У вас версия " + AppUpdates.CurrentVersion.ToString(3) + "\n" +
                (update.Installed ? "Пакет обновления Windows Installer" : "Обновление версии без установки"),
                Dock = DockStyle.Fill, ForeColor = Theme.Muted };
            var notes = new LinkLabel { Text = "Что нового на GitHub", AutoSize = true, LinkColor = Theme.Teal, ActiveLinkColor = Theme.Teal };
            notes.LinkClicked += (sender, args) => OpenLink(this, update.Notes);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var download = Theme.Button("Скачать обновление", 166, primary: true, glyph: Glyph.Refresh);
            download.DialogResult = DialogResult.OK;
            var cancel = Theme.Button("Позже", 88);
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.AddRange(new Control[] { download, cancel });
            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(detail, 0, 1);
            layout.Controls.Add(notes, 0, 2);
            layout.Controls.Add(buttons, 0, 3);
            Controls.Add(layout);
            AcceptButton = download;
            CancelButton = cancel;
        }

        public static void OpenLink(IWin32Window owner, string url)
        {
            try { using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { } }
            catch (Win32Exception ex) { Theme.ShowError(owner, ex); }
        }
    }
}
