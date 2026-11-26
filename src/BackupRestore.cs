using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace SshManager
{
    internal sealed class BackupRestore : IDisposable
    {
        private readonly List<Change> changes = new List<Change>();
        private readonly string sshDirectory, settingsDirectory;
        private readonly PasswordUpdates passwords = new PasswordUpdates();

        public BackupRestore(IDictionary<string, byte[]> entries, string sshDirectory, string settingsPath)
        {
            this.sshDirectory = Path.GetFullPath(sshDirectory);
            settingsPath = Path.GetFullPath(settingsPath);
            settingsDirectory = Path.GetDirectoryName(settingsPath);

            byte[] config, tabs;
            if (!entries.TryGetValue(BackupSnapshot.ConfigEntry, out config) || !entries.TryGetValue(BackupSnapshot.TabsEntry, out tabs))
                throw new InvalidDataException("В архиве отсутствует конфиг или файл настроек приложения.");
            ConfigFile.Decode(config);
            Groups archived;
            using (var xml = new MemoryStream(tabs, false)) archived = GroupFile.Parse(xml);

            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.Key.Equals(BackupSnapshot.ConfigEntry, StringComparison.OrdinalIgnoreCase) ||
                    entry.Key.Equals(BackupSnapshot.TabsEntry, StringComparison.OrdinalIgnoreCase) ||
                    SshPasswords.IsBackupEntry(entry.Key)) continue;
                bool isInclude = entry.Key.StartsWith(BackupSnapshot.IncludePrefix, StringComparison.OrdinalIgnoreCase);
                if (!isInclude && !entry.Key.StartsWith(BackupSnapshot.KeyPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Неизвестный файл в архиве «" + entry.Key + "».");
                var relative = entry.Key.Substring(isInclude ? BackupSnapshot.IncludePrefix.Length : BackupSnapshot.KeyPrefix.Length);
                BackupPaths.ValidateSshDataPath(relative);
                if (isInclude) ConfigFile.Decode(entry.Value);
                Add(destinations, relative, BackupPaths.Under(this.sshDirectory, relative), entry.Value, true, isKey: !isInclude);
            }
            // Make all SSH assets available before switching either settings file.
            Add(destinations, "config", BackupPaths.Under(this.sshDirectory, "config"), config, true);
            Add(destinations, "tabs.xml", settingsPath, tabs, false);
            foreach (var destination in destinations)
                for (var parent = Path.GetDirectoryName(destination); !String.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    if (destinations.Contains(parent)) throw new InvalidDataException("Пути файлов в архиве конфликтуют.");
            var secrets = entries.Where(entry => SshPasswords.IsBackupEntry(entry.Key)).ToArray();
            if (secrets.Length == 0) return;
            var validTargets = SshPasswords.BackupTargets(config).ToDictionary(target => SshPasswords.BackupPrefix + target.Id,
                StringComparer.OrdinalIgnoreCase);
            var validDesktops = archived.Desktops.ToDictionary(profile => RdpPasswords.BackupPrefix + profile.Id,
                StringComparer.OrdinalIgnoreCase);
            var validVnc = archived.Vnc.ToDictionary(profile => VncPasswords.BackupPrefix + profile.Id,
                StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var secret in secrets)
                {
                    if (!SshPasswords.IsValid(secret.Value))
                        throw new InvalidDataException("Пароль в архиве повреждён.");
                    if (VncPasswords.IsBackupEntry(secret.Key))
                    {
                        if (!validVnc.TryGetValue(secret.Key, out var vnc))
                            throw new InvalidDataException("VNC-пароль в архиве не соответствует ни одному VNC-подключению.");
                        passwords.Add(vnc, secret.Value);
                    }
                    else if (RdpPasswords.IsBackupEntry(secret.Key))
                    {
                        if (!validDesktops.TryGetValue(secret.Key, out var desktop))
                            throw new InvalidDataException("RDP-пароль в архиве не соответствует ни одному RDP-подключению.");
                        passwords.Add(desktop, secret.Value);
                    }
                    else if (validTargets.TryGetValue(secret.Key, out var target)) passwords.Add(target, secret.Value);
                    else throw new InvalidDataException("Пароль в архиве не соответствует ни одному подключению или повреждён.");
                }
            }
            catch { passwords.Dispose(); throw; }
        }

        public int ChangeCount { get { return changes.Count; } }
        public int ReplaceCount { get { return changes.Count(item => item.Before != null); } }
        public int PasswordCount => passwords.Count;
        public int PasswordReplaceCount => passwords.ReplaceCount;
        public int DesktopPasswordCount => passwords.DesktopCount;
        public int DesktopPasswordReplaceCount => passwords.DesktopReplaceCount;
        public IEnumerable<string> Replacements { get { return changes.Where(item => item.Before != null).Select(item => item.Path); } }
        public void Dispose() { passwords.Dispose(); }

        public IList<string> Apply()
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var sshRecovery = Path.Combine(sshDirectory, BackupPaths.RestoreFolder, stamp);
            var settingsRecovery = Path.Combine(settingsDirectory, BackupPaths.RestoreFolder, stamp);
            var originalFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var applied = new List<Change>();

            using (var configLock = new RestoreLock(ConfigFile.WriteLock))
            using (var settingsLock = new RestoreLock(GroupFile.WriteLock))
            using (var identity = WindowsIdentity.GetCurrent())
            {
                try
                {
                    foreach (var item in changes)
                    {
                        CheckBefore(item);
                        var folder = Path.GetDirectoryName(item.Path);
                        Directory.CreateDirectory(folder);
                        BackupPaths.RequireRegularPath(item.Path);
                        item.Staged = Path.Combine(folder, ".ssh-manager-" + Guid.NewGuid().ToString("N") + ".tmp");
                        Stage(item, identity.User);
                        if (item.Before == null) continue;
                        string recovery = item.IsSshFile ? sshRecovery : settingsRecovery;
                        item.Original = BackupPaths.Under(recovery, item.Relative);
                        BackupPaths.RequireRegularPath(item.Original);
                        Directory.CreateDirectory(Path.GetDirectoryName(item.Original));
                        originalFolders.Add(recovery);
                    }

                    passwords.Apply(() =>
                    {
                        foreach (var item in changes)
                        {
                            CheckBefore(item);
                            if (item.IsKey && item.Original != null)
                            {
                                // Moving the prepared key preserves its private ACL instead of merging the old ACL into it.
                                File.Move(item.Path, item.Original);
                                applied.Add(item);
                                File.Move(item.Staged, item.Path);
                            }
                            else
                            {
                                if (item.Original == null) File.Move(item.Staged, item.Path);
                                else File.Replace(item.Staged, item.Path, item.Original);
                                applied.Add(item);
                            }
                        }
                    });
                }
                catch (Exception error)
                {
                    var rollbackErrors = RollBack(applied);
                    if (rollbackErrors.Count > 0)
                        throw new IOException("Импорт остановлен. Часть файлов не удалось вернуть автоматически. " +
                            "Копии прежних файлов доступны в папках восстановления:\n" + String.Join("\n", originalFolders) +
                            "\n\nФайлы, требующие проверки:\n" + String.Join("\n", rollbackErrors), error);
                    throw;
                }
                finally
                {
                    foreach (var item in changes)
                        if (item.Staged != null && File.Exists(item.Staged))
                            try { File.Delete(item.Staged); }
                            catch (Exception ex) when (Theme.IsFileError(ex)) { /* A failed cleanup cannot undo an applied restore. */ }
                }
            }
            return originalFolders.ToArray();
        }

        private void Add(HashSet<string> destinations, string relative, string path, byte[] data, bool ssh, bool isKey = false)
        {
            if (!destinations.Add(path)) throw new InvalidDataException("В архиве повторяется файл «" + relative + "».");
            var before = Existing(path);
            if (before == null || !before.SequenceEqual(data)) changes.Add(new Change(relative, path, data, before, ssh, isKey));
        }

        private static byte[] Existing(string path)
        {
            BackupPaths.RequireRegularPath(path);
            FileAttributes attributes;
            if (!BackupPaths.TryAttributes(path, out attributes)) return null;
            if ((attributes & FileAttributes.Directory) != 0) throw new IOException("На месте файла уже есть папка «" + path + "».");
            return BackupArchive.ReadLimitedFile(path);
        }

        private static void CheckBefore(Change item)
        {
            var current = Existing(item.Path);
            if (item.Before == null ? current != null : current == null || !current.SequenceEqual(item.Before))
                throw new IOException("Файл изменился после проверки архива. Начните импорт заново. «" + item.Path + "».");
        }

        private static void Stage(Change item, SecurityIdentifier user)
        {
            FileStream stream;
            if (item.IsSshFile)
            {
                var access = new FileSecurity();
                access.SetOwner(user);
                access.SetAccessRuleProtection(true, false);
                access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                stream = new FileStream(item.Staged, FileMode.CreateNew, FileSystemRights.WriteData, FileShare.None, 4096, FileOptions.None, access);
            }
            else stream = new FileStream(item.Staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using (stream)
            {
                stream.Write(item.Data, 0, item.Data.Length);
                stream.Flush(true);
            }
        }

        private static IList<string> RollBack(List<Change> applied)
        {
            var failed = new List<string>();
            for (int index = applied.Count - 1; index >= 0; index--)
            {
                var item = applied[index];
                try
                {
                    var current = Existing(item.Path);
                    if (item.IsKey && item.Original != null)
                    {
                        if (current != null)
                        {
                            if (!current.SequenceEqual(item.Data)) { failed.Add(item.Path); continue; }
                            File.Move(item.Path, item.Staged);
                        }
                        File.Move(item.Original, item.Path);
                        continue;
                    }
                    if (current == null || !current.SequenceEqual(item.Data)) { failed.Add(item.Path); continue; }
                    if (item.Original == null) File.Delete(item.Path);
                    else File.Replace(item.Original, item.Path, null);
                }
                catch (Exception ex) when (Theme.IsFileError(ex) || ex is InvalidDataException) { failed.Add(item.Path); }
            }
            return failed;
        }

        private sealed class Change
        {
            public readonly string Relative, Path;
            public readonly byte[] Data, Before;
            public readonly bool IsSshFile, IsKey;
            public string Staged, Original;

            public Change(string relative, string path, byte[] data, byte[] before, bool isSshFile, bool isKey)
            {
                Relative = relative;
                Path = path;
                Data = data;
                Before = before;
                IsSshFile = isSshFile;
                IsKey = isKey;
            }
        }

        private sealed class RestoreLock : IDisposable
        {
            private readonly Mutex mutex;

            public RestoreLock(string name)
            {
                mutex = new Mutex(false, name);
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("Файлы SSH Manager сейчас заняты.");
                }
                catch (AbandonedMutexException) { }
                catch { mutex.Dispose(); throw; }
            }

            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
}
