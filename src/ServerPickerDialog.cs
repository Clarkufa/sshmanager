using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SshManager
{
    internal sealed class ServerPickerDialog : ThemedForm
    {
        private readonly ServerPickerPanel panel;

        public ServerPickerDialog(IEnumerable<Connection> connections, Groups groups, CatalogKind preferred)
        {
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

            panel = new ServerPickerPanel(compact: false);
            panel.UpdateServers(connections, groups);
            panel.SelectProtocol(preferred);
            panel.Submitted += target => DialogResult = DialogResult.OK;
            Controls.Add(panel);
            AcceptButton = panel.ConnectButton;
            CancelButton = panel.CancelButton;
            Theme.ScaleToDpi(this);
            ResumeLayout();
            Shown += (sender, args) => panel.FocusSearch();
        }

        public Connection SelectedConnection => panel.SelectedConnection;
        public RdpProfile SelectedProfile => panel.SelectedTarget?.Desktop;
        public VncProfile SelectedVnc => panel.SelectedTarget?.Vnc;
    }

    internal sealed class ServerPickerPanel : UserControl
    {
        private Choice[] choices = new Choice[0];
        private string[] recent = new string[0];
        private readonly bool grouped;
        private Groups groups;
        /// <summary>A group header was clicked: fold or unfold it (key: tab id, «ungrouped» or «recent»).</summary>
        public event Action<string> SectionToggled;
        /// <summary>Right click on a server: the Connection, RdpProfile or VncProfile, where and at which point.</summary>
        public event Action<object, Control, Point> MenuRequested;
        /// <summary>Right click, the menu key or Shift+F10 on a group header.</summary>
        public event Action<string, Control, Point> SectionMenuRequested;
        /// <summary>F2 and Delete on a server (docs/ui-spec.md §2.2).</summary>
        public event Action<object> EditRequested, DeleteRequested;
        public event Action<CatalogKind> ProtocolChanged;
        public CatalogKind Kind => kind;
        /// <summary>The tab of the selected server or group header, for a new server; null for «Без вкладки».</summary>
        public string SelectedGroupId => list.SelectedItem is Choice choice ? choice.GroupId :
            list.SelectedItem is Section section && section.Key != null && !Groups.IsSection(section.Key) ? section.Key : null;
        /// <summary>Servers of the chosen protocol (the footer counter).</summary>
        public int Count => choices.Count(choice => choice.Kind == kind);
        private readonly TextBox search;
        private readonly ListBox list;
        private readonly Button connect;
        private readonly Label status;
        private readonly SurfaceButton sshTab, rdpTab, vncTab;
        private CatalogKind kind;
        public event Action<SessionTarget> Submitted;
        public Button ConnectButton => connect;
        public Button CancelButton { get; }
        public Connection SelectedConnection => (list.SelectedItem as Choice)?.Connection;
        public SessionTarget SelectedTarget => (list.SelectedItem as Choice)?.Target;

        public ServerPickerPanel(bool compact)
        {
            grouped = compact;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            BackColor = Theme.Canvas;
            ForeColor = Theme.Ink;
            Font = Theme.Body;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = compact ? Padding.Empty : new Padding(18, 14, 18, 14) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(layout);

            var protocols = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            sshTab = Theme.Button("SSH", compact ? 62 : 86, glyph: compact ? Glyph.None : Glyph.Terminal);
            rdpTab = Theme.Button("RDP", compact ? 62 : 86, glyph: compact ? Glyph.None : Glyph.Server);
            vncTab = Theme.Button("VNC", compact ? 62 : 86, glyph: compact ? Glyph.None : Glyph.Server);
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
                BackColor = Theme.Surface, ForeColor = Theme.Ink,
                IntegralHeight = false, DrawMode = DrawMode.OwnerDrawVariable, BorderStyle = BorderStyle.FixedSingle };
            list.DrawItem += DrawChoice;
            list.MeasureItem += (sender, args) => args.ItemHeight = Theme.Scaled(list,
                list.Items[args.Index] is Choice ? 43 : list.Items[args.Index] is Section section && section.Key != null ? 32 : 26);
            list.SelectedIndexChanged += (sender, args) => connect.Enabled = list.SelectedItem is Choice;
            list.MouseDoubleClick += (sender, args) => { if (list.IndexFromPoint(args.Location) >= 0 && list.SelectedItem is Choice) Submit(); };
            list.MouseDown += (sender, args) =>
            {
                int index = list.IndexFromPoint(args.Location);
                if (index < 0 || index >= list.Items.Count) return;
                if (list.Items[index] is Section section && section.Key != null && args.Button == MouseButtons.Left)
                {
                    SectionToggled?.Invoke(section.Key);
                    return;
                }
                if (args.Button == MouseButtons.Right && list.Items[index] is Choice choice)
                {
                    list.SelectedIndex = index;
                    MenuRequested?.Invoke(choice.Profile, list, args.Location);
                }
                else if (args.Button == MouseButtons.Right && list.Items[index] is Section header && header.Key != null)
                {
                    list.SelectedIndex = index;
                    SectionMenuRequested?.Invoke(header.Key, list, args.Location);
                }
            };
            list.KeyDown += (sender, args) =>
            {
                var section = list.SelectedItem as Section;
                if (args.KeyCode == Keys.Enter || args.KeyCode == Keys.Space)
                {
                    args.SuppressKeyPress = true;
                    if (section?.Key != null) SectionToggled?.Invoke(section.Key);
                    else if (args.KeyCode == Keys.Enter) Submit();
                }
                else if ((args.KeyCode == Keys.Left || args.KeyCode == Keys.Right) && section?.Key != null)
                {
                    args.SuppressKeyPress = true;
                    if (section.Folded == (args.KeyCode == Keys.Right)) SectionToggled?.Invoke(section.Key);
                }
                else if ((args.KeyCode == Keys.Apps || args.KeyData == (Keys.Shift | Keys.F10)) && list.SelectedIndex >= 0)
                {
                    args.SuppressKeyPress = true;
                    var bounds = list.GetItemRectangle(list.SelectedIndex);
                    var at = new Point(bounds.Left + 12, bounds.Bottom);
                    if (list.SelectedItem is Choice choice) MenuRequested?.Invoke(choice.Profile, list, at);
                    else if (section?.Key != null) SectionMenuRequested?.Invoke(section.Key, list, at);
                }
                else if (args.KeyData == Keys.F2 && list.SelectedItem is Choice edited)
                {
                    args.SuppressKeyPress = true;
                    EditRequested?.Invoke(edited.Profile);
                }
                else if (args.KeyData == Keys.Delete && list.SelectedItem is Choice removed)
                {
                    args.SuppressKeyPress = true;
                    DeleteRequested?.Invoke(removed.Profile);
                }
            };
            layout.Controls.Add(Theme.Frame(list, Padding.Empty), 0, 2);
            status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Margin = new Padding(0) };
            layout.Controls.Add(status, 0, 3);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
            connect = Theme.Button("Подключиться", 128, true);
            connect.Click += (sender, args) => Submit();
            actions.Controls.Add(connect);
            if (!compact)
            {
                CancelButton = Theme.Button("Отмена", 88);
                CancelButton.DialogResult = DialogResult.Cancel;
                actions.Controls.Add(CancelButton);
            }
            layout.Controls.Add(actions, 0, 4);

            search.TextChanged += (sender, args) => Filter();
            search.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    Submit();
                    return;
                }
                if (list.Items.Count == 0 || (args.KeyCode != Keys.Down && args.KeyCode != Keys.Up)) return;
                int step = args.KeyCode == Keys.Down ? 1 : -1;
                int next = list.SelectedIndex + step;
                while (next >= 0 && next < list.Items.Count && !(list.Items[next] is Choice)) next += step;
                if (next >= 0 && next < list.Items.Count) list.SelectedIndex = next;
                args.SuppressKeyPress = true;
            };
            SelectProtocol(CatalogKind.Ssh);
        }

        public void FocusSearch() { search.Focus(); search.SelectAll(); }

        public void UpdateServers(IEnumerable<Connection> connections, Groups groups)
        {
            this.groups = groups;
            recent = groups.Recent.Select(entry => entry.Key).ToArray();
            // The order of the config and tabs.xml; Filter sorts (the column by the chosen order, the dialog by name).
            choices = connections.Select(connection => new Choice(connection, groups.ForAlias(connection.Alias)))
                .Concat(groups.Desktops.Select(profile => new Choice(profile, groups.Find(profile.GroupId))))
                .Concat(groups.Vnc.Select(profile => new Choice(profile, groups.Find(profile.GroupId)))).ToArray();
            Filter();
        }

        /// <summary>Selects a server by SSH alias or RDP/VNC id, switching to its protocol (after a save).</summary>
        public void SelectServer(string aliasOrId)
        {
            if (String.IsNullOrEmpty(aliasOrId)) return;
            var found = choices.FirstOrDefault(choice => choice.Connection != null
                ? String.Equals(choice.Connection.Alias, aliasOrId, StringComparison.OrdinalIgnoreCase)
                : (choice.Target.Desktop?.Id ?? choice.Target.Vnc?.Id) == aliasOrId);
            if (found == null) return;
            if (found.Kind != kind) SelectProtocol(found.Kind);
            for (int index = 0; index < list.Items.Count; index++)
                if (list.Items[index] is Choice choice && choice.Key == found.Key) { list.SelectedIndex = index; return; }
        }

        public void SelectProtocol(CatalogKind next)
        {
            bool changed = kind != next;
            kind = next;
            if (changed) ProtocolChanged?.Invoke(next);
            sshTab.Style = next == CatalogKind.Ssh ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            rdpTab.Style = next == CatalogKind.Rdp ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            vncTab.Style = next == CatalogKind.Vnc ? ButtonStyle.SelectedTab : ButtonStyle.Tab;
            if (ContainsFocus) search.Focus();
            Filter();
        }

        private void Submit()
        {
            if (SelectedTarget is SessionTarget target) Submitted?.Invoke(target);
        }

        private void Filter()
        {
            string query = search.Text.Trim();
            var order = grouped && groups != null ? groups.Sort(null, kind) : ServerSort.Name;
            var candidates = ServerOrder.Apply(choices.Where(choice => choice.Kind == kind), order,
                choice => choice.Target.Alias, choice => choice.Host).ToArray();
            var visible = candidates.Where(choice => new[] { choice.Target.Alias, choice.Target.Address, choice.Target.Group }
                .Any(value => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            if (grouped && groups != null && query.Length == 0)
            {
                FillGroups(candidates, visible);
                return;
            }
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

        /// <summary>«Серверы» column: «Недавние», then each catalog tab, then «Без вкладки»; folded groups stay folded
        /// (tabs.xml «collapsed», docs/ui-spec.md §2.2).</summary>
        private void FillGroups(Choice[] candidates, Choice[] visible)
        {
            var selectedKey = (list.SelectedItem as Choice)?.Key;
            var selectedSection = (list.SelectedItem as Section)?.Key;
            var latest = recent.Select(key => visible.FirstOrDefault(choice =>
                String.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase))).Where(choice => choice != null).Take(5).ToArray();
            var sections = new List<KeyValuePair<Section, Choice[]>>();
            if (latest.Length > 0)
                sections.Add(new KeyValuePair<Section, Choice[]>(new Section("Недавние", Groups.RecentSection, null, latest.Length), latest));
            var favorites = visible.Where(choice => groups.IsFavorite(choice.Key)).ToArray();
            if (favorites.Length > 0)
                sections.Add(new KeyValuePair<Section, Choice[]>(new Section("Избранное", Groups.FavoritesSection, null, favorites.Length), favorites));
            int fixedSections = sections.Count;
            foreach (var group in groups.Items)
            {
                var members = visible.Where(choice => choice.GroupId == group.Id).ToArray();
                if (members.Length > 0)
                    sections.Add(new KeyValuePair<Section, Choice[]>(new Section(group.Name, group.Id, TabColor.Find(group.ColorId)?.Swatch, members.Length),
                        members.Select(choice => choice.Indented()).ToArray()));
            }
            var loose = visible.Where(choice => choice.GroupId == null).ToArray();
            if (loose.Length > 0)
            {
                bool anyGroup = sections.Count > fixedSections;
                sections.Add(new KeyValuePair<Section, Choice[]>(new Section(anyGroup ? Groups.UngroupedName : "Все серверы",
                    Groups.UngroupedSection, null, loose.Length), loose.Select(choice => choice.Indented()).ToArray()));
            }
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                int select = -1;
                foreach (var pair in sections)
                {
                    pair.Key.Folded = groups.Collapsed.Contains(pair.Key.Key);
                    list.Items.Add(pair.Key);
                    if (pair.Key.Key == selectedSection) select = list.Items.Count - 1;
                    if (pair.Key.Folded) continue;
                    foreach (var choice in pair.Value)
                    {
                        list.Items.Add(choice);
                        if (select < 0 && selectedKey != null && choice.Key == selectedKey) select = list.Items.Count - 1;
                    }
                }
                if (select < 0) select = list.Items.Cast<object>().ToList().FindIndex(item => item is Choice);
                if (select >= 0) list.SelectedIndex = select;
                connect.Enabled = list.SelectedItem is Choice;
            }
            finally { list.EndUpdate(); }
            status.Text = visible.Length == 0 ? (candidates.Length == 0 ? "Сохранённых серверов пока нет." : "Серверы не найдены.") : "Серверов: " + visible.Length;
        }

        private void DrawChoice(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0) return;
            var row = args.Bounds;
            if (list.Items[args.Index] is Section group && group.Key != null)
            {
                bool chosen = (args.State & DrawItemState.Selected) != 0;
                using (var fill = new SolidBrush(chosen ? Theme.Selection : Theme.Surface)) args.Graphics.FillRectangle(fill, row);
                int G(int logical) => Theme.Scaled(list, logical);
                const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
                TextRenderer.DrawText(args.Graphics, group.Folded ? "▸" : "▾", Theme.Body,
                    new Rectangle(row.X + G(6), row.Y, G(14), row.Height), Theme.Muted, line);
                int left = row.X + G(22);
                if (group.Color.HasValue)
                {
                    using (var dot = new SolidBrush(group.Color.Value)) args.Graphics.FillEllipse(dot, left, row.Y + (row.Height - G(8)) / 2, G(8), G(8));
                    left += G(14);
                }
                string count = group.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                int countWidth = TextRenderer.MeasureText(count, Theme.Body).Width;
                TextRenderer.DrawText(args.Graphics, count, Theme.Body, new Rectangle(row.Right - countWidth - G(8), row.Y, countWidth, row.Height), Theme.Muted, line);
                TextRenderer.DrawText(args.Graphics, group.Title, Theme.Emphasis,
                    new Rectangle(left, row.Y, row.Right - left - countWidth - G(14), row.Height), Theme.Ink, line | TextFormatFlags.EndEllipsis);
                if ((args.State & DrawItemState.Focus) != 0) args.DrawFocusRectangle();
                return;
            }
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
            if (!choice.InGroup)
                using (var dot = new SolidBrush(choice.Target.Color)) args.Graphics.FillEllipse(dot, row.X + S(11), row.Y + S(10), S(7), S(7));
            var title = new Rectangle(row.X + S(24), row.Y + S(2), row.Width - S(32), S(22));
            var detail = new Rectangle(row.X + S(24), row.Y + S(22), row.Width - S(32), S(18));
            const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(args.Graphics, (groups != null && groups.IsFavorite(choice.Key) ? "★ " : "") + choice.Target.Alias,
                Theme.Emphasis, title, Theme.Ink, flags);
            string subtitle = choice.Target.Address + (choice.Target.Group.Length == 0 ? "" : "  ·  " + choice.Target.Group);
            TextRenderer.DrawText(args.Graphics, subtitle, Theme.Body, detail, Theme.Muted, flags);
            args.DrawFocusRectangle();
        }

        private sealed class Section
        {
            public readonly string Title;
            /// <summary>Folding key of a column group; null for the plain captions of the dialog.</summary>
            public readonly string Key;
            public readonly Color? Color;
            public readonly int Count;
            public bool Folded;
            public Section(string title, string key = null, Color? color = null, int count = 0)
            {
                Title = title;
                Key = key;
                Color = color;
                Count = count;
            }
            public override string ToString() { return Title + (Key == null ? "" : " · " + Count + (Folded ? " · свёрнута" : "")); }
        }

        private sealed class Choice
        {
            public readonly Connection Connection;
            public readonly SessionTarget Target;
            public string GroupId { get; private set; }
            /// <summary>Host for «По адресу»: HostName, else the alias.</summary>
            public string Host => Connection != null ? (Connection.Get("hostname").Length > 0 ? Connection.Get("hostname") : Connection.Alias)
                : Target.Desktop?.Host ?? Target.Vnc?.Host ?? "";
            /// <summary>Drawn under a group header: no group dot.</summary>
            public bool InGroup { get; private set; }
            public object Profile => Target.Desktop ?? (object)Target.Vnc ?? Connection;

            public Choice Indented()
            {
                var copy = (Choice)MemberwiseClone();
                copy.InGroup = true;
                return copy;
            }

            public string Key => Target.Desktop != null ? Groups.Key(Target.Desktop) : Target.Vnc != null ? Groups.Key(Target.Vnc) : Groups.Key(Connection.Alias);
            public CatalogKind Kind => Target.Desktop != null ? CatalogKind.Rdp : Target.Vnc != null ? CatalogKind.Vnc : CatalogKind.Ssh;
            public Choice(Connection connection, ServerGroup group)
            {
                Connection = connection;
                Target = new SessionTarget(connection, group);
                GroupId = group?.Id;
            }
            public Choice(RdpProfile profile, ServerGroup group)
            {
                Target = new SessionTarget(profile, group);
                GroupId = group?.Id;
            }
            public Choice(VncProfile profile, ServerGroup group)
            {
                Target = new SessionTarget(profile, group);
                GroupId = group?.Id;
            }
            public override string ToString() { return Target.Alias; }
        }
    }
}
