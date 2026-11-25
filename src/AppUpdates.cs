using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SshManager
{
    internal sealed class AppUpdate
    {
        public readonly Version Version;
        public readonly string Digest;
        public readonly long Size;
        public readonly bool Installed;
        public readonly long AssetId;

        public AppUpdate(Version version, string digest, long size, bool installed, long assetId = 0)
        { Version = version; Digest = digest; Size = size; Installed = installed; AssetId = assetId; }

        public string Tag => "v" + Version.ToString(3);
        public string Asset => Installed ? AppUpdates.InstallerName : AppUpdates.ExecutableName;
        public string Notes => AppUpdates.Repository + "/releases/tag/" + Tag;
        public string DownloadUrl => AppUpdates.Repository + "/releases/download/" + Tag + "/" + Asset;
    }

    internal sealed class GitHubRequestException : IOException
    {
        public readonly bool Retryable;

        public GitHubRequestException(string message, bool retryable, Exception cause)
            : base(message, cause) { Retryable = retryable; }
    }

    internal static class AppUpdates
    {
        public const string Repository = "https://github.com/Clarkufa/sshmanager";
        public const string LatestRelease = Repository + "/releases/latest";
        public const string InstallerName = "SshManagerSetup.msi", ExecutableName = "SshManager.exe";
        private const string ReleasesApi = "https://api.github.com/repos/Clarkufa/sshmanager/releases";
        private const string UpdateManifest = LatestRelease + "/download/SSH-Manager-Update.json";
        private const long MaximumSize = 64 * 1024 * 1024;
        internal static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

        public static AppUpdate Check(bool installed, CancellationToken token)
        {
            return WithFallback(
                () => CheckSource(ReleasesApi + "/latest", CurrentVersion, installed, token),
                () => CheckSource(UpdateManifest, CurrentVersion, installed, token), token);
        }

        internal static AppUpdate CheckSource(string url, Version current, bool installed, CancellationToken token)
        {
            return WithRetry(() => ReadResponse(new Uri(url), token, response =>
            {
                using (var stream = response.GetResponseStream())
                    return ReadReleaseBody(stream, current, installed, token, response.ResponseUri);
            }), token);
        }

        internal static AppUpdate ReadRelease(Stream content, Version current, bool installed, CancellationToken token)
            => ReadReleaseBody(content, current, installed, token, null);

        private static AppUpdate ReadReleaseBody(Stream content, Version current, bool installed, CancellationToken token, Uri uri)
        {
            var buffer = new byte[8192];
            using (var json = new MemoryStream())
            {
                int count;
                while ((count = ReadChunk(content, buffer, uri, token)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (json.Length + count > 512 * 1024) throw BadRelease();
                    json.Write(buffer, 0, count);
                }
                json.Position = 0;
                var serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
                var release = (GitHubRelease)serializer.ReadObject(json);
                if (release == null || release.Draft || release.Prerelease || !ParseTag(release.Tag, out var version)) throw BadRelease();
                if (version.CompareTo(new Version(current.Major, current.Minor, current.Build)) <= 0) return null;
                string fileName = installed ? InstallerName : ExecutableName;
                var matches = release.Assets?.Where(item => item?.Name == fileName).ToArray();
                if (matches == null || matches.Length != 1) throw BadRelease();
                var asset = matches[0];
                if (asset.State != "uploaded" || asset.Size < 1 || asset.Size > MaximumSize ||
                    asset.Digest == null || !asset.Digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    !IsHash(asset.Digest.Substring(7))) throw BadRelease();
                return new AppUpdate(version, asset.Digest.Substring(7), asset.Size, installed, asset.Id);
            }
        }

        public static string Download(AppUpdate update, IProgress<int> progress, CancellationToken token)
        {
            string directory = UpdateWorker.CreateStage();
            string file = Path.Combine(directory, update.Asset);
            try
            {
                Func<bool> alternate = null;
                if (update.AssetId > 0)
                    alternate = () => DownloadFrom(ReleasesApi + "/assets/" + update.AssetId, file, update.Size, progress, token, binary: true);
                WithFallback(() => DownloadFrom(update.DownloadUrl, file, update.Size, progress, token), alternate, token);
                VerifyFile(file, update.Digest);
                return directory;
            }
            catch
            {
                UpdateWorker.CleanStage(directory);
                throw;
            }
        }

        private static bool DownloadFrom(string url, string file, long size, IProgress<int> progress, CancellationToken token, bool binary = false)
        {
            return WithRetry(() =>
            {
                // A previous transport attempt may have written part of the file.
                File.Delete(file);
                progress.Report(0);
                return ReadResponse(new Uri(url), token, response =>
                {
                    using (var source = response.GetResponseStream())
                    using (var destination = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[65536];
                        long received = 0;
                        int count;
                        while ((count = ReadChunk(source, buffer, response.ResponseUri, token)) > 0)
                        {
                            received += count;
                            if (received > size) throw BadRelease();
                            destination.Write(buffer, 0, count);
                            progress.Report((int)(received * 100 / size));
                        }
                        if (received != size)
                            throw new GitHubRequestException(response.ResponseUri.Host + " — загрузка прервалась.", true, null);
                        destination.Flush(true);
                    }
                    return true;
                }, binary);
            }, token);
        }

        internal static T WithFallback<T>(Func<T> primary, Func<T> secondary, CancellationToken token)
        {
            try { return primary(); }
            catch (GitHubRequestException first) when (secondary != null)
            {
                token.ThrowIfCancellationRequested();
                try { return secondary(); }
                catch (GitHubRequestException second)
                {
                    throw new GitHubRequestException(first.Message == second.Message ? first.Message : first.Message + "\n" + second.Message,
                        false, new AggregateException(first, second));
                }
            }
        }

        private static T WithRetry<T>(Func<T> work, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try { return work(); }
            catch (GitHubRequestException error) when (error.Retryable)
            {
                token.WaitHandle.WaitOne(400);
                token.ThrowIfCancellationRequested();
                return work();
            }
        }

        private static int ReadChunk(Stream stream, byte[] buffer, Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try { return stream.Read(buffer, 0, buffer.Length); }
            catch (IOException error) when (uri != null)
            {
                token.ThrowIfCancellationRequested();
                throw new GitHubRequestException(uri.Host + " — чтение ответа прервалось.", true, error);
            }
        }

        private static T ReadResponse<T>(Uri uri, CancellationToken token, Func<HttpWebResponse, T> read, bool binary = false)
        {
            if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            for (int redirects = 0; redirects < 5; redirects++)
            {
                token.ThrowIfCancellationRequested();
                if (!TrustedUri(uri)) throw BadRelease();
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.AllowAutoRedirect = false;
                request.Timeout = 12000;
                request.ReadWriteTimeout = 15000;
                request.UserAgent = "SSHManager/" + CurrentVersion.ToString(3);
                if (uri.Host == "api.github.com")
                {
                    request.Accept = binary ? "application/octet-stream" : "application/vnd.github+json";
                    request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
                }
                using (token.Register(request.Abort))
                {
                    try
                    {
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            if (response.StatusCode == HttpStatusCode.OK)
                            {
                                if ((response.ContentType ?? "").StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                                    throw new GitHubRequestException(uri.Host + " — вместо файла получена веб-страница.", false, null);
                                return read(response);
                            }
                            int status = (int)response.StatusCode;
                            var location = response.Headers[HttpResponseHeader.Location];
                            if (status < 300 || status >= 400 || String.IsNullOrEmpty(location) ||
                                !Uri.TryCreate(uri, location, out uri)) throw BadRelease();
                        }
                    }
                    catch (WebException ex)
                    {
                        token.ThrowIfCancellationRequested();
                        throw DescribeFailure(uri, ex);
                    }
                }
            }
            throw BadRelease();
        }

        private static GitHubRequestException DescribeFailure(Uri uri, WebException error)
        {
            using (var response = error.Response)
            {
                var http = response as HttpWebResponse;
                if (http != null)
                {
                    int code = (int)http.StatusCode;
                    string detail = "HTTP " + code;
                    if (code == 407) detail += " (прокси требует входа)";
                    if (code == 429 || (code == 403 && http.Headers["X-RateLimit-Remaining"] == "0"))
                        detail += " (исчерпан лимит запросов GitHub)";
                    return new GitHubRequestException(uri.Host + " — " + detail + ".", code == 408 || code >= 500, error);
                }
            }
            string reason;
            bool retry;
            switch (error.Status)
            {
                case WebExceptionStatus.NameResolutionFailure: reason = "адрес не найден в DNS"; retry = false; break;
                case WebExceptionStatus.ProxyNameResolutionFailure: reason = "адрес прокси не найден в DNS"; retry = false; break;
                case WebExceptionStatus.TrustFailure: reason = "Windows не доверяет сертификату сервера"; retry = false; break;
                case WebExceptionStatus.SecureChannelFailure: reason = "не удалось установить соединение TLS"; retry = false; break;
                case WebExceptionStatus.Timeout: reason = "истекло время ожидания"; retry = true; break;
                case WebExceptionStatus.ConnectFailure: reason = "сервер недоступен"; retry = true; break;
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.ConnectionClosed: reason = "соединение прервалось"; retry = true; break;
                default: reason = "сетевая ошибка " + error.Status; retry = false; break;
            }
            return new GitHubRequestException(uri.Host + " — " + reason + ".", retry, error);
        }

        private static bool TrustedUri(Uri uri)
        {
            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                (uri.Host == "api.github.com" || uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" ||
                 uri.Host == "objects.githubusercontent.com" || uri.Host == "github-releases.githubusercontent.com");
        }

        public static void VerifyFile(string file, string expected)
        {
            using (var stream = File.OpenRead(file))
            using (var sha = SHA256.Create())
            {
                if (stream.Length < 1 || stream.Length > MaximumSize) throw BadRelease();
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Контрольная сумма обновления не совпала с опубликованной на GitHub.");
            }
        }

        internal static bool ParseTag(string tag, out Version version)
        {
            version = null;
            return tag != null && tag.StartsWith("v", StringComparison.Ordinal) && Version.TryParse(tag.Substring(1), out version) &&
                version.Build >= 0 && version.Revision < 0 && tag == "v" + version.ToString(3);
        }

        internal static bool IsHash(string text) => text != null && text.Length == 64 && text.All(Uri.IsHexDigit);
        private static InvalidDataException BadRelease() => new InvalidDataException("GitHub вернул неполные или некорректные данные обновления.");

        [DataContract]
        private sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
            [DataMember(Name = "assets")] public GitHubAsset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class GitHubAsset
        {
            [DataMember(Name = "id")] public long Id { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "state")] public string State { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
        }
    }

    internal static class UpdateWorker
    {
        private const string WorkerName = "SshManagerUpdater.exe";
        private static string Root => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SSHManager-Updates"));

        public static string CreateStage()
        {
            string directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            if (!IsStage(directory)) throw new IOException("Папка обновлений недоступна.");
            return directory;
        }

        public static bool IsInstalled(string executable)
        {
            try
            {
                using (var settings = Registry.CurrentUser.OpenSubKey(@"Software\SshManager\Installer"))
                {
                    if (settings == null || settings.GetValue("Shortcut") == null) return false;
                    var expected = settings.GetValue("InstallPath") as string ??
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SSH Manager", AppUpdates.ExecutableName);
                    return String.Equals(expected, executable, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static void Schedule(AppUpdate update, string directory)
        {
            if (!IsStage(directory)) throw new IOException("Папка обновлений недоступна.");
            var worker = Path.Combine(directory, WorkerName);
            string executable = Application.ExecutablePath;
            File.Copy(executable, worker);
            using (var current = Process.GetCurrentProcess())
            {
                string args = "--apply-update " + current.Id + " " + current.StartTime.ToFileTimeUtc() + " " +
                    Terminal.Quote(executable) + " " + update.Tag + " " + update.Digest + " " +
                    (update.Installed ? "msi" : "portable") + " " + (update.Installed && HasDesktopShortcut() ? "desktop" : "default");
                using (Process.Start(new ProcessStartInfo(worker, args) { UseShellExecute = false, WorkingDirectory = directory })) { }
            }
        }

        public static int Apply(string[] args)
        {
            var directory = Path.GetDirectoryName(Application.ExecutablePath);
            if (args.Length != 8 || !IsStage(directory) || Path.GetFileName(Application.ExecutablePath) != WorkerName ||
                !Int32.TryParse(args[1], out int pid) || !Int64.TryParse(args[2], out long started) ||
                !AppUpdates.ParseTag(args[4], out var version) || !AppUpdates.IsHash(args[5]) ||
                (args[6] != "msi" && args[6] != "portable")) return 1;

            string destination = args[3];
            bool installed = args[6] == "msi";
            try
            {
                WaitForApp(pid, started, destination);
                if (!Path.IsPathRooted(destination) || FileVersionInfo.GetVersionInfo(destination).ProductName != "SSH Manager" ||
                    IsInstalled(destination) != installed) throw new IOException("Не удалось определить файл приложения для обновления.");
                var target = FileVersionInfo.GetVersionInfo(destination);
                if (new Version(target.FileMajorPart, target.FileMinorPart, target.FileBuildPart) >= version)
                    throw new IOException("На этом ПК уже есть эта или более новая версия SSH Manager.");
                string payload = Path.Combine(directory, installed ? AppUpdates.InstallerName : AppUpdates.ExecutableName);
                AppUpdates.VerifyFile(payload, args[5]);
                if (installed)
                {
                    int result = InstallMsi(payload, destination, args[7] == "desktop");
                    if (result != 0 && result != 3010)
                        throw new IOException("Windows Installer завершил обновление с кодом " + result + ".");
                    CheckVersion(destination, version);
                    Launch(destination);
                }
                else
                    ReplacePortable(payload, destination, version, args[5]);
                return 0;
            }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is ArgumentException || ex is InvalidOperationException ||
                ex is InvalidDataException || ex is System.ComponentModel.Win32Exception)
            {
                Theme.ShowError(null, ex);
                return 1;
            }
            finally { CleanStage(directory); }
        }

        private static void WaitForApp(int pid, long started, string destination)
        {
            Process application;
            try { application = Process.GetProcessById(pid); }
            catch (ArgumentException) { return; }
            using (application)
            {
                if (application.HasExited) return;
                try
                {
                    if (application.StartTime.ToFileTimeUtc() != started) return;
                    if (!String.Equals(application.MainModule.FileName, destination, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Путь запущенного приложения изменился.");
                    if (!application.WaitForExit(25000)) throw new IOException("SSH Manager не закрылся. Повторите обновление.");
                }
                catch (InvalidOperationException) when (application.HasExited) { }
            }
        }

        private static int InstallMsi(string package, string destination, bool desktop)
        {
            var msi = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            var arguments = "/i " + Terminal.Quote(package) + " /qb /norestart INSTALLDIR=" +
                Terminal.Quote(Path.GetDirectoryName(destination)) + (desktop ? " DESKTOPSHORTCUT=1" : "");
            using (var setup = Process.Start(new ProcessStartInfo(msi, arguments) { UseShellExecute = false }))
            {
                setup.WaitForExit();
                return setup.ExitCode;
            }
        }

        internal static void ReplacePortable(string payload, string destination, Version version, string hash, Action<string> start = null)
        {
            CheckVersion(payload, version);
            string prefix = Path.Combine(Path.GetDirectoryName(destination), ".ssh-manager-" + Guid.NewGuid().ToString("N"));
            string ready = prefix + ".update", backup = prefix + ".bak";
            try
            {
                File.Copy(payload, ready);
                AppUpdates.VerifyFile(ready, hash);
                ReplaceFile(ready, destination, backup);
                try { (start ?? Launch)(destination); }
                catch (Exception launchError)
                {
                    try { ReplaceFile(backup, destination, ready); }
                    catch (Exception ex) when (Theme.IsFileError(ex))
                    {
                        throw new IOException("Не удалось вернуть прежний EXE. Его копия сохранена здесь:\n" + backup, launchError);
                    }
                    throw;
                }
                TryDelete(backup);
            }
            finally { TryDelete(ready); }
        }

        private static void ReplaceFile(string source, string destination, string backup)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(source, destination, backup); return; }
                catch (IOException error) when (attempt < 5 && ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33))
                { Thread.Sleep(180); }
            }
        }

        private static void Launch(string file)
        {
            using (Process.Start(new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file) })) { }
        }

        private static void CheckVersion(string file, Version expected)
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            if (info.ProductName != "SSH Manager" || new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != expected)
                throw new IOException("Версия файла обновления не совпадает с версией релиза.");
        }

        private static bool HasDesktopShortcut()
        {
            using (var settings = Registry.CurrentUser.OpenSubKey(@"Software\SshManager\Installer"))
                return settings?.GetValue("DesktopShortcut") != null;
        }

        private static bool IsStage(string directory)
        {
            return !String.IsNullOrEmpty(directory) &&
                String.Equals(Path.GetDirectoryName(directory), Root, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(Path.GetFileName(directory), "N", out _) &&
                (File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0 &&
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;
        }

        public static void CleanStage(string directory)
        {
            try
            {
                if (!IsStage(directory)) return;
                foreach (var file in new[] { AppUpdates.InstallerName, AppUpdates.ExecutableName, WorkerName })
                    TryDelete(Path.Combine(directory, file));
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (Exception ex) when (Theme.IsFileError(ex)) { }
        }

        public static void CleanOldStages()
        {
            try
            {
                if (!Directory.Exists(Root) || (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) return;
                foreach (var folder in Directory.EnumerateDirectories(Root))
                    if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-2)) CleanStage(folder);
            }
            catch (Exception ex) when (Theme.IsFileError(ex)) { }
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (Theme.IsFileError(ex)) { }
        }
    }
}
