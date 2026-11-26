using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SshManager
{
    internal sealed class VncFilesUnavailable : IOException
    {
        public VncFilesUnavailable(string message) : base(message) { }
    }

    /// <summary>A shared RFB connection used only for the TightVNC 2 file protocol. It requests no screen updates.</summary>
    internal sealed class TightVncFiles : IRemoteFiles
    {
        private const uint ListRequest = 0xfc000102, ListReply = 0xfc000103;
        private const uint UploadStart = 0xfc000106, UploadData = 0xfc000108, UploadEnd = 0xfc00010a;
        private const uint DownloadStart = 0xfc00010c, DownloadData = 0xfc00010e, DownloadEnd = 0xfc000110;
        private const uint MakeDirectory = 0xfc000111, Remove = 0xfc000113, Rename = 0xfc000115, Failed = 0xfc000119;
        private const int ChunkSize = 64 * 1024, MaximumDirectory = 16 * 1024 * 1024;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        private readonly VncProfile profile;
        private readonly string password;
        private TcpClient tcp;
        private SshTunnel tunnel;
        private Wire wire;
        private volatile bool disposed;
        private bool initialized;

        public TightVncFiles(VncProfile profile, string password)
        {
            this.profile = profile;
            this.password = password;
        }

        public bool Connected => initialized && !disposed;
        public bool CanReplace => false;
        public string Protocol => "TightVNC";
        public string CanonicalPath(string path)
        {
            var components = new List<string>();
            foreach (var item in path.Split('/'))
            {
                if (item == "..")
                {
                    if (components.Count > 0) components.RemoveAt(components.Count - 1);
                }
                else if (item != "." && item.Length != 0) components.Add(item);
            }
            return "/" + String.Join("/", components);
        }

        public void Initialize()
        {
            try
            {
                if (profile.ViewOnly)
                    throw new VncFilesUnavailable("Передача файлов через VNC отключена в режиме «Только просмотр».");
                var host = profile.Host;
                int port = profile.Port;
                if (profile.Via.Length != 0)
                {
                    var opened = SshTunnel.Open(profile.Via, host, port);
                    Interlocked.Exchange(ref tunnel, opened);
                    if (disposed) { Interlocked.Exchange(ref tunnel, null)?.Dispose(); throw new ObjectDisposedException(nameof(TightVncFiles)); }
                    host = "127.0.0.1";
                    port = opened.LocalPort;
                }
                var client = new TcpClient();
                Interlocked.Exchange(ref tcp, client);
                if (disposed) { Interlocked.Exchange(ref tcp, null)?.Close(); throw new ObjectDisposedException(nameof(TightVncFiles)); }
                var pending = client.BeginConnect(host, port, null, null);
                using (var ready = pending.AsyncWaitHandle)
                    if (!ready.WaitOne(TimeSpan.FromSeconds(15))) throw new RfbException("Сервер VNC не ответил за 15 секунд.");
                client.EndConnect(pending);
                client.NoDelay = true;
                client.ReceiveTimeout = client.SendTimeout = 15000;
                wire = new Wire(client.GetStream(), Dispose);
                ReadCapabilities();
                initialized = true;
            }
            catch (SocketException error)
            {
                Dispose();
                throw new RfbException(error.Message);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void ReadCapabilities()
        {
            string version = Encoding.ASCII.GetString(wire.Bytes(12));
            if (!version.StartsWith("RFB 003.", StringComparison.Ordinal) || !Int32.TryParse(version.Substring(8, 3), out int minor))
                throw new RfbException("Это не сервер VNC.");
            if (minor < 7) throw Unsupported();
            bool modern = minor >= 8;
            wire.Write(Encoding.ASCII.GetBytes(modern ? "RFB 003.008\n" : "RFB 003.007\n"));
            byte count = wire.ReadByte();
            if (count == 0) throw new RfbException(wire.ReadText());
            if (!wire.Bytes(count).Contains((byte)16)) throw Unsupported();
            wire.WriteByte(16);

            uint tunnels = wire.Read32();
            if (tunnels != 0)
            {
                var available = Capabilities(tunnels);
                if (!available.Contains("0:TGHT:NOTUNNEL")) throw Unsupported();
                wire.Write32(0);
            }
            uint authCount = wire.Read32();
            bool authenticate = false;
            if (authCount != 0)
            {
                var available = Capabilities(authCount);
                authenticate = available.Contains("2:STDV:VNCAUTH_");
                if (!authenticate && !available.Contains("1:STDV:NOAUTH__")) throw Unsupported();
                if (authenticate && String.IsNullOrEmpty(password)) throw new RfbPasswordRequired();
                wire.Write32(authenticate ? 2u : 1u);
            }
            if (authenticate) wire.Write(VncClient.EncryptChallenge(wire.Bytes(16), password));
            if ((authenticate || modern) && wire.Read32() != 0)
                throw new RfbAuthenticationFailed(modern ? wire.ReadText() : "");

            wire.WriteByte(1); // Shared ClientInit.
            wire.Bytes(20); // Dimensions and pixel format.
            wire.Bytes(wire.Read32(), 64 * 1024); // Desktop title.
            ushort replies = wire.Read16(), requests = wire.Read16(), encodings = wire.Read16();
            wire.Read16();
            var server = Capabilities(replies);
            var client = Capabilities(requests);
            Capabilities(encodings);
            if (!Has(client, ListRequest, "FTCFLRST") || !Has(server, ListReply, "FTSFLRLY") ||
                !Has(client, UploadStart, "FTCFURST") || !Has(server, UploadStart + 1, "FTSFURLY") ||
                !Has(client, UploadData, "FTCUDRST") || !Has(server, UploadData + 1, "FTSUDRLY") ||
                !Has(client, UploadEnd, "FTCUERST") || !Has(server, UploadEnd + 1, "FTSUERLY") ||
                !Has(client, DownloadStart, "FTCFDRST") || !Has(server, DownloadStart + 1, "FTSFDRLY") ||
                !Has(client, DownloadData, "FTCDDRST") || !Has(server, DownloadData + 1, "FTSDDRLY") ||
                !Has(server, DownloadEnd, "FTSDERLY") || !Has(server, Failed, "FTLRFRLY") ||
                !Has(client, MakeDirectory, "FTCMDRST") || !Has(server, MakeDirectory + 1, "FTSMDRLY") ||
                !Has(client, Remove, "FTCFRRST") || !Has(server, Remove + 1, "FTSFTRLY") ||
                !Has(client, Rename, "FTCFMRST") || !Has(server, Rename + 1, "FTSFMRLY"))
                throw Unsupported();
        }

        private static bool Has(HashSet<string> capabilities, uint code, string signature) =>
            capabilities.Contains(code + ":TGHT:" + signature);

        private HashSet<string> Capabilities(uint count)
        {
            if (count > 1024) throw BadReply();
            var capabilities = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                uint code = wire.Read32();
                string vendor = Encoding.ASCII.GetString(wire.Bytes(4));
                string signature = Encoding.ASCII.GetString(wire.Bytes(8));
                capabilities.Add(code + ":" + vendor + ":" + signature);
            }
            return capabilities;
        }

        internal static VncFilesUnavailable Unsupported() =>
            new VncFilesUnavailable("Сервер VNC не подтвердил доступ к передаче файлов через TightVNC 2.");

        public List<RemoteFile> List(string directory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Send(ListRequest, request => { request.WriteByte(0); request.WriteText(directory); });
            Reply(ListReply);
            using (var data = new MemoryStream(Block(MaximumDirectory)))
            {
                var reader = new Wire(data);
                uint count = reader.Read32();
                if (count > 100000 || count > (data.Length - data.Position) / 22) throw BadReply();
                var entries = new List<RemoteFile>((int)count);
                for (int i = 0; i < count; i++)
                {
                    ulong size = reader.Read64(), milliseconds = reader.Read64();
                    ushort flags = reader.Read16();
                    string name = reader.ReadText(tight: true);
                    if (name == "." || name == "..") continue;
                    if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0 || size > Int64.MaxValue) throw BadReply();
                    DateTime? modified = milliseconds == 0 || milliseconds > 253402300799999 ? (DateTime?)null : Epoch.AddMilliseconds(milliseconds);
                    entries.Add(new RemoteFile(name, new FileMetadata((flags & 1) != 0, false, (long)size, modified)));
                }
                if (data.Position != data.Length) throw BadReply();
                return entries;
            }
        }

        public FileMetadata Info(string path)
        {
            string full = CanonicalPath(path);
            if (full == "/") return new FileMetadata(true, false, 0, null);
            int split = full.LastIndexOf('/');
            string directory = split <= 0 ? "/" : full.Substring(0, split);
            string name = full.Substring(split + 1);
            return List(directory, CancellationToken.None).FirstOrDefault(item =>
                String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))?.Metadata;
        }

        public void CreateDirectory(string path)
        {
            Send(MakeDirectory, request => request.WriteText(path));
            Reply(MakeDirectory + 1);
        }

        public void Delete(string path, bool directory)
        {
            Send(Remove, request => request.WriteText(path));
            Reply(Remove + 1);
        }

        public void Move(string source, string destination, bool replace = false)
        {
            if (replace) throw new IOException("Для замены файла выберите другое имя.");
            Send(Rename, request => { request.WriteText(source); request.WriteText(destination); });
            Reply(Rename + 1);
        }

        public void Download(string path, Stream destination, Action<long, long> progress, CancellationToken token)
        {
            long expected = Info(path)?.Size ?? -1, received = 0;
            Send(DownloadStart, request => { request.WriteText(path); request.Write64(0); });
            Reply(DownloadStart + 1);
            try
            {
                for (;;)
                {
                    token.ThrowIfCancellationRequested();
                    Send(DownloadData, request => { request.WriteByte(0); request.Write32(ChunkSize); });
                    if (Reply(DownloadData + 1, DownloadEnd) == DownloadEnd)
                    {
                        wire.ReadByte(); // File flags.
                        wire.Read64(); // Last modification time.
                        if (expected >= 0 && received != expected) throw new IOException("Размер файла на сервере изменился во время скачивания.");
                        progress(received, received);
                        return;
                    }
                    byte[] data = Block(ChunkSize);
                    if (data.Length == 0 || (expected >= 0 && data.Length > expected - received)) throw BadReply();
                    destination.Write(data, 0, data.Length);
                    received += data.Length;
                    progress(received, expected);
                }
            }
            catch { Dispose(); throw; }
        }

        public void Upload(string path, Stream source, Action<long, long> progress, CancellationToken token, Action created, uint? permissions)
        {
            token.ThrowIfCancellationRequested();
            if (Info(path) != null) throw new IOException("Временный файл уже существует на сервере.");
            Send(UploadStart, request =>
            {
                request.WriteText(path);
                request.WriteByte(1); // TightVNC creates or truncates the unique staging path.
                request.Write64(0);
            });
            Reply(UploadStart + 1);
            try
            {
                created();
                long total = source.Length, sent = 0;
                var buffer = new byte[ChunkSize];
                for (;;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = source.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    Send(UploadData, request =>
                    {
                        request.WriteByte(0);
                        request.Write32((uint)count);
                        request.Write32((uint)count);
                        request.Write(buffer, count);
                    });
                    Reply(UploadData + 1);
                    sent += count;
                    progress(sent, total);
                }
                Send(UploadEnd, request =>
                {
                    request.Write16(0);
                    request.Write64((ulong)(DateTime.UtcNow - Epoch).TotalMilliseconds);
                });
                Reply(UploadEnd + 1);
            }
            catch
            {
                // A transfer in progress cannot be followed by an unrelated file request.
                Dispose();
                throw;
            }
        }

        private byte[] Block(int maximum)
        {
            byte compression = wire.ReadByte();
            uint packed = wire.Read32(), plain = wire.Read32();
            if (compression != 0 || packed != plain) throw BadReply();
            return wire.Bytes(plain, maximum);
        }

        private void Send(uint code, Action<Wire> write)
        {
            using (var packet = new MemoryStream())
            {
                var request = new Wire(packet);
                request.Write32(code);
                write(request);
                wire.Write(packet.ToArray());
            }
        }

        private uint Reply(uint expected, uint alternative = 0)
        {
            for (;;)
            {
                byte type = wire.ReadByte();
                if (type == 2) continue; // Bell.
                if (type == 3) // A server may send legacy cut text even without a framebuffer request.
                {
                    wire.Bytes(3);
                    wire.Bytes(wire.Read32(), 1024 * 1024);
                    continue;
                }
                if (type != 0xfc) throw BadReply();
                uint code = ((uint)type << 24) | ((uint)wire.ReadByte() << 16) | wire.Read16();
                if (code == Failed) throw new IOException(wire.ReadText(tight: true));
                if (code == expected || code == alternative) return code;
                throw BadReply();
            }
        }

        private static SftpProtocolException BadReply() => new SftpProtocolException("Сервер прислал некорректный ответ TightVNC.");

        public void Dispose()
        {
            disposed = true;
            Interlocked.Exchange(ref tcp, null)?.Close();
            Interlocked.Exchange(ref tunnel, null)?.Dispose();
        }

        private sealed class Wire
        {
            private readonly Stream stream;
            private readonly Action disconnected;

            public Wire(Stream stream, Action disconnected = null)
            {
                this.stream = stream;
                this.disconnected = disconnected;
            }
            public byte ReadByte()
            {
                return Bytes(1)[0];
            }
            public ushort Read16() => (ushort)((ReadByte() << 8) | ReadByte());
            public uint Read32() => ((uint)Read16() << 16) | Read16();
            public ulong Read64() => ((ulong)Read32() << 32) | Read32();

            public byte[] Bytes(uint count, int maximum = 1024)
            {
                if (count > maximum) throw BadReply();
                var data = new byte[(int)count];
                try
                {
                    for (int offset = 0; offset < data.Length;)
                    {
                        int size = stream.Read(data, offset, data.Length - offset);
                        if (size <= 0) throw new EndOfStreamException();
                        offset += size;
                    }
                    return data;
                }
                catch (IOException) { disconnected?.Invoke(); throw; }
            }

            public string ReadText(bool tight = false)
            {
                var data = Bytes(Read32(), 64 * 1024);
                int length = data.Length;
                if (tight && length > 0 && data[length - 1] == 0) length--;
                try { return Utf8.GetString(data, 0, length); }
                catch (DecoderFallbackException) { throw BadReply(); }
            }
            public void Write(byte[] data) => Write(data, data.Length);
            public void Write(byte[] data, int count)
            {
                try { stream.Write(data, 0, count); }
                catch (IOException) { disconnected?.Invoke(); throw; }
            }
            public void WriteByte(byte value) => Write(new[] { value });
            public void Write16(ushort value) { WriteByte((byte)(value >> 8)); WriteByte((byte)value); }
            public void Write32(uint value) { Write16((ushort)(value >> 16)); Write16((ushort)value); }
            public void Write64(ulong value) { Write32((uint)(value >> 32)); Write32((uint)value); }
            public void WriteText(string text)
            {
                var data = Utf8.GetBytes(text);
                Write32((uint)data.Length + 1);
                Write(data);
                WriteByte(0);
            }
        }
    }
}
