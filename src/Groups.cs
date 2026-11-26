using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace SshManager
{
    internal sealed class ServerGroup
    {
        public readonly string Id;
        public string Name;
        public string ColorId;
        public ServerSort SshSort, RdpSort, VncSort;
        public readonly HashSet<string> Aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ServerGroup(string id, string name) { Id = id; Name = name; }
    }

    /// <summary>«Настройки» (docs/ui-spec.md §7), stored as attributes of the tabs.xml root like on macOS.</summary>
    internal sealed class Preferences
    {
        public const int DefaultTerminalSize = 11;
        public string TerminalFont = "";
        public int TerminalSize = DefaultTerminalSize;
        public int Scrollback = 3000;
        public string DefaultUser = "";
        public string DefaultPort = "";
        public bool AutoReconnect;
        public bool CloseToTray = true;
        public bool CheckUpdates = true;

        public Preferences Copy() => (Preferences)MemberwiseClone();
    }

    internal sealed class Snippet
    {
        public readonly string Name, Command;

        public Snippet(string name, string command)
        {
            Name = (name ?? "").Trim();
            Command = command ?? "";
            if (Name.Length == 0 || Name.Length > 80 || Name.Any(char.IsControl))
                throw new ArgumentException("Укажите название команды длиной до 80 символов.");
            if (Command.Trim().Length == 0 || Command.Length > 4000 || Command.Any(ch => char.IsControl(ch) && ch != '\n' && ch != '\r' && ch != '\t'))
                throw new ArgumentException("Укажите команду длиной до 4000 символов.");
        }
    }

    internal sealed class RecentEntry
    {
        public readonly string Key;
        public readonly long At;
        public RecentEntry(string key, long at) { Key = key; At = at; }
    }

    internal sealed class Groups
    {
        public const string AllName = "Все серверы";
        public const string UngroupedName = "Без вкладки";
        public readonly List<ServerGroup> Items = new List<ServerGroup>();
        public readonly List<RdpProfile> Desktops = new List<RdpProfile>();
        public readonly List<VncProfile> Vnc = new List<VncProfile>();
        public ServerSort SshSort, RdpSort, VncSort;
        public string StartupTabId { get; private set; }
        public bool DarkTheme { get; set; }
        public readonly List<string> Favorites = new List<string>();
        public readonly List<RecentEntry> Recent = new List<RecentEntry>();
        public readonly List<Snippet> Snippets = new List<Snippet>();
        /// <summary>Folded groups of the «Серверы» column: tab ids, <see cref="UngroupedSection"/> and <see cref="RecentSection"/>.</summary>
        public readonly HashSet<string> Collapsed = new HashSet<string>(StringComparer.Ordinal);
        public const string UngroupedSection = "ungrouped", RecentSection = "recent";

        public void ToggleCollapsed(string key)
        {
            if (!Collapsed.Remove(key)) Collapsed.Add(key);
        }
        public Preferences Preferences = new Preferences();

        /// <summary>"ssh:alias" (lower case, as on macOS and Linux) or "rdp:id".</summary>
        public static string Key(string alias) => "ssh:" + alias.ToLowerInvariant();
        public static string Key(RdpProfile profile) => "rdp:" + profile.Id;
        public static string Key(VncProfile profile) => "vnc:" + profile.Id;

        public bool IsFavorite(string key) => Favorites.Contains(key, StringComparer.OrdinalIgnoreCase);

        public void ToggleFavorite(string key)
        {
            if (Favorites.RemoveAll(item => String.Equals(item, key, StringComparison.OrdinalIgnoreCase)) == 0) Favorites.Add(key);
        }

        public void RecordRecent(string key)
        {
            Recent.RemoveAll(item => String.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            Recent.Insert(0, new RecentEntry(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            if (Recent.Count > 10) Recent.RemoveRange(10, Recent.Count - 10);
        }

        /// <summary>Keeps favorites and recent entries pointing at the renamed or removed connection.</summary>
        public void RenameKey(string previous, string next)
        {
            for (int i = 0; i < Favorites.Count; i++)
                if (String.Equals(Favorites[i], previous, StringComparison.OrdinalIgnoreCase)) Favorites[i] = next;
            for (int i = 0; i < Recent.Count; i++)
                if (String.Equals(Recent[i].Key, previous, StringComparison.OrdinalIgnoreCase)) Recent[i] = new RecentEntry(next, Recent[i].At);
            if (next == null)
            {
                Favorites.RemoveAll(item => item == null);
                Recent.RemoveAll(item => item.Key == null);
            }
        }

        public ServerGroup Find(string id) { return Items.FirstOrDefault(group => group.Id == id); }
        public ServerGroup ForAlias(string alias) { return Items.FirstOrDefault(group => group.Aliases.Contains(alias)); }

        public ServerSort Sort(string id, CatalogKind kind)
        {
            var tab = Find(id);
            switch (kind)
            {
                case CatalogKind.Rdp: return tab?.RdpSort ?? RdpSort;
                case CatalogKind.Vnc: return tab?.VncSort ?? VncSort;
                default: return tab?.SshSort ?? SshSort;
            }
        }

        public void SetSort(string id, CatalogKind kind, ServerSort sort)
        {
            if (id == null)
            {
                if (kind == CatalogKind.Rdp) RdpSort = sort;
                else if (kind == CatalogKind.Vnc) VncSort = sort;
                else SshSort = sort;
            }
            else
            {
                var tab = RequireTab(id);
                if (kind == CatalogKind.Rdp) tab.RdpSort = sort;
                else if (kind == CatalogKind.Vnc) tab.VncSort = sort;
                else tab.SshSort = sort;
            }
        }

        public void SaveVnc(VncProfile profile, bool editing)
        {
            if (profile.GroupId != null) RequireTab(profile.GroupId);
            int index = Vnc.FindIndex(item => item.Id == profile.Id);
            if (index < 0 && editing) throw new InvalidOperationException("VNC-профиль уже удалён. Обновите список.");
            if (index < 0) Vnc.Add(profile);
            else Vnc[index] = profile;
        }

        public void MoveVnc(string id, string groupId)
        {
            var profile = Vnc.FirstOrDefault(item => item.Id == id);
            if (profile == null) throw new InvalidOperationException("VNC-профиль уже удалён. Обновите список.");
            SaveVnc(profile.InGroup(groupId), true);
        }

        public void SaveDesktop(RdpProfile profile, bool editing)
        {
            if (profile.GroupId != null) RequireTab(profile.GroupId);
            int index = Desktops.FindIndex(item => item.Id == profile.Id);
            if (index < 0 && editing) throw new InvalidOperationException("RDP-профиль уже удалён. Обновите список.");
            if (index < 0) Desktops.Add(profile);
            else Desktops[index] = profile;
        }

        public void MoveDesktop(string id, string groupId)
        {
            var profile = Desktops.FirstOrDefault(item => item.Id == id);
            if (profile == null) throw new InvalidOperationException("RDP-профиль уже удалён. Обновите список.");
            SaveDesktop(profile.InGroup(groupId), true);
        }

        public void NameGroup(string id, string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 32 || name.Any(char.IsControl))
                throw new ArgumentException("Введите название вкладки длиной от 1 до 32 символов.");
            if (new[] { AllName, UngroupedName }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                Items.Any(group => group.Id != id && String.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Это название уже используется. Выберите другое.");

            var existing = Find(id);
            if (existing == null) Items.Add(new ServerGroup(id, name));
            else existing.Name = name;
        }

        public void Move(string alias, string id)
        {
            var target = Find(id);
            if (id != null && target == null) throw new InvalidOperationException("Эта вкладка уже удалена. Обновите список.");
            foreach (var group in Items) group.Aliases.Remove(alias);
            if (target != null) target.Aliases.Add(alias);
        }

        public void Mark(string id, string colorId)
        {
            RequireTab(id).ColorId = colorId;
        }

        public void PlaceTab(string id, string anchorId, bool before)
        {
            var group = RequireTab(id);
            var anchor = RequireTab(anchorId);
            if (group == anchor) return;
            Items.Remove(group);
            Items.Insert(Items.IndexOf(anchor) + (before ? 0 : 1), group);
        }

        public void ShiftTab(string id, int offset)
        {
            var group = RequireTab(id);
            int target = Items.IndexOf(group) + offset;
            if (target >= 0 && target < Items.Count) PlaceTab(id, Items[target].Id, offset < 0);
        }

        public void SetStartupTab(string id)
        {
            if (id != null) RequireTab(id);
            StartupTabId = id;
        }

        public void RemoveTab(string id)
        {
            Items.RemoveAll(group => group.Id == id);
            for (int i = 0; i < Desktops.Count; i++)
                if (Desktops[i].GroupId == id) Desktops[i] = Desktops[i].InGroup(null);
            for (int i = 0; i < Vnc.Count; i++)
                if (Vnc[i].GroupId == id) Vnc[i] = Vnc[i].InGroup(null);
            if (StartupTabId == id) StartupTabId = null;
        }

        private ServerGroup RequireTab(string id)
        {
            var group = Find(id);
            if (group == null) throw new InvalidOperationException("Эта вкладка уже удалена. Обновите список.");
            return group;
        }
    }

    internal sealed class GroupFile
    {
        internal const string WriteLock = @"Local\SshManager.GroupsWrite";
        private readonly string path;

        public GroupFile(string path) { this.path = path; }

        public Groups Load()
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return Parse(file);
            }
            catch (FileNotFoundException) { return new Groups(); }
            catch (DirectoryNotFoundException) { return new Groups(); }
            catch (Exception ex) when (ex is XmlException || ex is ArgumentException || ex is FormatException || ex is OverflowException)
            {
                throw new IOException("Не удалось прочитать настройки вкладок. Проверьте файл «" + path + "».", ex);
            }
        }

        internal static Groups Parse(Stream file)
        {
            var groups = new Groups();
            XElement root;
            using (var reader = XmlReader.Create(file, new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit }))
                root = XDocument.Load(reader).Root;
            if (root == null || root.Name != "tabs") throw new XmlException("Ожидался файл настроек вкладок.");
            groups.DarkTheme = (string)root.Attribute("theme") == "dark";
            groups.SshSort = ServerOrder.Parse((string)root.Attribute("sshSort"));
            groups.RdpSort = ServerOrder.Parse((string)root.Attribute("rdpSort"));
            groups.VncSort = ServerOrder.Parse((string)root.Attribute("vncSort"));
            ParseExtras(root, groups);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in root.Elements("tab"))
            {
                var id = (string)node.Attribute("id");
                var name = (string)node.Attribute("name");
                Guid parsed;
                if (!Guid.TryParseExact(id, "N", out parsed) || groups.Find(id) != null || String.IsNullOrWhiteSpace(name))
                    throw new XmlException("Повреждены настройки вкладок.");
                groups.NameGroup(id, name);
                var group = groups.Find(id);
                group.ColorId = (string)node.Attribute("color");
                group.SshSort = ServerOrder.Parse((string)node.Attribute("sshSort"));
                group.RdpSort = ServerOrder.Parse((string)node.Attribute("rdpSort"));
                group.VncSort = ServerOrder.Parse((string)node.Attribute("vncSort"));
                foreach (var host in node.Elements("host"))
                {
                    var alias = (string)host.Attribute("alias");
                    if (alias == null || !ConfigDocument.IsAlias(alias) || !known.Add(alias)) throw new XmlException("Повреждён список серверов во вкладках.");
                    group.Aliases.Add(alias);
                }
            }
            var desktopIds = new HashSet<string>();
            foreach (var node in root.Elements("rdp"))
            {
                var profile = RdpProfile.Parse(node, groups.Find((string)node.Attribute("group"))?.Id);
                if (!desktopIds.Add(profile.Id)) throw new XmlException("Повторяющийся идентификатор RDP-профиля.");
                groups.Desktops.Add(profile);
            }
            var vncIds = new HashSet<string>();
            foreach (var node in root.Elements("vnc"))
            {
                var profile = VncProfile.Parse(node, groups.Find((string)node.Attribute("group"))?.Id);
                if (!vncIds.Add(profile.Id)) throw new XmlException("Повторяющийся идентификатор VNC-профиля.");
                groups.Vnc.Add(profile);
            }
            string startup = (string)root.Attribute("startupTab");
            if (groups.Find(startup) != null) groups.SetStartupTab(startup);
            foreach (var key in ((string)root.Attribute("collapsed") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (key == Groups.UngroupedSection || key == Groups.RecentSection || groups.Find(key) != null) groups.Collapsed.Add(key);
            return groups;
        }

        private static XAttribute CollapsedAttribute(Groups latest)
        {
            var keys = latest.Collapsed.Where(key => key == Groups.UngroupedSection || key == Groups.RecentSection || latest.Find(key) != null)
                .OrderBy(key => key, StringComparer.Ordinal).ToArray();
            return keys.Length == 0 ? null : new XAttribute("collapsed", String.Join(" ", keys));
        }

        /// <summary>Stage 2 data (docs/ui-spec.md §7). Unknown or damaged values are skipped, never fatal.</summary>
        private static void ParseExtras(XElement root, Groups groups)
        {
            string Text(string name) => (string)root.Attribute(name);
            int Number(string name, int fallback, int min, int max) =>
                Int32.TryParse(Text(name), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
                    ? Math.Min(max, Math.Max(min, value)) : fallback;
            var preferences = groups.Preferences;
            var font = Text("terminalFont") ?? "";
            preferences.TerminalFont = font.Length <= 80 && !font.Any(char.IsControl) ? font : "";
            preferences.TerminalSize = Number("terminalSize", Preferences.DefaultTerminalSize, 6, 48);
            preferences.Scrollback = Number("scrollback", 3000, 100, 100000);
            var user = Text("defaultUser") ?? "";
            preferences.DefaultUser = user.Length <= 128 && !user.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)) ? user : "";
            int port = Number("defaultPort", 0, 0, 65535);
            preferences.DefaultPort = port == 0 ? "" : port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            preferences.AutoReconnect = Text("autoReconnect") == "1";
            preferences.CloseToTray = Text("closeToTray") != "0";
            preferences.CheckUpdates = Text("checkUpdates") != "0";
            foreach (var node in root.Elements("favorite"))
            {
                var key = (string)node.Attribute("key");
                if (key != null && key.Length <= 300 && (key.StartsWith("ssh:", StringComparison.Ordinal) || key.StartsWith("rdp:", StringComparison.Ordinal)) &&
                    !groups.IsFavorite(key)) groups.Favorites.Add(key);
            }
            foreach (var node in root.Elements("recent"))
            {
                var key = (string)node.Attribute("key");
                if (key == null || key.Length > 300 || groups.Recent.Count >= 10) continue;
                long.TryParse((string)node.Attribute("at"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var at);
                groups.Recent.Add(new RecentEntry(key, at));
            }
            foreach (var node in root.Elements("snippet"))
            {
                try { groups.Snippets.Add(new Snippet((string)node.Attribute("name"), (string)node.Attribute("command"))); }
                catch (ArgumentException) { }
            }
        }

        private static IEnumerable<XObject> WriteExtras(Groups latest)
        {
            var preferences = latest.Preferences;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            if (preferences.TerminalFont.Length > 0) yield return new XAttribute("terminalFont", preferences.TerminalFont);
            yield return new XAttribute("terminalSize", preferences.TerminalSize.ToString(invariant));
            yield return new XAttribute("scrollback", preferences.Scrollback.ToString(invariant));
            if (preferences.DefaultUser.Length > 0) yield return new XAttribute("defaultUser", preferences.DefaultUser);
            if (preferences.DefaultPort.Length > 0) yield return new XAttribute("defaultPort", preferences.DefaultPort);
            yield return new XAttribute("autoReconnect", preferences.AutoReconnect ? "1" : "0");
            yield return new XAttribute("closeToTray", preferences.CloseToTray ? "1" : "0");
            yield return new XAttribute("checkUpdates", preferences.CheckUpdates ? "1" : "0");
            foreach (var key in latest.Favorites) yield return new XElement("favorite", new XAttribute("key", key));
            foreach (var entry in latest.Recent.Take(10))
                yield return new XElement("recent", new XAttribute("key", entry.Key), new XAttribute("at", entry.At.ToString(invariant)));
            foreach (var snippet in latest.Snippets)
                yield return new XElement("snippet", new XAttribute("name", snippet.Name), new XAttribute("command", snippet.Command));
        }

        public Groups Update(Action<Groups> modify)
        {
            using (var mutex = new Mutex(false, WriteLock))
            {
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("Настройки вкладок заняты другим окном приложения.");
                }
                catch (AbandonedMutexException) { /* The previous writer exited. */ }
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var latest = Load();
                    modify(latest);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var document = new XDocument(new XElement("tabs",
                        latest.StartupTabId == null ? null : new XAttribute("startupTab", latest.StartupTabId),
                        latest.DarkTheme ? new XAttribute("theme", "dark") : null,
                        SortAttribute("sshSort", latest.SshSort), SortAttribute("rdpSort", latest.RdpSort),
                        SortAttribute("vncSort", latest.VncSort),
                        CollapsedAttribute(latest),
                        WriteExtras(latest),
                        latest.Items.Select(group =>
                        new XElement("tab", new XAttribute("id", group.Id), new XAttribute("name", group.Name),
                            group.ColorId == null ? null : new XAttribute("color", group.ColorId),
                            SortAttribute("sshSort", group.SshSort), SortAttribute("rdpSort", group.RdpSort),
                            SortAttribute("vncSort", group.VncSort),
                            group.Aliases.OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                                .Select(alias => new XElement("host", new XAttribute("alias", alias))))),
                        latest.Desktops.Select(profile => profile.ToXml()),
                        latest.Vnc.Select(profile => profile.ToXml())));
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        document.Save(stream);
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) ReplaceSettings(temp);
                    else File.Move(temp, path);
                    return latest;
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
        }

        private void ReplaceSettings(string temporary)
        {
            const int sharingViolation = 32, lockViolation = 33, unableToRemoveReplaced = 1175;
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(temporary, path, null); return; }
                catch (IOException error) when (attempt < 5 &&
                    ((error.HResult & 0xffff) is sharingViolation or lockViolation or unableToRemoveReplaced))
                {
                    Thread.Sleep(60);
                }
            }
        }

        private static XAttribute SortAttribute(string name, ServerSort value) => value == ServerSort.Original ? null : new XAttribute(name, value);
    }
}
