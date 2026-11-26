using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SshManager
{
    internal sealed class PasswordTarget
    {
        private static readonly Regex HostPattern = new Regex(@"\A[A-Za-z0-9_.:-]+\z");
        public readonly string Id, Host, User, Port;

        private PasswordTarget(string alias, string host, string user, int port)
        {
            Host = host.ToLowerInvariant();
            User = user;
            Port = port.ToString(CultureInfo.InvariantCulture);
            using (var hash = SHA256.Create())
            {
                var identity = alias.ToLowerInvariant() + "\n" + Host + "\n" + User + "\n" + Port;
                Id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "");
            }
        }

        public static PasswordTarget From(Connection connection)
        {
            return connection == null ? null : From(connection.Alias, connection.Get("hostname"), connection.Get("user"), connection.Get("port"));
        }

        public static PasswordTarget From(string alias, string host, string user, string port)
        {
            if (host.Length == 0) host = alias;
            if (port.Length == 0) port = "22";
            if (!ConfigDocument.IsAlias(alias) || host.Length > 253 || !HostPattern.IsMatch(host) || host[0] == '-' ||
                (host.Contains(":") && !System.Net.IPAddress.TryParse(host, out _)) ||
                user.Length == 0 || user.Length > 255 || user.Any(ch => Char.IsControl(ch) || Char.IsWhiteSpace(ch) || "#'\"".Contains(ch)) ||
                !Int32.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 || number > 65535)
                return null;
            return new PasswordTarget(alias, host, user, number);
        }

        public bool SameDestination(PasswordTarget other)
        {
            return other != null && Host == other.Host && User == other.User && Port == other.Port;
        }

        public string Account => User + "@" + Host;

        public string Arguments => "-o HostName=" + Host + " -l " + Terminal.Quote(User) + " -p " + Port +
            " -o BatchMode=no -o PasswordAuthentication=yes -o PreferredAuthentications=password,publickey,keyboard-interactive ";
    }

    internal static class SshPasswords
    {
        private const uint Generic = 1;
        private const uint PersistLocal = 2;
        private const int NotFound = 1168;
        private const int NoLogonSession = 1312;
        // Leave space for the newline and null terminator in OpenSSH's askpass reader.
        public const int MaxBytes = 1022;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        public const string BackupPrefix = "app/passwords/";

        public static byte[] Encode(string password)
        {
            try
            {
                if (password.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0 && Utf8.GetByteCount(password) <= MaxBytes)
                    return Utf8.GetBytes(password);
            }
            catch (EncoderFallbackException) { }
            throw new ArgumentException("Пароль должен быть одной строкой длиной до " + MaxBytes + " байт.");
        }

        public static bool IsValid(byte[] password)
        {
            if (password == null || password.Length == 0 || password.Length > MaxBytes ||
                password.Any(value => value == 0 || value == 10 || value == 13)) return false;
            try { return Utf8.GetCharCount(password) > 0; }
            catch (DecoderFallbackException) { return false; }
        }

        public static bool Exists(PasswordTarget target)
        {
            var password = Read(target);
            Clear(password);
            return password != null;
        }

        public static byte[] Read(PasswordTarget target) => target == null ? null : Read(Name(target));

        internal static byte[] Read(string name)
        {
            if (!CredRead(name, Generic, 0, out IntPtr pointer))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NotFound || error == NoLogonSession) return null;
                throw VaultError("прочитать", error);
            }
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.Size <= 0 || credential.Size > MaxBytes) return null;
                var bytes = new byte[credential.Size];
                Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                for (int i = 0; i < credential.Size; i++) Marshal.WriteByte(credential.Blob, i, 0);
                if (IsValid(bytes)) return bytes;
                Clear(bytes);
                return null;
            }
            finally { CredFree(pointer); }
        }

        public static void Set(PasswordTarget target, byte[] password) => Set(Name(target), target.User, password);

        internal static void Set(string name, string user, byte[] password)
        {
            if (password == null)
            {
                if (CredDelete(name, Generic, 0)) return;
                int error = Marshal.GetLastWin32Error();
                if (error != NotFound) throw VaultError("удалить", error);
                return;
            }
            if (!IsValid(password)) throw new InvalidDataException("Неверный формат сохранённого пароля.");
            var blob = GCHandle.Alloc(password, GCHandleType.Pinned);
            try
            {
                var credential = new Credential
                {
                    Type = Generic, Target = name, Size = password.Length, Blob = blob.AddrOfPinnedObject(),
                    Persist = PersistLocal, User = String.IsNullOrEmpty(user) ? null : user
                };
                if (!CredWrite(ref credential, 0)) throw VaultError("сохранить", Marshal.GetLastWin32Error());
            }
            finally { blob.Free(); }
        }

        public static bool IsBackupEntry(string path) => path.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase);

        public static IEnumerable<PasswordTarget> BackupTargets(byte[] config)
        {
            return new ConfigDocument(ConfigFile.Decode(config)).Connections().Select(PasswordTarget.From)
                .Where(target => target != null).GroupBy(target => target.Id).Select(group => group.First());
        }

        public static void Clear(byte[] password)
        {
            if (password != null) Array.Clear(password, 0, password.Length);
        }

        internal static string Name(PasswordTarget target) => "SSH Manager/" + target.Id;

        private static IOException VaultError(string action, int error)
        {
            return new IOException("Не удалось " + action + " пароль в Диспетчере учётных данных Windows.", new Win32Exception(error));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags, Type;
            public string Target, Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME Written;
            public int Size;
            public IntPtr Blob;
            public uint Persist, AttributeCount;
            public IntPtr Attributes;
            public string Alias, User;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);
        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr credential);
    }

    internal static class RdpPasswords
    {
        /// <summary>Backup entry prefix: RDP passwords live next to SSH ones, so only protected archives carry them.</summary>
        public const string BackupPrefix = SshPasswords.BackupPrefix + "rdp-";

        internal static string Name(string profileId) => "SSH Manager/RDP/" + profileId;

        public static bool IsBackupEntry(string path) => path.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase);

        public static bool Exists(RdpProfile profile)
        {
            var password = SshPasswords.Read(Name(profile.Id));
            SshPasswords.Clear(password);
            return password != null;
        }

        public static string Read(RdpProfile profile)
        {
            var password = SshPasswords.Read(Name(profile.Id));
            try { return password == null ? null : Encoding.UTF8.GetString(password); }
            finally { SshPasswords.Clear(password); }
        }

        public static byte[] ReadBytes(string profileId) => SshPasswords.Read(Name(profileId));

        public static void Set(RdpProfile profile, string password)
        {
            if (String.IsNullOrEmpty(password)) { Set(profile.Id, profile.Account, null); return; }
            var bytes = SshPasswords.Encode(password);
            try { Set(profile.Id, profile.Account, bytes); }
            finally { SshPasswords.Clear(bytes); }
        }

        public static void Set(string profileId, string account, byte[] password) =>
            SshPasswords.Set(Name(profileId), account, password);
    }

    /// <summary>VNC passwords: "SSH Manager/VNC/&lt;id&gt;", backed up as app/passwords/vnc-&lt;id&gt; like RDP ones.</summary>
    internal static class VncPasswords
    {
        public const string BackupPrefix = SshPasswords.BackupPrefix + "vnc-";

        internal static string Name(string profileId) => "SSH Manager/VNC/" + profileId;

        public static bool IsBackupEntry(string path) => path.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase);

        public static bool Exists(VncProfile profile)
        {
            var password = SshPasswords.Read(Name(profile.Id));
            SshPasswords.Clear(password);
            return password != null;
        }

        public static string Read(VncProfile profile)
        {
            var password = SshPasswords.Read(Name(profile.Id));
            try { return password == null ? null : Encoding.UTF8.GetString(password); }
            finally { SshPasswords.Clear(password); }
        }

        public static byte[] ReadBytes(string profileId) => SshPasswords.Read(Name(profileId));

        public static void Set(VncProfile profile, string password)
        {
            if (String.IsNullOrEmpty(password)) { SshPasswords.Set(Name(profile.Id), profile.User, null); return; }
            var bytes = SshPasswords.Encode(password);
            try { SshPasswords.Set(Name(profile.Id), profile.User.Length == 0 ? "-" : profile.User, bytes); }
            finally { SshPasswords.Clear(bytes); }
        }
    }

    internal sealed class PasswordUpdates : IDisposable
    {
        private readonly List<Change> changes = new List<Change>();
        public int Count => changes.Count;
        public int ReplaceCount => changes.Count(change => change.Before != null);
        public int DesktopCount => changes.Count(change => change.Desktop);
        public int DesktopReplaceCount => changes.Count(change => change.Desktop && change.Before != null);

        public void Add(PasswordTarget target, byte[] value)
        {
            if (target != null) Add(SshPasswords.Name(target), target.User, value, false);
        }

        public void Add(RdpProfile profile, byte[] value)
        {
            if (profile != null) Add(RdpPasswords.Name(profile.Id), profile.Account, value, true);
        }

        public void Add(VncProfile profile, byte[] value)
        {
            if (profile != null) Add(VncPasswords.Name(profile.Id), profile.User.Length == 0 ? "-" : profile.User, value, true);
        }

        private void Add(string name, string user, byte[] value, bool desktop)
        {
            var before = SshPasswords.Read(name);
            if (Equal(before, value)) { SshPasswords.Clear(before); return; }
            changes.Add(new Change(name, user, desktop, before, value == null ? null : (byte[])value.Clone()));
        }

        public void Apply(Action commit)
        {
            int applied = 0;
            try
            {
                foreach (var change in changes)
                {
                    var current = SshPasswords.Read(change.Name);
                    try
                    {
                        if (!Equal(current, change.Before))
                            throw new IOException("Сохранённый пароль изменился после проверки. Повторите операцию.");
                        SshPasswords.Set(change.Name, change.User, change.After);
                        applied++;
                    }
                    finally { SshPasswords.Clear(current); }
                }
                commit();
            }
            catch (Exception error)
            {
                bool rolledBack = true;
                for (int i = applied - 1; i >= 0; i--)
                {
                    var change = changes[i];
                    try { SshPasswords.Set(change.Name, change.User, change.Before); }
                    catch (IOException) { rolledBack = false; }
                }
                if (!rolledBack) throw new IOException("Операция остановлена. Часть сохранённых паролей не удалось вернуть автоматически.", error);
                throw;
            }
        }

        private static bool Equal(byte[] first, byte[] second) => first == null ? second == null : second != null && first.SequenceEqual(second);

        public void Dispose()
        {
            foreach (var change in changes)
            {
                SshPasswords.Clear(change.Before);
                SshPasswords.Clear(change.After);
            }
            changes.Clear();
        }

        private sealed class Change
        {
            public readonly string Name, User;
            public readonly bool Desktop;
            public readonly byte[] Before, After;

            public Change(string name, string user, bool desktop, byte[] before, byte[] after)
            {
                Name = name; User = user; Desktop = desktop; Before = before; After = after;
            }
        }
    }
}
