using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Xml.Linq;

namespace SshManager
{
    /// <summary>A saved VNC connection (docs/ui-spec.md §8), stored as &lt;vnc&gt; in tabs.xml next to &lt;rdp&gt;.</summary>
    internal sealed class VncProfile
    {
        public const int DefaultPort = 5900;
        public const string FitScale = "fit", NativeScale = "native";
        public readonly string Id, Name, Host, User, GroupId, Via, FilesVia, Scale;
        public readonly int Port;
        public readonly bool ViewOnly, Clipboard, FullScreen;

        public VncProfile(string id, string name, string host, int port, string user, string groupId,
            string scale = FitScale, bool viewOnly = false, bool clipboard = true, bool fullScreen = false, string via = "")
            : this(id, name, host, port, user, groupId, scale, viewOnly, clipboard, fullScreen, via, "") { }

        public VncProfile(string id, string name, string host, int port, string user, string groupId,
            string scale, bool viewOnly, bool clipboard, bool fullScreen, string via, string filesVia)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Некорректный идентификатор VNC-профиля.");
            Id = id.ToLowerInvariant();
            Name = Text(name, 80);
            if (Name.Length == 0) throw new ArgumentException("Укажите название VNC-подключения.");
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
            GroupId = groupId;
            Scale = scale == NativeScale ? NativeScale : FitScale;
            ViewOnly = viewOnly;
            Clipboard = clipboard;
            FullScreen = fullScreen;
            Via = (via ?? "").Trim();
            if (Via.Length > 0 && !ConfigDocument.IsAlias(Via))
                throw new ArgumentException("Выберите SSH-сервер из списка или «Напрямую».");
            FilesVia = (filesVia ?? "").Trim();
            if (FilesVia.Length > 0 && !ConfigDocument.IsAlias(FilesVia))
                throw new ArgumentException("Выберите SSH-профиль для файлов в настройках VNC-подключения.");
        }

        public string Address => (Host.Contains(":") ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);
        public string Key => "VNC:" + Id;

        public VncProfile InGroup(string id) =>
            new VncProfile(Id, Name, Host, Port, User, id, Scale, ViewOnly, Clipboard, FullScreen, Via, FilesVia);

        private static string Text(string input, int limit)
        {
            string value = (input ?? "").Trim();
            if (value.Length > limit || value.Any(char.IsControl)) throw new ArgumentException("Одно из полей VNC содержит недопустимый текст.");
            return value;
        }

        public static VncProfile Parse(XElement node, string groupId)
        {
            var via = (string)node.Attribute("via") ?? "";
            var filesVia = (string)node.Attribute("filesVia") ?? "";
            return new VncProfile((string)node.Attribute("id"), (string)node.Attribute("name"), (string)node.Attribute("host"),
                (int?)node.Attribute("port") ?? DefaultPort, (string)node.Attribute("user"), groupId,
                (string)node.Attribute("scale"), (string)node.Attribute("viewOnly") == "1", (string)node.Attribute("clipboard") != "0",
                (string)node.Attribute("fullScreen") == "1", ConfigDocument.IsAlias(via) ? via : "",
                ConfigDocument.IsAlias(filesVia) ? filesVia : "");
        }

        public XElement ToXml() => new XElement("vnc", new XAttribute("id", Id), new XAttribute("name", Name),
            new XAttribute("host", Host), new XAttribute("port", Port), new XAttribute("user", User),
            GroupId == null ? null : new XAttribute("group", GroupId), new XAttribute("scale", Scale),
            ViewOnly ? new XAttribute("viewOnly", 1) : null, new XAttribute("clipboard", Clipboard ? 1 : 0),
            FullScreen ? new XAttribute("fullScreen", 1) : null, Via.Length == 0 ? null : new XAttribute("via", Via),
            FilesVia.Length == 0 ? null : new XAttribute("filesVia", FilesVia));
    }
}
