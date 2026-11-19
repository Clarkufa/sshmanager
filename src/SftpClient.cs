using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SshManager
{
    internal sealed class SftpClient : IDisposable
    {
        private const byte Init = 1, Version = 2, Open = 3, Close = 4, Read = 5, Write = 6, LStat = 7, FStat = 8,
            OpenDir = 11, ReadDir = 12, Remove = 13, MkDir = 14, RmDir = 15, RealPath = 16, Rename = 18,
            Status = 101, Handle = 102, Data = 103, Name = 104, Attrs = 105, Extended = 200;
        private const int BlockSize = 32 * 1024, Pipeline = 16, MaxPacket = 1024 * 1024;
        private const string PosixRename = "posix-rename@openssh.com";
        private readonly StringBuilder errors = new StringBuilder();
        private readonly Process process;
        private readonly SshAskPass askPass;
        private readonly Stream input, output;
        private uint sequence;
        private bool supportsReplace;
        private int disposed;

        public SftpClient(string alias, PasswordTarget target, string configPath = null)
        {
            if (!ConfigDocument.IsAlias(alias)) throw new ArgumentException("Недопустимый псевдоним подключения.");
            var password = SshPasswords.Exists(target) ? target : null;
            string arguments = (password?.Arguments ?? "") +
                "-T -o ClearAllForwardings=yes -o RemoteCommand=none -o ServerAliveInterval=15 -o ServerAliveCountMax=3 " +
                (configPath == null ? "" : "-F " + Terminal.Quote(configPath) + " ") + "-s " + alias + " sftp";
            var start = new ProcessStartInfo(Terminal.SshExecutable(), arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            askPass = new SshAskPass(password);
            try
            {
                askPass.SetEnvironment((key, value) => start.EnvironmentVariables[key] = value);
                process = new Process { StartInfo = start };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data == null) return;
                    lock (errors)
                    {
                        if (errors.Length > 3000) errors.Clear();
                        errors.AppendLine(args.Data.Length > 1500 ? args.Data.Substring(0, 1500) : args.Data);
                    }
                };
                process.Start();
                if (password != null) askPass.Listen(process.Id);
                process.BeginErrorReadLine();
                input = process.StandardOutput.BaseStream;
                output = process.StandardInput.BaseStream;
            }
            catch { Dispose(); throw; }
        }

        public bool Connected
        {
            get
            {
                if (Volatile.Read(ref disposed) != 0) return false;
                try { return !process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        public void Initialize()
        {
            Send(Init, null, packet => packet.Number(3));
            var packet = Receive(out byte type);
            if (type != Version || packet.Number() != 3)
                throw new IOException("Сервер не поддерживает SFTP версии 3.");
            while (!packet.End)
            {
                string extension = packet.Text();
                byte[] value = packet.Blob();
                if (extension == PosixRename && value.Length == 1 && value[0] == '1') supportsReplace = true;
            }
        }

        public string CanonicalPath(string path)
        {
            var response = Request(RealPath, Name, packet => packet.Text(path));
            if (response.Number() != 1) throw BadPacket();
            string canonical = response.Text();
            if (!canonical.StartsWith("/", StringComparison.Ordinal) || canonical.IndexOf('\0') >= 0) throw BadPacket();
            return canonical;
        }

        public List<RemoteFile> List(string directory, CancellationToken token)
        {
            var handle = Request(OpenDir, Handle, packet => packet.Text(directory)).Blob();
            try
            {
                var entries = new List<RemoteFile>();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var page = Request(ReadDir, Name, packet => packet.Blob(handle), allowEof: true);
                    if (page == null) return entries;
                    uint count = page.Number();
                    if (count == 0 || count > 10000) throw BadPacket();
                    for (uint index = 0; index < count; index++)
                    {
                        string name = page.Text();
                        page.Blob(); // Presentation field. File type and size come from the attributes.
                        var attrs = ReadAttributes(page);
                        if (name == "." || name == "..") continue;
                        if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\0' }) >= 0) throw BadPacket();
                        entries.Add(new RemoteFile(name, attrs));
                        if (entries.Count > 100000) throw new IOException("В папке больше 100 000 элементов.");
                    }
                }
            }
            finally { CloseHandle(handle); }
        }

        public FileMetadata Info(string path)
        {
            try { return ReadAttributes(Request(LStat, Attrs, packet => packet.Text(path))); }
            catch (SftpStatusException ex) when (ex.Code == 2) { return null; }
        }

        public void CreateDirectory(string path) => Request(MkDir, Status, packet => { packet.Text(path); packet.Number(0); });
        public void Delete(string path, bool directory) => Request(directory ? RmDir : Remove, Status, packet => packet.Text(path));

        public void Move(string source, string destination, bool replace = false)
        {
            if (!replace || supportsReplace)
            {
                Request(replace ? Extended : Rename, Status, packet =>
                {
                    if (replace) packet.Text(PosixRename);
                    packet.Text(source);
                    packet.Text(destination);
                });
                return;
            }
            // SFTP v3 RENAME cannot overwrite. Keep the previous file until its replacement is in place.
            var recovery = TemporaryName(destination);
            Move(destination, recovery);
            try { Move(source, destination); }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                try { Move(recovery, destination); }
                catch (IOException) { throw new IOException("Старый файл сохранён на сервере как " + recovery, ex); }
                throw;
            }
            try { Delete(recovery, false); }
            catch (IOException ex) { throw new IOException("Файл заменён. Прежняя версия осталась на сервере как " + recovery, ex); }
        }

        public void Download(string path, Stream destination, Action<long, long> progress, CancellationToken token)
        {
            var handle = OpenFile(path, 1);
            try
            {
                long size;
                try { size = ReadAttributes(Request(FStat, Attrs, packet => packet.Blob(handle))).Size; }
                catch (SftpStatusException ex) when (ex.Code == 8) { size = -1; }
                if (size < 0)
                {
                    long position = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var response = Request(Read, Data, ReadRequest(handle, position, BlockSize), allowEof: true);
                        if (response == null) return;
                        byte[] bytes = response.Blob();
                        if (bytes.Length == 0 || bytes.Length > BlockSize) throw BadPacket();
                        destination.Write(bytes, 0, bytes.Length);
                        position += bytes.Length;
                        progress(position, -1);
                    }
                }
                var pending = new Dictionary<uint, Block>();
                long next = 0, completed = 0;
                IOException failure = null;
                while ((next < size && failure == null && !token.IsCancellationRequested) || pending.Count > 0)
                {
                    while (failure == null && !token.IsCancellationRequested && pending.Count < Pipeline && next < size)
                    {
                        int length = (int)Math.Min(BlockSize, size - next);
                        pending.Add(SendRequest(Read, ReadRequest(handle, next, length)), new Block(next, length));
                        next += length;
                    }
                    if (pending.Count == 0) break;
                    var response = Receive(out byte type);
                    uint id = response.Number();
                    if (!pending.TryGetValue(id, out var block)) throw BadPacket();
                    pending.Remove(id);
                    if (failure != null || token.IsCancellationRequested) continue;
                    try
                    {
                        CheckResponse(type, Data, response);
                        byte[] bytes = response.Blob();
                        if (bytes.Length == 0 || bytes.Length > block.Length) throw BadPacket();
                        destination.Position = block.Offset;
                        destination.Write(bytes, 0, bytes.Length);
                        completed += bytes.Length;
                        if (bytes.Length < block.Length)
                        {
                            var rest = new Block(block.Offset + bytes.Length, block.Length - bytes.Length);
                            pending.Add(SendRequest(Read, ReadRequest(handle, rest.Offset, rest.Length)), rest);
                        }
                        progress(completed, size);
                    }
                    catch (IOException ex) { failure = ex; }
                }
                token.ThrowIfCancellationRequested();
                if (failure != null) throw failure;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (ex is SftpStatusException failure && failure.Code == 1)
                    throw new IOException("Размер файла на сервере изменился во время скачивания.", ex);
                throw;
            }
            finally { CloseHandle(handle); }
        }

        public void Upload(string path, Stream source, Action<long, long> progress, CancellationToken token, Action created, uint? permissions)
        {
            // WRITE | CREAT | EXCL keeps an unexpected existing file intact.
            var handle = OpenFile(path, 2 | 8 | 32, permissions);
            try
            {
                created();
                var pending = new Dictionary<uint, int>();
                var buffer = new byte[BlockSize];
                long position = 0, completed = 0;
                bool end = false;
                IOException failure = null;
                while ((!end && !token.IsCancellationRequested) || pending.Count > 0)
                {
                    while (!end && !token.IsCancellationRequested && pending.Count < Pipeline)
                    {
                        int length;
                        try { length = source.Read(buffer, 0, buffer.Length); }
                        catch (IOException ex) { failure = ex; end = true; break; }
                        if (length == 0) { end = true; break; }
                        uint id = SendRequest(Write, packet =>
                        {
                            packet.Blob(handle);
                            packet.Offset((ulong)position);
                            packet.Blob(buffer, length);
                        });
                        pending.Add(id, length);
                        position += length;
                    }
                    if (pending.Count == 0) break;
                    var response = Receive(out byte type);
                    uint request = response.Number();
                    if (!pending.TryGetValue(request, out int written)) throw BadPacket();
                    pending.Remove(request);
                    try { CheckResponse(type, Status, response); }
                    catch (SftpStatusException ex) { failure = failure ?? ex; end = true; }
                    completed += written;
                    progress(completed, source.Length);
                }
                token.ThrowIfCancellationRequested();
                if (failure != null) throw failure;
            }
            finally { CloseHandle(handle); }
        }

        public static string Join(string directory, string name) => directory.TrimEnd('/') + "/" + name;
        public static string TemporaryName(string destination)
        {
            return destination.Substring(0, destination.LastIndexOf('/') + 1) + ".ssh-manager-" + Guid.NewGuid().ToString("N") + ".part";
        }

        private byte[] OpenFile(string path, uint flags, uint? permissions = null)
        {
            return Request(Open, Handle, packet =>
            {
                packet.Text(path);
                packet.Number(flags);
                packet.Number(permissions.HasValue ? 4u : 0u);
                if (permissions.HasValue) packet.Number(permissions.Value);
            }).Blob();
        }

        private static Action<SftpPacket> ReadRequest(byte[] handle, long offset, int length)
        {
            return packet => { packet.Blob(handle); packet.Offset((ulong)offset); packet.Number((uint)length); };
        }

        private void CloseHandle(byte[] handle)
        {
            if (Connected) Request(Close, Status, packet => packet.Blob(handle));
        }

        private SftpPacket Request(byte operation, byte expected, Action<SftpPacket> body, bool allowEof = false)
        {
            uint id = SendRequest(operation, body);
            var response = Receive(out byte type);
            if (response.Number() != id) throw BadPacket();
            return CheckResponse(type, expected, response, allowEof) ? response : null;
        }

        private static bool CheckResponse(byte type, byte expected, SftpPacket packet, bool allowEof = false)
        {
            if (type != Status)
            {
                if (type != expected) throw BadPacket();
                return true;
            }
            uint code = packet.Number();
            if (code == 0 && expected == Status) return true;
            if (code == 1 && allowEof) return false;
            throw new SftpStatusException(code, packet.End ? "" : packet.Text());
        }

        private uint SendRequest(byte operation, Action<SftpPacket> body)
        {
            uint id = ++sequence;
            Send(operation, id, body);
            return id;
        }

        private void Send(byte type, uint? id, Action<SftpPacket> body)
        {
            using (var stream = new MemoryStream())
            {
                var packet = new SftpPacket(stream);
                packet.Number(0);
                stream.WriteByte(type);
                if (id.HasValue) packet.Number(id.Value);
                body(packet);
                int length = (int)stream.Length;
                stream.Position = 0;
                packet.Number((uint)length - 4);
                output.Write(stream.GetBuffer(), 0, length);
                output.Flush();
            }
        }

        private SftpPacket Receive(out byte type)
        {
            var header = new byte[4];
            Fill(header);
            uint length = new SftpPacket(new MemoryStream(header, false)).Number();
            if (length == 0 || length > MaxPacket) throw BadPacket();
            var bytes = new byte[length];
            Fill(bytes);
            type = bytes[0];
            return new SftpPacket(new MemoryStream(bytes, 1, bytes.Length - 1, false));
        }

        private void Fill(byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = input.Read(bytes, offset, bytes.Length - offset);
                if (count == 0)
                {
                    if (Connected) process.WaitForExit(250);
                    if (!Connected && Volatile.Read(ref disposed) == 0) process.WaitForExit();
                    lock (errors)
                    {
                        string detail = errors.ToString().Trim();
                        throw new IOException(detail.Length == 0 ? "SSH-подключение для передачи файлов закрыто." : detail);
                    }
                }
                offset += count;
            }
        }

        private static FileMetadata ReadAttributes(SftpPacket packet)
        {
            uint fields = packet.Number();
            long size = -1;
            uint mode = 0;
            DateTime? modified = null;
            if ((fields & 1) != 0)
            {
                ulong value = packet.Offset();
                if (value <= long.MaxValue) size = (long)value;
            }
            if ((fields & 2) != 0) { packet.Number(); packet.Number(); }
            if ((fields & 4) != 0) mode = packet.Number();
            if ((fields & 8) != 0)
            {
                packet.Number();
                modified = DateTimeOffset.FromUnixTimeSeconds(packet.Number()).LocalDateTime;
            }
            if ((fields & 0x80000000) != 0)
            {
                uint count = packet.Number();
                if (count > 1024) throw BadPacket();
                for (uint i = 0; i < count; i++) { packet.Blob(); packet.Blob(); }
            }
            uint kind = mode & 0xf000;
            bool special = kind != 0 && kind != 0x8000 && kind != 0x4000 && kind != 0xa000;
            return new FileMetadata(kind == 0x4000, kind == 0xa000, size, modified, special,
                (fields & 4) != 0 ? (uint?)(mode & 0x1ff) : null);
        }

        internal static IOException BadPacket() => new SftpProtocolException();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            askPass.Dispose();
            try { if (process != null && !process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            input?.Dispose();
            output?.Dispose();
            process?.Dispose();
        }

        private struct Block
        {
            public readonly long Offset;
            public readonly int Length;
            public Block(long offset, int length) { Offset = offset; Length = length; }
        }
    }

    internal sealed class SftpPacket
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly MemoryStream data;
        public SftpPacket(MemoryStream data) { this.data = data; }
        public bool End => data.Position == data.Length;

        public uint Number()
        {
            uint value = 0;
            for (int i = 0; i < 4; i++)
            {
                int next = data.ReadByte();
                if (next < 0) throw SftpClient.BadPacket();
                value = (value << 8) | (uint)next;
            }
            return value;
        }

        public void Number(uint value)
        {
            for (int i = 24; i >= 0; i -= 8) data.WriteByte((byte)(value >> i));
        }

        public ulong Offset() => ((ulong)Number() << 32) | Number();
        public void Offset(ulong value) { Number((uint)(value >> 32)); Number((uint)value); }

        public byte[] Blob()
        {
            uint length = Number();
            if (length > data.Length - data.Position) throw SftpClient.BadPacket();
            var bytes = new byte[length];
            data.Read(bytes, 0, bytes.Length);
            return bytes;
        }

        public void Blob(byte[] bytes) => Blob(bytes, bytes.Length);
        public void Blob(byte[] bytes, int length) { Number((uint)length); data.Write(bytes, 0, length); }
        public string Text()
        {
            try { return Utf8.GetString(Blob()); }
            catch (DecoderFallbackException) { throw new IOException("Имя файла на сервере не записано в UTF-8."); }
        }
        public void Text(string text) => Blob(Utf8.GetBytes(text));
    }

    internal sealed class FileMetadata
    {
        public readonly bool Directory, Link, Special;
        public readonly long Size;
        public readonly DateTime? Modified;
        public readonly uint? Permissions;
        public FileMetadata(bool directory, bool link, long size, DateTime? modified, bool special = false, uint? permissions = null)
        { Directory = directory; Link = link; Size = size; Modified = modified; Special = special; Permissions = permissions; }
    }

    internal sealed class RemoteFile
    {
        public readonly string Name;
        public readonly FileMetadata Metadata;
        public RemoteFile(string name, FileMetadata metadata) { Name = name; Metadata = metadata; }
    }

    internal sealed class SftpStatusException : IOException
    {
        public readonly uint Code;
        public SftpStatusException(uint code, string detail) : base(Describe(code, detail)) { Code = code; }
        private static string Describe(uint code, string detail)
        {
            switch (code)
            {
                case 1: return "Сервер вернул конец файла.";
                case 2: return "Файл или папка не найдены.";
                case 3: return "Сервер отказал в доступе к файлу или папке.";
                case 8: return "Сервер не поддерживает эту операцию SFTP.";
                default: return String.IsNullOrWhiteSpace(detail) ? "Ошибка SFTP (" + code + ")." : "Ошибка SFTP: " + detail;
            }
        }
    }

    internal sealed class SftpProtocolException : IOException
    {
        public SftpProtocolException() : base("Сервер вернул некорректный ответ SFTP. Переподключитесь.") { }
    }
}
