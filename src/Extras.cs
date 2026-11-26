using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;

namespace SshManager
{
    // Stage 2 helpers shared by the catalog, the sessions page and the dialogs (docs/ui-spec.md §7).

    /// <summary>«user@host:port», «ssh://…», «rdp://[user@]host[:port]» or «vnc://…» typed into «Быстрое подключение».</summary>
    internal sealed class QuickTarget
    {
        private static readonly Regex Pattern = new Regex(@"\A(?:([^@\s:]+)@)?(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9_.-]+)(?::([0-9]{1,5}))?\z");
        public readonly bool Rdp, Vnc;
        public readonly string User, Host;
        public readonly int Port;

        public QuickTarget(string input)
        {
            string text = (input ?? "").Trim();
            foreach (var prefix in new[] { "rdp://", "vnc://", "ssh://" })
            {
                if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                Rdp = prefix == "rdp://";
                Vnc = prefix == "vnc://";
                text = text.Substring(prefix.Length).TrimEnd('/');
            }
            var match = Pattern.Match(text);
            if (!match.Success) throw new ArgumentException("Введите адрес как user@host:port, rdp://host:port или vnc://host:port.");
            Port = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : DefaultPort;
            if (Port < 1 || Port > 65535) throw new ArgumentException("Порт должен быть числом от 1 до 65535.");
            User = match.Groups[1].Value;
            if (User.Any(ch => "#'\"\\".Contains(ch))) throw new ArgumentException("Укажите имя пользователя без кавычек и решётки.");
            Host = match.Groups[2].Value.Trim('[', ']');
        }

        /// <summary>ssh arguments that need no config entry.</summary>
        public string SshArguments => "-p " + Port.ToString(CultureInfo.InvariantCulture) + " " +
            Terminal.Quote((User.Length == 0 ? "" : User + "@") + Host);

        public string Address => (User.Length == 0 ? "" : User + "@") + (Host.Contains(":") ? "[" + Host + "]" : Host) +
            (Port == DefaultPort ? "" : ":" + Port.ToString(CultureInfo.InvariantCulture));

        private int DefaultPort => Rdp ? 3389 : Vnc ? VncProfile.DefaultPort : 22;

        /// <summary>A free config alias based on the host name: "db.example.com", then "db.example.com-2".</summary>
        public static string Alias(string host, IEnumerable<string> existing)
        {
            var start = Regex.Replace(host ?? "", @"[^A-Za-z0-9_.-]", "-").Trim('-', '.');
            if (start.Length == 0 || !ConfigDocument.IsAlias(start)) start = "server";
            var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            var alias = start;
            for (int number = 2; taken.Contains(alias); number++) alias = start + "-" + number.ToString(CultureInfo.InvariantCulture);
            return alias;
        }
    }

    internal sealed class FoundConnection
    {
        public readonly bool Rdp, Vnc;
        public readonly string Name, Host, User, Domain, Source;
        public readonly int Port;

        public FoundConnection(bool rdp, string name, string host, int port, string user, string domain, string source, bool vnc = false)
        {
            Rdp = rdp;
            Vnc = vnc && !rdp;
            Name = String.IsNullOrWhiteSpace(name) ? host : name.Trim();
            Host = host.Trim();
            Port = port >= 1 && port <= 65535 ? port : rdp ? 3389 : vnc ? VncProfile.DefaultPort : 22;
            User = (user ?? "").Trim();
            Domain = (domain ?? "").Trim();
            Source = source;
        }

        public string Address => (User.Length == 0 ? "" : User + "@") + Host + ":" + Port.ToString(CultureInfo.InvariantCulture);
        public string Protocol => Rdp ? "RDP" : Vnc ? "VNC" : "SSH";
    }

    /// <summary>«Импорт из других программ». Passwords are never imported: the other programs encrypt them.</summary>
    internal static class Importers
    {
        public static List<FoundConnection> PuTTY()
        {
            var found = new List<FoundConnection>();
            using (var sessions = Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions"))
            {
                if (sessions == null) return found;
                foreach (var key in sessions.GetSubKeyNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                {
                    using (var session = sessions.OpenSubKey(key))
                    {
                        if (session == null || !String.Equals(session.GetValue("Protocol") as string ?? "ssh", "ssh", StringComparison.OrdinalIgnoreCase)) continue;
                        var host = (session.GetValue("HostName") as string ?? "").Trim();
                        var user = (session.GetValue("UserName") as string ?? "").Trim();
                        int at = host.IndexOf('@');
                        if (user.Length == 0 && at > 0) { user = host.Substring(0, at); host = host.Substring(at + 1); }
                        if (host.Length == 0) continue;
                        int port = session.GetValue("PortNumber") is int number ? number : 22;
                        found.Add(new FoundConnection(false, Uri.UnescapeDataString(key), host, port, user, "", "PuTTY"));
                    }
                }
            }
            return found;
        }

        public static List<FoundConnection> Read(string path)
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Файл слишком большой.");
            return Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase) ? MRemoteNG(path) : MobaXterm(path);
        }

        /// <summary>confCons.xml. A fully encrypted file has no readable connections.</summary>
        public static List<FoundConnection> MRemoteNG(string path)
        {
            var found = new List<FoundConnection>();
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "Node" || reader.GetAttribute("Type") != "Connection") continue;
                    bool rdp = false, vnc = false;
                    switch (reader.GetAttribute("Protocol"))
                    {
                        case "SSH1": case "SSH2": break;
                        case "RDP": rdp = true; break;
                        case "VNC": vnc = true; break;
                        default: continue;
                    }
                    var host = reader.GetAttribute("Hostname") ?? "";
                    if (host.Trim().Length == 0) continue;
                    int.TryParse(reader.GetAttribute("Port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port);
                    found.Add(new FoundConnection(rdp, reader.GetAttribute("Name"), host, port, reader.GetAttribute("Username"),
                        rdp ? reader.GetAttribute("Domain") : "", "mRemoteNG", vnc));
                }
            }
            return found;
        }

        /// <summary>.mxtsessions or MobaXterm.ini: «Name=#icon#type%host%port%user%…», type 0 is SSH and 4 is RDP.</summary>
        public static List<FoundConnection> MobaXterm(string path)
        {
            var found = new List<FoundConnection>();
            string text;
            using (var reader = new StreamReader(path, Encoding.Default, true)) text = reader.ReadToEnd();
            foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = raw.Trim();
                int equals = line.IndexOf('=');
                if (equals <= 0 || line.StartsWith("[") || line.StartsWith("SubRep") || line.StartsWith("ImgNum")) continue;
                var fields = line.Substring(equals + 1).Split('%');
                if (fields.Length < 4) continue;
                var kind = fields[0].Split('#').Last();
                // Session types: 0 SSH, 4 RDP, 5 VNC.
                if ((kind != "0" && kind != "4" && kind != "5") || fields[1].Trim().Length == 0) continue;
                int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port);
                found.Add(new FoundConnection(kind == "4", line.Substring(0, equals), fields[1], port, fields[3], "", "MobaXterm", kind == "5"));
            }
            return found;
        }
    }

    internal static class SshKeys
    {
        private static readonly Regex PublicKey = new Regex(@"\A(?:ssh|ecdsa|sk)-[A-Za-z0-9@.-]+ [A-Za-z0-9+/=]+(?: [^'\r\n]*)?\z");

        /// <summary>ssh-keygen -t ed25519 without a passphrase; returns the private key file.</summary>
        public static string Generate(string sshDirectory, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith("."))
                throw new ArgumentException("Укажите имя файла без папок.");
            Directory.CreateDirectory(sshDirectory);
            var path = Path.Combine(sshDirectory, name);
            if (File.Exists(path) || File.Exists(path + ".pub")) throw new ArgumentException("Файл " + path + " уже существует.");
            var keygen = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");
            if (!File.Exists(keygen)) throw new IOException("Установите компонент «Клиент OpenSSH» в дополнительных компонентах Windows.");
            var comment = Environment.UserName + "@" + Environment.MachineName;
            var start = new ProcessStartInfo(keygen, "-q -t ed25519 -N \"\" -C " + Terminal.Quote(comment) + " -f " + Terminal.Quote(path))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                RedirectStandardInput = true
            };
            using (var process = Process.Start(start))
            {
                process.StandardInput.Close();
                var errors = process.StandardError.ReadToEndAsync();
                process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(30000)) { process.Kill(); throw new IOException("ssh-keygen не ответил за 30 секунд."); }
                if (process.ExitCode != 0) throw new IOException("ssh-keygen завершился с ошибкой.\n" + errors.Result.Trim());
            }
            return path;
        }

        /// <summary>A remote shell command that appends the public key once to ~/.ssh/authorized_keys.</summary>
        public static string CopyCommand(string publicKeyFile)
        {
            if (new FileInfo(publicKeyFile).Length > 64 * 1024) throw new ArgumentException("Файл не похож на публичный ключ OpenSSH.");
            var key = File.ReadAllText(publicKeyFile).Trim();
            if (!PublicKey.IsMatch(key)) throw new ArgumentException("Файл не похож на публичный ключ OpenSSH.");
            var quoted = "'" + key + "'";
            return "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
                "(grep -qxF " + quoted + " ~/.ssh/authorized_keys || echo " + quoted + " >> ~/.ssh/authorized_keys) && " +
                "echo 'Ключ добавлен в ~/.ssh/authorized_keys.'";
        }
    }
}
