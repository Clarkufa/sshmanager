using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace SshManager
{
    internal enum ServerSort { Original, Name, NameDescending, Address, AddressDescending }

    internal static class ServerOrder
    {
        public static ServerSort Parse(string value) => Enum.TryParse(value, true, out ServerSort order) && Enum.IsDefined(typeof(ServerSort), order)
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

    /// <summary>RDP «Дополнительно» (docs/ui-spec.md §7).</summary>
    internal sealed class RdpOptions
    {
        public static readonly RdpOptions None = new RdpOptions(null, false, false, "", "");
        private static readonly System.Text.RegularExpressions.Regex GatewayPattern =
            new System.Text.RegularExpressions.Regex(@"\A[A-Za-z0-9_.:\[\]-]{1,253}\z");
        public readonly string[] Drives;
        public readonly bool Printers, Multimon;
        public readonly string Gateway, GatewayUser;

        public RdpOptions(IEnumerable<string> drives, bool printers, bool multimon, string gateway, string gatewayUser)
        {
            Drives = (drives ?? new string[0]).Select(path => (path ?? "").Trim()).Where(path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (Drives.Length > 16 || Drives.Any(path => !IsLocalFolder(path)))
                throw new ArgumentException("Укажите до 16 локальных папок вида C:\\Папка.");
            Printers = printers;
            Multimon = multimon;
            Gateway = (gateway ?? "").Trim();
            if (Gateway.Length > 0 && !GatewayPattern.IsMatch(Gateway))
                throw new ArgumentException("Укажите шлюз RD Gateway как DNS-имя или IP, при необходимости с портом.");
            GatewayUser = (gatewayUser ?? "").Trim();
            if (GatewayUser.Length > 128 || GatewayUser.Any(char.IsControl))
                throw new ArgumentException("Имя пользователя шлюза слишком длинное.");
        }

        public bool IsEmpty => Drives.Length == 0 && !Printers && !Multimon && Gateway.Length == 0;

        /// <summary>A rooted Windows folder: "C:\..." with no ";" (the .rdp list separator).</summary>
        public static bool IsLocalFolder(string path) => path.Length >= 2 && path.Length <= 260 && path[1] == ':' &&
            char.IsLetter(path[0]) && path[0] < 128 && (path.Length == 2 || path[2] == '\\') && path.IndexOf(';') < 0 &&
            path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

        /// <summary>The drive letters to redirect: the ActiveX client and mstsc share whole drives, not folders.</summary>
        public IEnumerable<char> DriveLetters => Drives.Select(path => char.ToUpperInvariant(path[0])).Distinct();

        /// <summary>Reads tabs.xml; folders saved by the macOS or Linux app are skipped rather than rejected.</summary>
        public static RdpOptions Parse(XElement node)
        {
            var drives = ((string)node.Attribute("drives") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(IsLocalFolder).Take(16);
            string gateway = (string)node.Attribute("gateway") ?? "", user = (string)node.Attribute("gatewayUser") ?? "";
            if (gateway.Length > 0 && !GatewayPattern.IsMatch(gateway)) gateway = user = "";
            if (user.Length > 128 || user.Any(char.IsControl)) user = "";
            return new RdpOptions(drives, (string)node.Attribute("printers") == "1", (string)node.Attribute("multimon") == "1", gateway, user);
        }

        public IEnumerable<XAttribute> ToXml()
        {
            if (Drives.Length > 0) yield return new XAttribute("drives", String.Join(";", Drives));
            if (Printers) yield return new XAttribute("printers", 1);
            if (Multimon) yield return new XAttribute("multimon", 1);
            if (Gateway.Length > 0) yield return new XAttribute("gateway", Gateway);
            if (GatewayUser.Length > 0) yield return new XAttribute("gatewayUser", GatewayUser);
        }
    }

    internal sealed class RdpProfile
    {
        public readonly string Id, Name, Host, User, Domain, GroupId;
        public readonly int Port, Width, Height;
        public readonly bool Clipboard, FullScreen;
        public readonly RdpOptions Options;

        public RdpProfile(string id, string name, string host, int port, string user, string domain, string groupId,
            int width = 0, int height = 0, bool clipboard = false, bool fullScreen = false, RdpOptions options = null)
        {
            Options = options ?? RdpOptions.None;
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

        public RdpProfile InGroup(string id) => new RdpProfile(Id, Name, Host, Port, User, Domain, id, Width, Height, Clipboard, FullScreen, Options);

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
            (string)node.Attribute("clipboard") == "1", (string)node.Attribute("fullScreen") == "1", RdpOptions.Parse(node));

        public XElement ToXml() => new XElement("rdp", new XAttribute("id", Id), new XAttribute("name", Name),
            new XAttribute("host", Host), new XAttribute("port", Port), new XAttribute("user", User), new XAttribute("domain", Domain),
            GroupId == null ? null : new XAttribute("group", GroupId), Width == 0 ? null : new XAttribute("width", Width),
            Height == 0 ? null : new XAttribute("height", Height), Clipboard ? new XAttribute("clipboard", 1) : null,
            FullScreen ? new XAttribute("fullScreen", 1) : null, Options.ToXml());

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
            var drives = Field("drivestoredirect").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(drive => drive.Trim().TrimEnd('\\')).Where(drive => drive.Length == 2 && drive[1] == ':')
                .Select(drive => drive.ToUpperInvariant() + "\\").Where(RdpOptions.IsLocalFolder).Take(16);
            string gateway = Number("gatewayusagemethod", 0) == 1 ? Field("gatewayhostname").Trim() : "";
            var options = new RdpOptions(drives, Number("redirectprinters", 0) == 1, Number("use multimon", 0) == 1,
                gateway, gateway.Length == 0 ? "" : Field("gatewayusername").Trim());
            return new RdpProfile(Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(file), host, port, user, domain,
                groupId, width, height, Number("redirectclipboard", 0) == 1, Number("screen mode id", 1) == 2, options);
        }

        public void Export(string file) => Export(file, null);

        private void Export(string file, string password)
        {
            var lines = new List<string> { "full address:s:" + Address, "username:s:" + Account,
                "screen mode id:i:" + (FullScreen ? 2 : 1), "redirectclipboard:i:" + (Clipboard ? 1 : 0),
                "enablecredsspsupport:i:1", "authentication level:i:2",
                "drivestoredirect:s:" + String.Join(";", Options.DriveLetters.Select(letter => letter + ":\\")),
                "redirectprinters:i:" + (Options.Printers ? 1 : 0), "use multimon:i:" + (Options.Multimon ? 1 : 0) };
            if (Options.Gateway.Length > 0)
            {
                lines.AddRange(new[] { "gatewayhostname:s:" + Options.Gateway, "gatewayusagemethod:i:1", "gatewayprofileusagemethod:i:1",
                    "gatewaycredentialssource:i:4", "promptcredentialonce:i:1" });
                if (Options.GatewayUser.Length > 0) lines.Add("gatewayusername:s:" + Options.GatewayUser);
            }
            if (Width != 0) lines.AddRange(new[] { "desktopwidth:i:" + Width, "desktopheight:i:" + Height });
            else lines.Add("dynamic resolution:i:1");
            if (!String.IsNullOrEmpty(password))
                lines.AddRange(new[] { "password 51:b:" + ProtectPassword(password), "prompt for credentials:i:0" });
            File.WriteAllLines(file, lines, Encoding.Unicode);
        }

        public void OpenExternal()
        {
            string file = Path.Combine(Path.GetTempPath(), "SSHManager-" + Guid.NewGuid().ToString("N") + ".rdp");
            try
            {
                Export(file, RdpPasswords.Read(this));
                var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe"), Terminal.Quote(file))
                { UseShellExecute = false });
                process.Exited += (sender, args) => { DeleteTemporary(file); process.Dispose(); };
                process.EnableRaisingEvents = true;
            }
            catch { DeleteTemporary(file); throw; }
        }

        // mstsc accepts a DPAPI blob (current user) of the UTF-16 password in "password 51:b:".
        private static string ProtectPassword(string password)
        {
            var bytes = Encoding.Unicode.GetBytes(password);
            var input = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var output = new DataBlob();
            try
            {
                var data = new DataBlob { Size = bytes.Length, Data = input.AddrOfPinnedObject() };
                if (!CryptProtectData(ref data, "psw", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output))
                    throw new IOException("Не удалось подготовить пароль для клиента Windows.", new System.ComponentModel.Win32Exception());
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return BitConverter.ToString(result).Replace("-", "");
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                input.Free();
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int Size; public IntPtr Data; }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CryptProtectData(ref DataBlob data, string description, IntPtr entropy, IntPtr reserved,
            IntPtr prompt, int flags, ref DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static void DeleteTemporary(string file)
        {
            try { File.Delete(file); }
            catch (Exception error) when (Theme.IsFileError(error)) { }
        }
    }
}
