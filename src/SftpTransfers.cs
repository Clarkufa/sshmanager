using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace SshManager
{
    internal enum FileConflict { Replace, KeepBoth, Skip, Cancel }

    internal sealed class SftpTransfers
    {
        private readonly IRemoteFiles client;
        private readonly CancellationToken token;
        private readonly Func<string, bool, FileConflict> conflict;
        private readonly Action<string, long, long> progress;
        private readonly Stopwatch update = Stopwatch.StartNew();
        public int Completed { get; private set; }
        public int Skipped { get; private set; }

        public SftpTransfers(IRemoteFiles client, CancellationToken token, Func<string, bool, FileConflict> conflict,
            Action<string, long, long> progress)
        {
            this.client = client;
            this.token = token;
            this.conflict = conflict;
            this.progress = progress;
        }

        public void Upload(IEnumerable<string> paths, string directory)
        {
            foreach (var path in paths) UploadItem(path, directory, 0);
        }

        public void Download(string directory, IEnumerable<RemoteFile> items, string destination)
        {
            foreach (var item in items) DownloadItem(directory, item, destination, 0);
        }

        private void UploadItem(string path, string directory, int depth)
        {
            CheckDepth(depth);
            var item = File.GetAttributes(path);
            if ((item & FileAttributes.ReparsePoint) != 0) { Skipped++; return; }
            bool folder = (item & FileAttributes.Directory) != 0;
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (String.IsNullOrEmpty(name)) throw new IOException("Для загрузки выберите файлы или папку внутри диска.");
            string destination = SftpClient.Join(directory, name);
            var existing = client.Info(destination);
            bool replace = false;
            if (existing != null && !(folder && existing.Directory && !existing.Link))
            {
                var choice = Ask(destination, client.CanReplace && !folder && !existing.Directory && !existing.Link && !existing.Special);
                if (choice == FileConflict.Skip) return;
                if (choice == FileConflict.KeepBoth)
                {
                    destination = Alternative(name, proposed => SftpClient.Join(directory, proposed), remote => client.Info(remote) != null);
                    existing = null;
                }
                else replace = true;
            }
            if (folder)
            {
                if (existing == null) client.CreateDirectory(destination);
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) UploadItem(child, destination, depth + 1);
                return;
            }
            string temporary = SftpClient.TemporaryName(destination);
            bool owned = false;
            try
            {
                progress(name, 0, -1);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    client.Upload(temporary, stream, ProgressFor(name), token, () => owned = true, replace ? existing.Permissions : null);
                token.ThrowIfCancellationRequested();
                client.Move(temporary, destination, replace);
                owned = false;
                Completed++;
            }
            finally
            {
                if (owned && client.Connected)
                    try { client.Delete(temporary, false); }
                    catch (IOException) { }
            }
        }

        private void DownloadItem(string directory, RemoteFile item, string destinationDirectory, int depth)
        {
            CheckDepth(depth);
            var source = SftpClient.Join(directory, item.Name);
            // Re-check the current type; a stale list must not turn a file copy into symlink traversal.
            var remote = client.Info(source);
            if (remote == null) throw new IOException("Элемент на сервере исчез: " + source);
            if (remote.Link || remote.Special) { Skipped++; return; }
            string destination = LocalChild(destinationDirectory, item.Name);
            var existing = LocalAttributes(destination);
            bool replace = false;
            if (existing.HasValue && !(remote.Directory && IsDirectory(existing.Value) && !IsLink(existing.Value)))
            {
                var choice = Ask(destination, !remote.Directory && !IsDirectory(existing.Value) && !IsLink(existing.Value));
                if (choice == FileConflict.Skip) return;
                if (choice == FileConflict.KeepBoth)
                    destination = Alternative(item.Name, name => LocalChild(destinationDirectory, name), path => LocalAttributes(path).HasValue);
                else replace = true;
            }
            if (remote.Directory)
            {
                Directory.CreateDirectory(destination);
                foreach (var child in client.List(source, token)) DownloadItem(source, child, destination, depth + 1);
                return;
            }
            string temporary = Path.Combine(destinationDirectory, ".ssh-manager-" + Guid.NewGuid().ToString("N") + ".part");
            bool owned = false;
            try
            {
                progress(item.Name, 0, remote.Size);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    owned = true;
                    client.Download(source, stream, ProgressFor(item.Name), token);
                }
                token.ThrowIfCancellationRequested();
                if (replace) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
                owned = false;
                Completed++;
            }
            finally
            {
                if (owned)
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (Theme.IsFileError(ex)) { }
            }
        }

        private Action<long, long> ProgressFor(string name)
        {
            return (done, total) =>
            {
                if (update.ElapsedMilliseconds < 85 && done != total) return;
                update.Restart();
                progress(name, done, total);
            };
        }

        private FileConflict Ask(string destination, bool canReplace)
        {
            token.ThrowIfCancellationRequested();
            var choice = conflict(destination, canReplace);
            if (choice == FileConflict.Cancel) throw new OperationCanceledException();
            if (choice == FileConflict.Skip) Skipped++;
            return choice;
        }

        private void CheckDepth(int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 64) throw new IOException("Слишком много уровней вложенных папок.");
        }

        private static string Alternative(string name, Func<string, string> makePath, Func<string, bool> exists)
        {
            int dot = name.LastIndexOf('.');
            var stem = dot <= 0 ? name : name.Substring(0, dot);
            var suffix = dot <= 0 ? "" : name.Substring(dot);
            for (int index = 2; index < 10000; index++)
            {
                var path = makePath(stem + " (" + index + ")" + suffix);
                if (!exists(path)) return path;
            }
            throw new IOException("Не удалось подобрать свободное имя.");
        }

        internal static string LocalChild(string directory, string name)
        {
            if (String.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal) || IsReservedName(name))
                throw new IOException("Имя нельзя сохранить в Windows: " + name);
            return Path.Combine(directory, name);
        }

        private static bool IsReservedName(string name)
        {
            string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem)) return true;
            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                "0123456789¹²³".Contains(stem[3]);
        }

        internal static FileAttributes? LocalAttributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        internal static bool IsDirectory(FileAttributes attributes) => (attributes & FileAttributes.Directory) != 0;
        internal static bool IsLink(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;
    }
}
