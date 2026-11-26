using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SshManager
{
    /// <summary>One key line of a known_hosts file (docs/ui-spec.md §9).</summary>
    internal sealed class KnownHostEntry
    {
        public int Line;
        public string Marker = "";
        public string Hosts = "";
        public string KeyType = "";
        public string Key = "";
        /// <summary>The name a hashed entry matched in the last search, or null.</summary>
        public string Revealed;

        public bool Hashed => Hosts.StartsWith("|1|", StringComparison.Ordinal);
        public string Display => Hashed ? Revealed ?? "скрытое имя" : Hosts;

        public string Fingerprint
        {
            get
            {
                try
                {
                    using (var sha = SHA256.Create())
                        return "SHA256:" + Convert.ToBase64String(sha.ComputeHash(Convert.FromBase64String(Key))).TrimEnd('=');
                }
                catch (FormatException) { return "повреждённый ключ"; }
            }
        }

        /// <summary>True when this entry is for exactly <paramref name="name"/> (`host` or `[host]:port`).</summary>
        public bool IsFor(string name)
        {
            if (Hashed)
            {
                var parts = Hosts.Split('|');
                if (parts.Length != 4) return false;
                try
                {
                    using (var hmac = new HMACSHA1(Convert.FromBase64String(parts[2])))
                        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(name))) == parts[3];
                }
                catch (FormatException) { return false; }
            }
            return Hosts.Split(',').Any(pattern => !pattern.StartsWith("!", StringComparison.Ordinal) &&
                Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase));
        }
    }

    /// <summary>A known_hosts file: entries are removed or added line by line, everything else stays as it was.</summary>
    internal sealed class KnownHostsFile
    {
        public readonly string Path;
        public readonly List<KnownHostEntry> Entries = new List<KnownHostEntry>();
        private readonly List<string> lines = new List<string>();
        private string newline = "\n";

        public static string DefaultPath =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

        private KnownHostsFile(string path) { Path = path; }

        /// <summary>The name ssh writes for a host: `host`, or `[host]:port` for another port.</summary>
        public static string HostName(string host, int port)
        {
            host = host.Trim().Trim('[', ']');
            return port == 22 ? host : "[" + host + "]:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>UserKnownHostsFile as OpenSSH reads it: empty — the default file; `~` and `%d` — the home folder, `%u` — the user.</summary>
        public static string Resolve(string configured)
        {
            string value = (configured ?? "").Trim();
            if (value.Length == 0) return DefaultPath;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                value = home + value.Substring(1);
            value = value.Replace("%d", home).Replace("%u", Environment.UserName).Replace("%%", "%");
            return System.IO.Path.GetFullPath(value.Replace('/', '\\'));
        }

        /// <summary>A file path for UserKnownHostsFile: absolute or from ~, one file, no control characters.</summary>
        public static bool IsValidSetting(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 1024 || value.Any(char.IsControl) || value.Contains("\"")) return false;
            if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal)) return true;
            return Regex.IsMatch(value, @"^[A-Za-z]:[\\/]") || value.StartsWith(@"\\", StringComparison.Ordinal);
        }

        public static KnownHostsFile Load(string path)
        {
            var file = new KnownHostsFile(path);
            if (!File.Exists(path)) return file;
            var info = new FileInfo(path);
            if (info.Length > 16 * 1024 * 1024) throw new IOException("Файл known_hosts слишком большой.");
            string text = File.ReadAllText(path, new UTF8Encoding(false));
            if (text.Contains("\r\n")) file.newline = "\r\n";
            file.lines.AddRange(text.Replace("\r\n", "\n").Split('\n'));
            if (file.lines.Count > 0 && file.lines[file.lines.Count - 1].Length == 0) file.lines.RemoveAt(file.lines.Count - 1);
            for (int i = 0; i < file.lines.Count; i++)
            {
                var entry = Parse(file.lines[i]);
                if (entry == null) continue;
                entry.Line = i;
                file.Entries.Add(entry);
            }
            return file;
        }

        private static KnownHostEntry Parse(string line)
        {
            var fields = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0].StartsWith("#", StringComparison.Ordinal)) return null;
            var entry = new KnownHostEntry();
            int index = 0;
            if (fields[0].StartsWith("@", StringComparison.Ordinal)) entry.Marker = fields[index++];
            if (fields.Length < index + 3) return null;
            entry.Hosts = fields[index];
            entry.KeyType = fields[index + 1];
            entry.Key = fields[index + 2];
            return entry;
        }

        /// <summary>Entries for the search text: plain names by substring, hashed names only by the exact host name.</summary>
        public List<KnownHostEntry> Search(string query)
        {
            query = (query ?? "").Trim();
            foreach (var entry in Entries) entry.Revealed = null;
            if (query.Length == 0) return Entries.ToList();
            var names = new List<string> { query };
            var withPort = Regex.Match(query, @"^\[?([^\[\]]+?)\]?:(\d{1,5})$");
            if (withPort.Success && int.TryParse(withPort.Groups[2].Value, out var port)) names.Add(HostName(withPort.Groups[1].Value, port));
            var found = new List<KnownHostEntry>();
            foreach (var entry in Entries)
            {
                if (entry.Hashed)
                {
                    var name = names.FirstOrDefault(entry.IsFor);
                    if (name == null) continue;
                    entry.Revealed = name;
                    found.Add(entry);
                }
                else if (entry.Hosts.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || entry.KeyType.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    found.Add(entry);
            }
            return found;
        }

        public void Remove(IEnumerable<KnownHostEntry> entries)
        {
            var drop = new HashSet<int>(entries.Select(entry => entry.Line));
            Write(lines.Where((line, index) => !drop.Contains(index)).ToList());
        }

        /// <summary>Replaces the entries for <paramref name="name"/> with the scanned key lines.</summary>
        public void Replace(string name, IEnumerable<string> keyLines)
        {
            var drop = new HashSet<int>(Entries.Where(entry => entry.Marker.Length == 0 && entry.IsFor(name)).Select(entry => entry.Line));
            var next = lines.Where((line, index) => !drop.Contains(index)).ToList();
            next.AddRange(keyLines);
            Write(next);
        }

        private void Write(List<string> next)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            if (File.Exists(Path)) File.Copy(Path, Path + ".old", true);
            string temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, String.Join(newline, next) + (next.Count > 0 ? newline : ""), new UTF8Encoding(false));
                if (File.Exists(Path)) File.Replace(temp, Path, null);
                else File.Move(temp, Path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        /// <summary>`ssh-keyscan -T 10` next to ssh.exe: the key lines for host:port, with the name ssh would write.</summary>
        public static List<string> Scan(string host, int port)
        {
            host = host.Trim().Trim('[', ']');
            if (!Regex.IsMatch(host, @"^[A-Za-z0-9_.:-]+$") || host.StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException("Укажите DNS-имя, IPv4 или IPv6 без пробелов.");
            if (port < 1 || port > 65535) throw new ArgumentException("Порт должен быть числом от 1 до 65535.");
            string ssh = Terminal.SshExecutable();
            string number = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string scanner = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ssh), "ssh-keyscan.exe");
            string problem = null;
            if (File.Exists(scanner))
            {
                var keys = KeyLines(Run(scanner, "-T 10 -p " + number + " " + host, out var errors));
                if (keys.Count > 0) return keys;
                problem = LastLine(errors);
            }
            // Some Windows builds of ssh-keyscan offer a key exchange they cannot do; ssh itself then records the key it
            // checks. An empty config and a throwaway known_hosts keep the user's settings and files out of it.
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sshm-keyscan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string config = System.IO.Path.Combine(folder, "config"), known = System.IO.Path.Combine(folder, "known_hosts"),
                    global = System.IO.Path.Combine(folder, "global_known_hosts");
                File.WriteAllText(config, "");
                File.WriteAllText(global, "");
                Run(ssh, "-F \"" + config + "\" -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o \"UserKnownHostsFile=" + known +
                    "\" -o \"GlobalKnownHostsFile=" + global + "\" -o HashKnownHosts=no -o UpdateHostKeys=no -o CheckHostIP=no" +
                    " -o ConnectTimeout=10 -o PreferredAuthentications=none -l sshmanager-keyscan -p " + number + " " + host + " exit",
                    out var sshErrors);
                var keys = File.Exists(known) ? KeyLines(File.ReadAllText(known)) : new List<string>();
                if (keys.Count > 0) return keys;
                throw new IOException("Сервер не прислал ключи." + (LastLine(sshErrors) ?? problem ?? "").Insert(0, " ").TrimEnd());
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static List<string> KeyLines(string text) => text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .Where(line => Parse(line) != null).ToList();

        private static string LastLine(string text) => (text ?? "").Replace("\r\n", "\n").Split('\n').Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal));

        private static string Run(string program, string arguments, out string errors)
        {
            var info = new ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(info))
            {
                process.StandardInput.Close();
                var stderr = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(30000)) { try { process.Kill(); } catch (InvalidOperationException) { } }
                errors = stderr.Wait(2000) ? stderr.Result : "";
                return output;
            }
        }

        public static KnownHostEntry Describe(string line) => Parse(line);
    }
}
