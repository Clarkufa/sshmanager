using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SshManager
{
    internal static class BackupPaths
    {
        private static readonly Regex DeviceName = new Regex(@"^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ShortPathAlias = new Regex(@"^[^.]{1,6}~[0-9]+(?:\.|$)", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(new[]
        {
            "authorized_keys", "authorized_keys2", "known_hosts", "known_hosts.old", "rc", "environment", "config.bak"
        }, StringComparer.OrdinalIgnoreCase);

        public const string RestoreFolder = ".ssh-manager-restore";

        public static void ValidateRelative(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 1024 || path.Split('/').Any(part => part.Length == 0 ||
                part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || DeviceName.IsMatch(part) || ShortPathAlias.IsMatch(part)))
                throw new InvalidDataException("Архив содержит недопустимый путь к файлу.");
        }

        public static void ValidateSshDataPath(string relative)
        {
            ValidateRelative(relative);
            if (IsReserved(relative))
                throw new InvalidDataException("Архив содержит зарезервированный путь «" + relative + "».");
        }

        public static bool IsReserved(string relative)
        {
            var first = relative.Split('/')[0];
            return first.Equals(RestoreFolder, StringComparison.OrdinalIgnoreCase) ||
                first.Equals("config", StringComparison.OrdinalIgnoreCase) || ReservedNames.Contains(first);
        }

        public static string RelativeTo(string directory, string path)
        {
            var prefix = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length).Replace('\\', '/') : null;
        }

        public static string Under(string root, string relative)
        {
            ValidateRelative(relative);
            var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (RelativeTo(root, destination) == null) throw new InvalidDataException("Путь архива выходит за папку восстановления.");
            return destination;
        }

        public static void RequireRegularPath(string path)
        {
            for (var current = path; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                if (TryAttributes(current, out attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Резервное копирование не обрабатывает ссылки и точки подключения: «" + current + "».");
            }
        }

        public static bool TryAttributes(string path, out FileAttributes attributes)
        {
            try { attributes = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { attributes = 0; return false; }
            catch (DirectoryNotFoundException) { attributes = 0; return false; }
        }

        public static bool IsLocalFile(string path)
        {
            var root = Path.GetPathRoot(path);
            if (root == null || root.Length != 3 || root[1] != ':') return false;
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network) return false;
            try { RequireRegularPath(path); }
            catch (IOException) { return false; }
            return File.Exists(path);
        }

        public static IEnumerable<string> LocalFiles(string directory)
        {
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count != 0)
            {
                var folder = pending.Pop();
                FileAttributes attributes;
                if (!TryAttributes(folder, out attributes) || (attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    if (!TryAttributes(path, out attributes) || (attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) == 0) yield return path;
                    else if (!Path.GetFileName(path).Equals(RestoreFolder, StringComparison.OrdinalIgnoreCase)) pending.Push(path);
                }
            }
        }
    }

    internal sealed class BackupSnapshot
    {
        public const string TabsEntry = "app/tabs.xml";
        public const string ConfigEntry = "ssh/config";
        public const string KeyPrefix = "ssh/keys/";
        public const string IncludePrefix = "ssh/includes/";
        private const string ExternalFolder = "ssh-manager-files";

        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> omissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string sshDirectory, userDirectory;
        private long total;

        public BackupSnapshot(string sshDirectory, string settingsPath)
        {
            this.sshDirectory = Path.GetFullPath(sshDirectory);
            userDirectory = Path.GetDirectoryName(this.sshDirectory);
            BackupPaths.RequireRegularPath(this.sshDirectory);
            BackupPaths.RequireRegularPath(settingsPath);
            var settings = File.Exists(settingsPath) ? BackupArchive.ReadLimitedFile(settingsPath) : Encoding.UTF8.GetBytes("<tabs />\n");
            using (var xml = new MemoryStream(settings, false)) GroupFile.Parse(xml);
            Add(TabsEntry, settings);

            string config = Path.Combine(this.sshDirectory, "config");
            if (BackupPaths.IsLocalFile(config)) AddConfiguration(config, ConfigEntry, "config", 0);
            else Add(ConfigEntry, new byte[0]);

            foreach (var path in BackupPaths.LocalFiles(this.sshDirectory))
            {
                if (BackupPaths.IsReserved(BackupPaths.RelativeTo(this.sshDirectory, path)) ||
                    !BackupPaths.IsLocalFile(path) || !KeyFiles.IsKey(path)) continue;
                AddKey(path);
            }
        }

        public int KeyCount { get { return Files.Keys.Count(name => name.StartsWith(KeyPrefix, StringComparison.Ordinal)); } }
        public int OmittedCount { get { return omissions.Count; } }

        public int AddPasswords()
        {
            int added = 0;
            foreach (var target in SshPasswords.BackupTargets(Files[ConfigEntry]))
            {
                var password = SshPasswords.Read(target);
                if (password == null) continue;
                try { Add(SshPasswords.BackupPrefix + target.Id, password); }
                catch { SshPasswords.Clear(password); throw; }
                added++;
            }
            return added;
        }

        public int AddDesktopPasswords()
        {
            Groups groups;
            using (var xml = new MemoryStream(Files[TabsEntry], false)) groups = GroupFile.Parse(xml);
            int added = 0;
            foreach (var profile in groups.Desktops)
            {
                var password = RdpPasswords.ReadBytes(profile.Id);
                if (password == null) continue;
                try { Add(RdpPasswords.BackupPrefix + profile.Id, password); }
                catch { SshPasswords.Clear(password); throw; }
                added++;
            }
            foreach (var profile in groups.Vnc)
            {
                var password = VncPasswords.ReadBytes(profile.Id);
                if (password == null) continue;
                try { Add(VncPasswords.BackupPrefix + profile.Id, password); }
                catch { SshPasswords.Clear(password); throw; }
                added++;
            }
            return added;
        }

        public static void ClearPasswords(IDictionary<string, byte[]> entries)
        {
            foreach (var entry in entries.Where(item => SshPasswords.IsBackupEntry(item.Key)).ToArray())
            {
                SshPasswords.Clear(entry.Value);
                entries.Remove(entry.Key);
            }
        }

        private void AddConfiguration(string path, string entryName, string relative, int depth)
        {
            if (sources.ContainsKey(path)) return;
            if (depth > 16) throw new InvalidDataException("Слишком много уровней вложенных файлов Include.");
            BackupPaths.RequireRegularPath(path);
            sources.Add(path, relative);
            var document = new ConfigDocument(ConfigFile.Decode(BackupArchive.ReadLimitedFile(path)));
            foreach (var directive in document.Directives)
            {
                if ((directive.Keyword == "identityfile" || directive.Keyword == "certificatefile") && directive.Args.Count == 1)
                {
                    if (directive.Value.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
                    var key = Resolve(directive.Value, Environment.CurrentDirectory);
                    if (key != null && IsBackupFile(key) && KeyFiles.IsKey(key)) directive.ReplaceValue(SshPath(AddKey(key)));
                    else omissions.Add(directive.Value);
                }
                else if (directive.Keyword == "include")
                {
                    for (int i = directive.Args.Count - 1; i >= 0; i--)
                    {
                        var value = directive.Args[i].Value;
                        var replacements = ExpandInclude(value).Where(IsBackupFile).Select(found => AddInclude(found, depth + 1)).ToArray();
                        if (replacements.Length > 0) directive.ReplaceArgument(i, String.Join(" ", replacements.Select(SshPath)));
                        else omissions.Add(value);
                    }
                }
            }
            Add(entryName, Encoding.UTF8.GetBytes(document.ToString()));
        }

        private string AddInclude(string path, int depth)
        {
            string relative;
            if (sources.TryGetValue(path, out relative)) return relative;
            relative = PortablePath(path);
            AddConfiguration(path, IncludePrefix + relative, relative, depth);
            return relative;
        }

        private string AddKey(string path)
        {
            string relative;
            if (sources.TryGetValue(path, out relative)) return relative;
            BackupPaths.RequireRegularPath(path);
            relative = PortablePath(path);
            sources.Add(path, relative);
            Add(KeyPrefix + relative, BackupArchive.ReadLimitedFile(path));
            if (KeyFiles.IsPrivateKey(path))
            {
                foreach (var suffix in new[] { ".pub", "-cert.pub" })
                {
                    string paired = path + suffix;
                    if (IsBackupFile(paired) && KeyFiles.IsKey(paired)) AddKey(paired);
                }
            }
            return relative;
        }

        private string PortablePath(string path)
        {
            var relative = BackupPaths.RelativeTo(sshDirectory, path);
            if (relative == null)
            {
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetDirectoryName(path).ToUpperInvariant()));
                    relative = ExternalFolder + "/" + BitConverter.ToString(hash, 0, 12).Replace("-", "").ToLowerInvariant() + "/" + Path.GetFileName(path);
                }
            }
            BackupPaths.ValidateSshDataPath(relative);
            return relative;
        }

        private bool IsBackupFile(string path)
        {
            var relative = BackupPaths.RelativeTo(sshDirectory, path);
            return (relative == null || !BackupPaths.IsReserved(relative)) && BackupPaths.IsLocalFile(path);
        }

        private string Resolve(string value, string baseDirectory)
        {
            const string percent = "\u0001";
            value = value.Replace("%%", percent).Replace("%d", userDirectory);
            value = Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
            if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                value = Path.Combine(userDirectory, value.Substring(2));
            if (value.IndexOf('%') >= 0 || value.Contains("${") || value.StartsWith("~", StringComparison.Ordinal)) return null;
            value = value.Replace(percent, "%");
            if (value.StartsWith("\\", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal) ||
                (value.Length > 1 && value[1] == ':' && (value.Length < 3 || (value[2] != '\\' && value[2] != '/')))) return null;
            try { return Path.GetFullPath(Path.Combine(baseDirectory, value)); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        private IEnumerable<string> ExpandInclude(string value)
        {
            int split = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
            var pattern = value.Substring(split + 1);
            if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                var path = Resolve(value, sshDirectory);
                return path != null && BackupPaths.IsLocalFile(path) ? new[] { path } : new string[0];
            }

            var directory = split < 0 ? sshDirectory : Resolve(value.Substring(0, split + 1), sshDirectory);
            if (directory == null || Path.GetPathRoot(directory).Length != 3 ||
                new DriveInfo(Path.GetPathRoot(directory)).DriveType == DriveType.Network || !Directory.Exists(directory)) return new string[0];
            BackupPaths.RequireRegularPath(directory);
            var match = new Regex(@"\A" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + @"\z", RegexOptions.IgnoreCase);
            return Directory.EnumerateFiles(directory).Where(file =>
            {
                var name = Path.GetFileName(file);
                return (pattern.StartsWith(".", StringComparison.Ordinal) || !name.StartsWith(".", StringComparison.Ordinal)) && match.IsMatch(name);
            }).OrderBy(file => file, StringComparer.Ordinal).ToArray();
        }

        private static string SshPath(string relative)
        {
            return ConfigDocument.Quote("~/.ssh/" + relative.Replace("%", "%%"));
        }

        private void Add(string name, byte[] bytes)
        {
            BackupPaths.ValidateRelative(name);
            if (Files.Count >= BackupArchive.EntryLimit || bytes.Length > BackupArchive.FileLimit || (total += bytes.Length) > BackupArchive.TotalLimit)
                throw BackupArchive.TooLarge();
            Files.Add(name, bytes);
        }
    }
}
