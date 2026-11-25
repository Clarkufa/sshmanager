using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class ServerPickerDialog : ThemedForm
    {
        private readonly Choice[] choices;
        private readonly TextBox search;
        private readonly ListBox list;
        private readonly Button connect;
        private readonly Label status;
        private readonly SurfaceButton sshTab, rdpTab;
        private bool showRdp;

        public ServerPickerDialog(IEnumerable<Connection> connections, Groups groups, bool preferRdp)
        {
            choices = connections.Select(connection => new Choice(connection, groups.ForAlias(connection.Alias)))
                .Concat(groups.Desktops.Select(profile => new Choice(profile, groups.Find(profile.GroupId))))
                .OrderBy(choice => choice.Target.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
            Text = "Новый сеанс";
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(495, 389);
            MinimumSize = new Size(430, 305);
            ShowInTaskbar = MaximizeBox = MinimizeBox = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(18, 14, 18, 14) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(layout);

            var protocols = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            sshTab = Theme.Button("SSH", 86, glyph: Glyph.Terminal);
            rdpTab = Theme.Button("RDP", 86, glyph: Glyph.Server);
            sshTab.AccessibleRole = rdpTab.AccessibleRole = AccessibleRole.PageTab;
            sshTab.Click += (sender, args) => SelectProtocol(false);
            rdpTab.Click += (sender, args) => SelectProtocol(true);
            protocols.Controls.AddRange(new Control[] { sshTab, rdpTab });
            layout.Controls.Add(protocols, 0, 0);
            var filter = new ServerSearch { Dock = DockStyle.Top };
            search = filter.Input;
            layout.Controls.Add(filter, 0, 1);
            list = new ListBox { AccessibleName = "Список серверов", Dock = DockStyle.Fill, Margin = new Padding(0),
                IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 43, BorderStyle = BorderStyle.FixedSingle };
            list.DrawItem += DrawChoice;
            list.SelectedIndexChanged += (sender, args) => connect.Enabled = list.SelectedItem != null;
            list.MouseDoubleClick += (sender, args) => { if (list.IndexFromPoint(args.Location) >= 0) Submit(); };
            list.KeyDown += (sender, args) => { if (args.KeyCode == Keys.Enter) { args.SuppressKeyPress = true; Submit(); } };
            layout.Controls.Add(Theme.Frame(list, Padding.Empty), 0, 2);
            status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Margin = new Padding(0) };
            layout.Controls.Add(status, 0, 3);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
            connect = Theme.Button("Подключиться", 128, true);
            connect.Click += (sender, args) => Submit();
            var cancel = Theme.Button("Отмена", 88);
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.AddRange(new Control[] { connect, cancel });
            layout.Controls.Add(actions, 0, 4);
            AcceptButton = connect;
            CancelButton = cancel;

            search.TextChanged += (sender, args) => Filter();
            search.KeyDown += (sender, args) =>
            {
                if (list.Items.Count == 0 || (args.KeyCode != Keys.Down && args.KeyCode != Keys.Up)) return;
                int next = list.SelectedIndex + (args.KeyCode == Keys.Down ? 1 : -1);
                list.SelectedIndex = Math.Max(0, Math.Min(list.Items.Count - 1, next));
                args.SuppressKeyPress = true;
            };
            SelectProtocol(preferRdp);
        }

        public Connection SelectedConnection { get; private set; }
        public RdpProfile SelectedProfile { get; private set; }

        private void SelectProtocol(bool rdp)
        {
            showRdp = rdp;
            sshTab.Style = rdp ? ButtonStyle.Tab : ButtonStyle.SelectedTab;
            rdpTab.Style = rdp ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            ActiveControl = search;
            Filter();
        }

        private void Submit()
        {
            var choice = list.SelectedItem as Choice;
            if (choice == null) return;
            SelectedConnection = choice.Connection;
            SelectedProfile = choice.Target.Desktop;
            DialogResult = DialogResult.OK;
        }

        private void Filter()
        {
            string query = search.Text.Trim();
            var candidates = choices.Where(choice => (choice.Target.Desktop != null) == showRdp).ToArray();
            var visible = candidates.Where(choice => new[] { choice.Target.Alias, choice.Target.Address, choice.Target.Group }
                .Any(value => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                list.Items.AddRange(visible);
                if (visible.Length > 0) list.SelectedIndex = 0;
                connect.Enabled = visible.Length != 0;
            }
            finally { list.EndUpdate(); }
            status.Text = visible.Length == 0 ? (candidates.Length == 0 ? "Сохранённых серверов пока нет." : "Серверы не найдены.") : "Серверов: " + visible.Length;
        }

        private void DrawChoice(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0) return;
            var choice = (Choice)list.Items[args.Index];
            var row = args.Bounds;
            using (var fill = new SolidBrush((args.State & DrawItemState.Selected) != 0 ? Theme.Selection : Theme.Surface))
                args.Graphics.FillRectangle(fill, row);
            using (var dot = new SolidBrush(choice.Target.Color)) args.Graphics.FillEllipse(dot, row.X + 11, row.Y + 10, 7, 7);
            var title = new Rectangle(row.X + 24, row.Y + 2, row.Width - 32, 22);
            var detail = new Rectangle(row.X + 24, row.Y + 22, row.Width - 32, 18);
            const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(args.Graphics, choice.Target.Alias, Theme.Emphasis, title, Theme.Ink, flags);
            string subtitle = choice.Target.Address + (choice.Target.Group.Length == 0 ? "" : "  ·  " + choice.Target.Group);
            TextRenderer.DrawText(args.Graphics, subtitle, Theme.Body, detail, Theme.Muted, flags);
            args.DrawFocusRectangle();
        }

        private sealed class Choice
        {
            public readonly Connection Connection;
            public readonly SessionTarget Target;
            public Choice(Connection connection, ServerGroup group)
            {
                Connection = connection;
                Target = new SessionTarget(connection, group);
            }
            public Choice(RdpProfile profile, ServerGroup group)
            {
                Target = new SessionTarget(profile, group);
            }
            public override string ToString() { return Target.Alias; }
        }
    }
}
