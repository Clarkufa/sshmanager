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
        public ServerSort SshSort, RdpSort;
        public readonly HashSet<string> Aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ServerGroup(string id, string name) { Id = id; Name = name; }
    }

    internal sealed class Groups
    {
        public const string AllName = "Все серверы";
        public const string UngroupedName = "Без вкладки";
        public readonly List<ServerGroup> Items = new List<ServerGroup>();
        public readonly List<RdpProfile> Desktops = new List<RdpProfile>();
        public ServerSort SshSort, RdpSort;
        public string StartupTabId { get; private set; }
        public bool DarkTheme { get; set; }

        public ServerGroup Find(string id) { return Items.FirstOrDefault(group => group.Id == id); }
        public ServerGroup ForAlias(string alias) { return Items.FirstOrDefault(group => group.Aliases.Contains(alias)); }

        public ServerSort Sort(string id, bool rdp)
        {
            var tab = Find(id);
            return rdp ? tab?.RdpSort ?? RdpSort : tab?.SshSort ?? SshSort;
        }

        public void SetSort(string id, bool rdp, ServerSort sort)
        {
            if (id == null)
            {
                if (rdp) RdpSort = sort;
                else SshSort = sort;
            }
            else
            {
                var tab = RequireTab(id);
                if (rdp) tab.RdpSort = sort;
                else tab.SshSort = sort;
            }
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
            string startup = (string)root.Attribute("startupTab");
            if (groups.Find(startup) != null) groups.SetStartupTab(startup);
            return groups;
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
                        latest.Items.Select(group =>
                        new XElement("tab", new XAttribute("id", group.Id), new XAttribute("name", group.Name),
                            group.ColorId == null ? null : new XAttribute("color", group.ColorId),
                            SortAttribute("sshSort", group.SshSort), SortAttribute("rdpSort", group.RdpSort),
                            group.Aliases.OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                                .Select(alias => new XElement("host", new XAttribute("alias", alias))))),
                        latest.Desktops.Select(profile => profile.ToXml())));
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        document.Save(stream);
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temp, path, null);
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

        private static XAttribute SortAttribute(string name, ServerSort value) => value == ServerSort.Original ? null : new XAttribute(name, value);
    }
}
