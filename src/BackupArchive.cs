using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SshManager
{
    internal static class BackupArchive
    {
        public const int FileLimit = 8 * 1024 * 1024;
        public const int TotalLimit = 64 * 1024 * 1024;
        public const int EntryLimit = 2048;
        private const int ArchiveLimit = TotalLimit + 2 * 1024 * 1024;
        private const string Marker = "ssh-manager-format.txt";
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SSHMBAK1");
        private static readonly byte[] Version = Encoding.ASCII.GetBytes("SSH Manager backup 1\n");

        public static bool IsProtected(string path)
        {
            using (var file = File.OpenRead(path))
            {
                var header = new byte[Magic.Length];
                return file.Read(header, 0, header.Length) == header.Length && header.SequenceEqual(Magic);
            }
        }

        public static void Write(string path, IDictionary<string, byte[]> files, string password)
        {
            if (password != null && password.Length == 0) throw new ArgumentException("Введите пароль резервной копии.");
            if (password == null && files.Keys.Any(SshPasswords.IsBackupEntry))
                throw new InvalidDataException("Пароли подключений можно сохранить только в защищённый архив.");
            byte[] zip;
            using (var buffer = new MemoryStream())
            {
                try
                {
                    using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true, Encoding.UTF8))
                    {
                        WriteEntry(archive, Marker, Version);
                        foreach (var entry in files.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                            WriteEntry(archive, entry.Key, entry.Value);
                    }
                    zip = buffer.ToArray();
                }
                finally { Array.Clear(buffer.GetBuffer(), 0, (int)buffer.Length); }
            }
            var temp = Path.Combine(Path.GetDirectoryName(path), ".ssh-manager-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = password == null ? zip : Protect(zip, password);
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                Array.Clear(zip, 0, zip.Length);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        public static Dictionary<string, byte[]> Read(string path, string password)
        {
            var data = ReadLimitedFile(path, ArchiveLimit);
            bool encrypted = data.Take(Magic.Length).SequenceEqual(Magic);
            if (encrypted) data = Unprotect(data, password);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var buffer = new MemoryStream(data, false))
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Read, false, Encoding.UTF8))
                {
                    if (archive.Entries.Count > EntryLimit + 1) throw TooLarge();
                    long total = 0;
                    foreach (var entry in archive.Entries)
                    {
                        BackupPaths.ValidateRelative(entry.FullName);
                        if (!encrypted && SshPasswords.IsBackupEntry(entry.FullName))
                            throw new InvalidDataException("Импорт паролей возможен только из защищённого архива.");
                        if (files.ContainsKey(entry.FullName)) throw new InvalidDataException("В архиве повторяется путь «" + entry.FullName + "».");
                        if (entry.Length > FileLimit || (total += entry.Length) > TotalLimit + Version.Length) throw TooLarge();
                        using (var contents = entry.Open()) files.Add(entry.FullName, ReadLimited(contents, FileLimit));
                        if (files[entry.FullName].LongLength != entry.Length) throw new InvalidDataException("Размер файла в архиве не совпадает с его содержимым.");
                    }
                }
                byte[] marker;
                if (!files.TryGetValue(Marker, out marker) || !marker.SequenceEqual(Version))
                    throw new InvalidDataException("Архив не является резервной копией SSH Manager поддерживаемой версии.");
                files.Remove(Marker);
                return files;
            }
            catch { BackupSnapshot.ClearPasswords(files); throw; }
            finally { Array.Clear(data, 0, data.Length); }
        }

        public static byte[] ReadLimitedFile(string path, int limit = FileLimit)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > limit) throw TooLarge();
                return ReadLimited(file, limit);
            }
        }

        public static InvalidDataException TooLarge() { return new InvalidDataException("Превышен допустимый объём резервной копии."); }

        private static byte[] ReadLimited(Stream source, int limit)
        {
            using (var destination = new MemoryStream())
            {
                var buffer = new byte[32768];
                try
                {
                    int length;
                    while ((length = source.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (destination.Length + length > limit) throw TooLarge();
                        destination.Write(buffer, 0, length);
                    }
                    return destination.ToArray();
                }
                finally
                {
                    Array.Clear(buffer, 0, buffer.Length);
                    Array.Clear(destination.GetBuffer(), 0, (int)destination.Length);
                }
            }
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] data)
        {
            using (var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open()) stream.Write(data, 0, data.Length);
        }

        // Format 1 is magic, salt, CBC IV, ciphertext, HMAC-SHA256. The MAC covers everything before itself.
        private const int SaltLength = 16, IvLength = 16, TagLength = 32, Iterations = 600000;

        private static byte[] Protect(byte[] zip, string password)
        {
            var salt = new byte[SaltLength];
            var iv = new byte[IvLength];
            using (var random = RandomNumberGenerator.Create()) { random.GetBytes(salt); random.GetBytes(iv); }
            using (var keys = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
            using (var aes = MakeAes(keys.GetBytes(32), iv))
            using (var mac = MakeMac(keys.GetBytes(32)))
            using (var encryptor = aes.CreateEncryptor())
            {
                var encrypted = encryptor.TransformFinalBlock(zip, 0, zip.Length);
                var body = Magic.Concat(salt).Concat(iv).Concat(encrypted).ToArray();
                return body.Concat(mac.ComputeHash(body)).ToArray();
            }
        }

        private static byte[] Unprotect(byte[] archive, string password)
        {
            int header = Magic.Length + SaltLength + IvLength;
            int authenticated = archive.Length - TagLength;
            if (authenticated <= header || (authenticated - header) % IvLength != 0)
                throw new InvalidDataException("Защищённая копия повреждена.");
            if (String.IsNullOrEmpty(password)) throw new InvalidDataException("Для этой резервной копии нужен пароль.");
            var salt = archive.Skip(Magic.Length).Take(SaltLength).ToArray();
            var iv = archive.Skip(Magic.Length + SaltLength).Take(IvLength).ToArray();
            using (var keys = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
            using (var aes = MakeAes(keys.GetBytes(32), iv))
            using (var mac = MakeMac(keys.GetBytes(32)))
            {
                var tag = mac.ComputeHash(archive, 0, authenticated);
                int difference = 0;
                for (int index = 0; index < TagLength; index++) difference |= tag[index] ^ archive[authenticated + index];
                if (difference != 0) throw new InvalidDataException("Неверный пароль или повреждённая резервная копия.");
                using (var decryptor = aes.CreateDecryptor()) return decryptor.TransformFinalBlock(archive, header, authenticated - header);
            }
        }

        private static Aes MakeAes(byte[] key, byte[] iv)
        {
            try
            {
                var aes = Aes.Create();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;
                return aes;
            }
            finally { Array.Clear(key, 0, key.Length); }
        }

        private static HMACSHA256 MakeMac(byte[] key)
        {
            try { return new HMACSHA256(key); }
            finally { Array.Clear(key, 0, key.Length); }
        }
    }
}
