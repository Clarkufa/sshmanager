using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace SshManager
{
    internal enum ServerSort { Original, Name, NameDescending, Address, AddressDescending }

    internal static class ServerOrder
    {
        public static ServerSort Parse(string value) => Enum.TryParse(value, out ServerSort order) && Enum.IsDefined(typeof(ServerSort), order)
            ? order : ServerSort.Original;

        public static IEnumerable<T> Apply<T>(IEnumerable<T> values, ServerSort order, Func<T, string> name, Func<T, string> host)
        {
            var names = StringComparer.CurrentCultureIgnoreCase;
            switch (order)
            {
                case ServerSort.Name: return values.OrderBy(name, names);
                case ServerSort.NameDescending: return values.OrderByDescending(name, names);
                case ServerSort.Address: return values.OrderBy(host, AddressComparer.Instance).ThenBy(name, names);
                case ServerSort.AddressDescending: return values.OrderByDescending(host, AddressComparer.Instance).ThenBy(name, names);
                default: return values;
            }
        }

        private sealed class AddressComparer : IComparer<string>
        {
            public static readonly AddressComparer Instance = new AddressComparer();
            public int Compare(string left, string right)
            {
                byte[] a = Bytes(left), b = Bytes(right);
                if (a == null || b == null)
                    return a == null && b == null ? StringComparer.OrdinalIgnoreCase.Compare(left, right) : a == null ? 1 : -1;
                int length = a.Length.CompareTo(b.Length);
                if (length != 0) return length;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i]) return a[i].CompareTo(b[i]);
                return StringComparer.OrdinalIgnoreCase.Compare(left, right);
            }

            private static byte[] Bytes(string host) => !String.IsNullOrEmpty(host) && (host.Contains(".") || host.Contains(":")) &&
                IPAddress.TryParse(host.Trim('[', ']'), out var address) ? address.GetAddressBytes() : null;
        }
    }

    internal sealed class RdpProfile
    {
        public readonly string Id, Name, Host, User, Domain, GroupId;
        public readonly int Port, Width, Height;
        public readonly bool Clipboard, FullScreen;

        public RdpProfile(string id, string name, string host, int port, string user, string domain, string groupId,
            int width = 0, int height = 0, bool clipboard = false, bool fullScreen = false)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Некорректный идентификатор RDP-профиля.");
            Id = id.ToLowerInvariant();
            Name = Text(name, 80);
            if (Name.Length == 0) throw new ArgumentException("Укажите название RDP-подключения.");
            var address = Text(host, 253);
            if (address.StartsWith("[") && address.EndsWith("]")) address = address.Substring(1, address.Length - 2);
            if (IPAddress.TryParse(address, out var ip) && (address.Contains(".") || address.Contains(":")))
                Host = ip.ToString();
            else if (Uri.CheckHostName(address) == UriHostNameType.Dns)
                Host = new IdnMapping().GetAscii(address);
            else throw new ArgumentException("Укажите DNS-имя, IPv4 или IPv6 без номера порта.");
            if (port < 1 || port > 65535) throw new ArgumentException("Укажите порт от 1 до 65535.");
            Port = port;
            User = Text(user, 128);
            Domain = Text(domain, 128);
            GroupId = groupId;
            if ((width != 0 || height != 0) && (width < 640 || width > 8192 || height < 480 || height > 8192))
                throw new ArgumentException("Укажите разрешение от 640 × 480 до 8192 × 8192 или оставьте оба поля пустыми.");
            Width = width;
            Height = height;
            Clipboard = clipboard;
            FullScreen = fullScreen;
        }

        public string Address => (Host.Contains(":") ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);
        public string Account => Domain.Length == 0 || User.Length == 0 ? User : Domain + "\\" + User;

        public RdpProfile InGroup(string id) => new RdpProfile(Id, Name, Host, Port, User, Domain, id, Width, Height, Clipboard, FullScreen);

        private static string Text(string input, int limit)
        {
            string value = (input ?? "").Trim();
            if (value.Length > limit || value.Any(char.IsControl)) throw new ArgumentException("Одно из полей RDP содержит недопустимый текст.");
            return value;
        }

        public static RdpProfile Parse(XElement node, string groupId) => new RdpProfile((string)node.Attribute("id"),
            (string)node.Attribute("name"), (string)node.Attribute("host"), (int?)node.Attribute("port") ?? 3389,
            (string)node.Attribute("user"), (string)node.Attribute("domain"), groupId,
            (int?)node.Attribute("width") ?? 0, (int?)node.Attribute("height") ?? 0,
            (string)node.Attribute("clipboard") == "1", (string)node.Attribute("fullScreen") == "1");

        public XElement ToXml() => new XElement("rdp", new XAttribute("id", Id), new XAttribute("name", Name),
            new XAttribute("host", Host), new XAttribute("port", Port), new XAttribute("user", User), new XAttribute("domain", Domain),
            GroupId == null ? null : new XAttribute("group", GroupId), Width == 0 ? null : new XAttribute("width", Width),
            Height == 0 ? null : new XAttribute("height", Height), Clipboard ? new XAttribute("clipboard", 1) : null,
            FullScreen ? new XAttribute("fullScreen", 1) : null);

        public static RdpProfile Import(string file, string groupId)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(input, Encoding.UTF8, true))
            {
                if (input.Length > 256 * 1024) throw new IOException("RDP-файл слишком большой.");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Split(new[] { ':' }, 3);
                    if (parts.Length == 3) fields[parts[0].Trim()] = parts[2];
                }
            }
            string Field(string key) => fields.TryGetValue(key, out var value) ? value : "";
            int Number(string key, int fallback) => Int32.TryParse(Field(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;
            var host = Field("full address").Trim();
            int port = Number("server port", 3389);
            int separator = host.StartsWith("[") ? host.IndexOf("]:", StringComparison.Ordinal) + 1 :
                host.Count(ch => ch == ':') == 1 ? host.IndexOf(':') : 0;
            if (separator > 0)
            {
                if (!Int32.TryParse(host.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port))
                    throw new ArgumentException("Некорректный порт в RDP-файле.");
                host = host.Substring(0, separator);
            }
            string user = Field("username"), domain = Field("domain");
            int slash = user.IndexOf('\\');
            if (domain.Length == 0 && slash > 0) { domain = user.Substring(0, slash); user = user.Substring(slash + 1); }
            int width = Number("desktopwidth", 0), height = Number("desktopheight", 0);
            if (width == 0 || height == 0) width = height = 0;
            return new RdpProfile(Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(file), host, port, user, domain,
                groupId, width, height, Number("redirectclipboard", 0) == 1, Number("screen mode id", 1) == 2);
        }

        public void Export(string file)
        {
            var lines = new List<string> { "full address:s:" + Address, "username:s:" + Account,
                "screen mode id:i:" + (FullScreen ? 2 : 1), "redirectclipboard:i:" + (Clipboard ? 1 : 0),
                "enablecredsspsupport:i:1", "authentication level:i:2", "drivestoredirect:s:", "redirectprinters:i:0" };
            if (Width != 0) lines.AddRange(new[] { "desktopwidth:i:" + Width, "desktopheight:i:" + Height });
            File.WriteAllLines(file, lines, Encoding.Unicode);
        }

        public void OpenExternal()
        {
            string file = Path.Combine(Path.GetTempPath(), "SSHManager-" + Guid.NewGuid().ToString("N") + ".rdp");
            try
            {
                Export(file);
                var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe"), Terminal.Quote(file))
                { UseShellExecute = false });
                process.Exited += (sender, args) => { DeleteTemporary(file); process.Dispose(); };
                process.EnableRaisingEvents = true;
            }
            catch { DeleteTemporary(file); throw; }
        }

        private static void DeleteTemporary(string file)
        {
            try { File.Delete(file); }
            catch (Exception error) when (Theme.IsFileError(error)) { }
        }
    }
}
