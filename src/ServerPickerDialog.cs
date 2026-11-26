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
        private readonly SurfaceButton sshTab, rdpTab, vncTab;
        private readonly string[] recent;
        private CatalogKind kind;

        public ServerPickerDialog(IEnumerable<Connection> connections, Groups groups, CatalogKind preferred)
        {
            recent = groups.Recent.Select(entry => entry.Key).ToArray();
            choices = connections.Select(connection => new Choice(connection, groups.ForAlias(connection.Alias)))
                .Concat(groups.Desktops.Select(profile => new Choice(profile, groups.Find(profile.GroupId))))
                .Concat(groups.Vnc.Select(profile => new Choice(profile, groups.Find(profile.GroupId))))
                .OrderBy(choice => choice.Target.Alias, StringComparer.OrdinalIgnoreCase).ToArray();
            Text = "Новый сеанс";
            Font = Theme.Body;
            ForeColor = Theme.Ink;
            BackColor = Theme.Canvas;
            StartPosition = FormStartPosition.CenterParent;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
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
            vncTab = Theme.Button("VNC", 86, glyph: Glyph.Server);
            sshTab.AccessibleRole = rdpTab.AccessibleRole = vncTab.AccessibleRole = AccessibleRole.PageTab;
            sshTab.Click += (sender, args) => SelectProtocol(CatalogKind.Ssh);
            rdpTab.Click += (sender, args) => SelectProtocol(CatalogKind.Rdp);
            vncTab.Click += (sender, args) => SelectProtocol(CatalogKind.Vnc);
            protocols.Controls.AddRange(new Control[] { sshTab, rdpTab, vncTab });
            layout.Controls.Add(protocols, 0, 0);
            var filter = new ServerSearch { Dock = DockStyle.Top };
            search = filter.Input;
            layout.Controls.Add(filter, 0, 1);
            list = new ListBox { AccessibleName = "Список серверов", Dock = DockStyle.Fill, Margin = new Padding(0),
                IntegralHeight = false, DrawMode = DrawMode.OwnerDrawVariable, BorderStyle = BorderStyle.FixedSingle };
            list.DrawItem += DrawChoice;
            list.MeasureItem += (sender, args) => args.ItemHeight = Theme.Scaled(list, list.Items[args.Index] is Choice ? 43 : 26);
            list.SelectedIndexChanged += (sender, args) => connect.Enabled = list.SelectedItem is Choice;
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
                int step = args.KeyCode == Keys.Down ? 1 : -1;
                int next = list.SelectedIndex + step;
                while (next >= 0 && next < list.Items.Count && !(list.Items[next] is Choice)) next += step;
                if (next >= 0 && next < list.Items.Count) list.SelectedIndex = next;
                args.SuppressKeyPress = true;
            };
            SelectProtocol(preferred);
            Theme.ScaleToDpi(this);
            ResumeLayout();
        }

        public Connection SelectedConnection { get; private set; }
        public RdpProfile SelectedProfile { get; private set; }
        public VncProfile SelectedVnc { get; private set; }

        private void SelectProtocol(CatalogKind next)
        {
            kind = next;
            sshTab.Style = next == CatalogKind.Ssh ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            rdpTab.Style = next == CatalogKind.Rdp ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            vncTab.Style = next == CatalogKind.Vnc ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            ActiveControl = search;
            Filter();
        }

        private void Submit()
        {
            var choice = list.SelectedItem as Choice;
            if (choice == null) return;
            SelectedConnection = choice.Connection;
            SelectedProfile = choice.Target.Desktop;
            SelectedVnc = choice.Target.Vnc;
            DialogResult = DialogResult.OK;
        }

        private void Filter()
        {
            string query = search.Text.Trim();
            var candidates = choices.Where(choice => choice.Kind == kind).ToArray();
            var visible = candidates.Where(choice => new[] { choice.Target.Alias, choice.Target.Address, choice.Target.Group }
                .Any(value => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            // «Недавние» (up to 5) before the full list while the search is empty (docs/ui-spec.md §7).
            var latest = query.Length == 0 ? recent.Select(key => candidates.FirstOrDefault(choice =>
                String.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase))).Where(choice => choice != null).Take(5).ToArray()
                : new Choice[0];
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                if (latest.Length > 0)
                {
                    list.Items.Add(new Section("Недавние"));
                    list.Items.AddRange(latest);
                    list.Items.Add(new Section("Все серверы"));
                }
                list.Items.AddRange(visible);
                if (visible.Length > 0) list.SelectedIndex = latest.Length > 0 ? 1 : 0;
                connect.Enabled = visible.Length != 0;
            }
            finally { list.EndUpdate(); }
            status.Text = visible.Length == 0 ? (candidates.Length == 0 ? "Сохранённых серверов пока нет." : "Серверы не найдены.") : "Серверов: " + visible.Length;
        }

        private void DrawChoice(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0) return;
            var row = args.Bounds;
            if (list.Items[args.Index] is Section section)
            {
                using (var fill = new SolidBrush(Theme.Surface)) args.Graphics.FillRectangle(fill, row);
                var caption = new Rectangle(row.X + Theme.Scaled(list, 11), row.Y, row.Width - Theme.Scaled(list, 16), row.Height);
                TextRenderer.DrawText(args.Graphics, section.Title, Theme.Body, caption, Theme.Muted,
                    TextFormatFlags.Bottom | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                return;
            }
            var choice = (Choice)list.Items[args.Index];
            using (var fill = new SolidBrush((args.State & DrawItemState.Selected) != 0 ? Theme.Selection : Theme.Surface))
                args.Graphics.FillRectangle(fill, row);
            int S(int logical) => Theme.Scaled(list, logical);
            using (var dot = new SolidBrush(choice.Target.Color)) args.Graphics.FillEllipse(dot, row.X + S(11), row.Y + S(10), S(7), S(7));
            var title = new Rectangle(row.X + S(24), row.Y + S(2), row.Width - S(32), S(22));
            var detail = new Rectangle(row.X + S(24), row.Y + S(22), row.Width - S(32), S(18));
            const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(args.Graphics, choice.Target.Alias, Theme.Emphasis, title, Theme.Ink, flags);
            string subtitle = choice.Target.Address + (choice.Target.Group.Length == 0 ? "" : "  ·  " + choice.Target.Group);
            TextRenderer.DrawText(args.Graphics, subtitle, Theme.Body, detail, Theme.Muted, flags);
            args.DrawFocusRectangle();
        }

        private sealed class Section
        {
            public readonly string Title;
            public Section(string title) { Title = title; }
            public override string ToString() { return Title; }
        }

        private sealed class Choice
        {
            public readonly Connection Connection;
            public readonly SessionTarget Target;
            public string Key => Target.Desktop != null ? Groups.Key(Target.Desktop) : Target.Vnc != null ? Groups.Key(Target.Vnc) : Groups.Key(Connection.Alias);
            public CatalogKind Kind => Target.Desktop != null ? CatalogKind.Rdp : Target.Vnc != null ? CatalogKind.Vnc : CatalogKind.Ssh;
            public Choice(Connection connection, ServerGroup group)
            {
                Connection = connection;
                Target = new SessionTarget(connection, group);
            }
            public Choice(RdpProfile profile, ServerGroup group)
            {
                Target = new SessionTarget(profile, group);
            }
            public Choice(VncProfile profile, ServerGroup group)
            {
                Target = new SessionTarget(profile, group);
            }
            public override string ToString() { return Target.Alias; }
        }
    }
}
