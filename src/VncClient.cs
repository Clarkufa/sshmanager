using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SshManager
{
    /// <summary>A VNC error with a message for the user.</summary>
    internal class RfbException : IOException
    {
        public RfbException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>The server asks for a VNC password and none was given: ask the user and connect again.</summary>
    internal sealed class RfbPasswordRequired : RfbException
    {
        public RfbPasswordRequired() : base("Нужен пароль VNC.") { }
    }

    /// <summary>The server rejected the password.</summary>
    internal sealed class RfbAuthenticationFailed : RfbException
    {
        public RfbAuthenticationFailed(string reason) : base("Неверный пароль VNC." + (String.IsNullOrEmpty(reason) ? "" : " " + reason)) { }
    }

    /// <summary>
    /// The framebuffer: 32-bit pixels 0x00RRGGBB in a pinned array that a Bitmap shares, so the decoder writes pixels and
    /// the control draws the Bitmap without copying. Access it under <see cref="Lock"/>.
    /// </summary>
    internal sealed class VncFrame : IDisposable
    {
        public readonly object Lock = new object();
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int[] Pixels { get; private set; }
        public Bitmap Bitmap { get; private set; }
        private GCHandle handle;

        public void Resize(int width, int height)
        {
            lock (Lock)
            {
                Release();
                Width = Math.Max(1, width);
                Height = Math.Max(1, height);
                Pixels = new int[Width * Height];
                handle = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
                Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            }
        }

        private void Release()
        {
            Bitmap?.Dispose();
            Bitmap = null;
            if (handle.IsAllocated) handle.Free();
        }

        public void Dispose() { lock (Lock) Release(); }
    }

    /// <summary>
    /// A VNC (RFB 3.3, 3.7, 3.8, RFC 6143) client: security None and VNC Authentication; Raw, CopyRect, Hextile and
    /// ZRLE encodings; DesktopSize and ExtendedDesktopSize. Reads on a background thread; events come from that thread.
    /// </summary>
    internal sealed class VncClient : IDisposable
    {
        private const int Raw = 0, CopyRect = 1, Hextile = 5, Zrle = 16, DesktopSize = -223, ExtendedDesktopSize = -308;
        private readonly VncClipboard clipboard;
        private readonly object writeLock = new object();
        private TcpClient tcp;
        private Stream input, output;
        private Thread reader;
        private volatile bool closing;
        private int minor;
        private ChunkStream zrleInput;
        private DeflateStream zrleStream;
        private byte[] zrleBuffer = new byte[65536];
        private int zrleStart, zrleEnd;
        private bool zrleHeaderSkipped;

        /// <summary>Checks only: offer these encodings instead of the normal list (pseudo-encodings are added).</summary>
        internal static int[] EncodingsOverride;
        public readonly VncFrame Frame = new VncFrame();
        public string DesktopName { get; private set; } = "";
        /// <summary>The server announced ExtendedDesktopSize: it may accept a new size (SetDesktopSize).</summary>
        public bool CanResize { get; private set; }
        public bool ViewOnly { get; set; }
        public bool ClipboardEnabled { get; set; } = true;

        public VncClient() { clipboard = new VncClipboard(TryWrite); }

        public event Action Connected;
        public event Action<Rectangle> Updated;
        public event Action<Size> Resized;
        public event Action<string> ClipboardReceived;
        /// <summary>Null when the connection was closed here, otherwise the reason.</summary>
        public event Action<Exception> Disconnected;

        public void Start(string host, int port, string password)
        {
            reader = new Thread(() => Run(host, port, password)) { IsBackground = true, Name = "VNC " + host };
            reader.Start();
        }

        private void Run(string host, int port, string password)
        {
            Exception failure = null;
            try
            {
                tcp = new TcpClient();
                var connect = tcp.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(15)))
                    throw new RfbException("Сервер VNC не ответил за 15 секунд.");
                tcp.EndConnect(connect);
                tcp.NoDelay = true;
                var network = tcp.GetStream();
                input = new BufferedStream(network, 65536);
                output = network;
                Handshake(password);
                Connected?.Invoke();
                Loop();
            }
            catch (Exception error) when (!closing)
            {
                failure = error is RfbException ? error :
                    error is SocketException || error is IOException || error is ObjectDisposedException
                        ? new RfbException(error is EndOfStreamException ? "Сервер VNC закрыл соединение." : error.Message, error)
                        : new RfbException("Ошибка VNC: " + error.Message, error);
            }
            catch (Exception) { /* Closed here. */ }
            finally
            {
                try { tcp?.Close(); } catch (SocketException) { }
                Disconnected?.Invoke(closing ? null : failure ?? new RfbException("Сервер VNC закрыл соединение."));
            }
        }

        // --- handshake --------------------------------------------------------------------------------------

        private void Handshake(string password)
        {
            var version = Encoding.ASCII.GetString(ReadExact(12));
            if (!version.StartsWith("RFB ") || version.Length != 12 || version[7] != '.')
                throw new RfbException("Это не сервер VNC.");
            int major = int.Parse(version.Substring(4, 3)), serverMinor = int.Parse(version.Substring(8, 3));
            minor = major > 3 || serverMinor >= 8 ? 8 : serverMinor == 7 ? 7 : 3;
            Write(Encoding.ASCII.GetBytes("RFB 003.00" + minor + "\n"));

            int type;
            if (minor >= 7)
            {
                int count = ReadU8();
                if (count == 0) throw new RfbException("Сервер VNC отказал в подключении: " + ReadReason());
                var types = ReadExact(count);
                type = Array.IndexOf(types, (byte)1) >= 0 ? 1 : Array.IndexOf(types, (byte)2) >= 0 ? 2 : 0;
                if (type == 0) throw new RfbException("Сервер VNC не поддерживает доступный способ входа.");
                if (type == 2 && String.IsNullOrEmpty(password)) throw new RfbPasswordRequired();
                Write(new[] { (byte)type });
            }
            else
            {
                type = (int)ReadU32();
                if (type == 0) throw new RfbException("Сервер VNC отказал в подключении: " + ReadReason());
                if (type != 1 && type != 2) throw new RfbException("Сервер VNC не поддерживает доступный способ входа.");
                if (type == 2 && String.IsNullOrEmpty(password)) throw new RfbPasswordRequired();
            }
            if (type == 2) Write(EncryptChallenge(ReadExact(16), password));
            if (type == 2 || minor >= 8)
            {
                if (ReadU32() != 0) throw new RfbAuthenticationFailed(minor >= 8 ? ReadReason() : "");
            }

            Write(new byte[] { 1 }); // ClientInit: shared
            int width = ReadU16(), height = ReadU16();
            ReadExact(16); // the server's pixel format; ours follows
            DesktopName = ReadText((int)Math.Min(ReadU32(), 4096));
            Frame.Resize(width, height);

            var format = new byte[20];
            format[0] = 0; // SetPixelFormat
            format[4] = 32; format[5] = 24; format[6] = 0; format[7] = 1; // bpp, depth, little endian, true colour
            format[8] = 0; format[9] = 255; format[10] = 0; format[11] = 255; format[12] = 0; format[13] = 255;
            format[14] = 16; format[15] = 8; format[16] = 0; // red, green, blue shifts
            Write(format);

            var encodings = new List<int>(EncodingsOverride ?? new[] { Zrle, Hextile, CopyRect, Raw })
                { DesktopSize, ExtendedDesktopSize };
            if (ClipboardEnabled && !ViewOnly) encodings.Add(VncClipboard.EncodingId);
            var message = new byte[4 + encodings.Count * 4];
            message[0] = 2;
            PutU16(message, 2, encodings.Count);
            for (int i = 0; i < encodings.Count; i++) PutS32(message, 4 + i * 4, encodings[i]);
            Write(message);
            RequestUpdate(false);
        }

        /// <summary>VNC Authentication: DES-encrypt the challenge with the password, each key byte bit-reversed.</summary>
        internal static byte[] EncryptChallenge(byte[] challenge, string password)
        {
            var key = new byte[8];
            var bytes = Encoding.GetEncoding(28591).GetBytes(password ?? "");
            for (int i = 0; i < 8 && i < bytes.Length; i++)
            {
                int value = bytes[i], reversed = 0;
                for (int bit = 0; bit < 8; bit++) if ((value & (1 << bit)) != 0) reversed |= 0x80 >> bit;
                key[i] = (byte)reversed;
            }
            if (DES.IsWeakKey(key) || DES.IsSemiWeakKey(key)) throw new RfbException("Этот пароль VNC не поддерживается.");
            using (var des = new DESCryptoServiceProvider { Mode = CipherMode.ECB, Padding = PaddingMode.None, Key = key })
            using (var encryptor = des.CreateEncryptor())
                return encryptor.TransformFinalBlock(challenge, 0, challenge.Length);
        }

        // --- server messages -----------------------------------------------------------------------------------

        private void Loop()
        {
            while (!closing)
            {
                int type = ReadU8();
                switch (type)
                {
                    case 0: ReadUpdate(); break;
                    case 1:
                        ReadExact(1);
                        ReadU16();
                        ReadExact(ReadU16() * 6);
                        break;
                    case 2: break; // Bell
                    case 3:
                        ReadExact(3);
                        int length = ReadS32();
                        if (length < -VncClipboard.MaxMessage || length > VncClipboard.MaxText)
                            throw new RfbException("Сервер VNC прислал слишком большой буфер обмена.");
                        var payload = ReadExact(Math.Abs(length));
                        if (ClipboardEnabled && !ViewOnly)
                        {
                            var text = clipboard.Receive(payload, length < 0);
                            if (text != null) ClipboardReceived?.Invoke(text);
                        }
                        break;
                    default: throw new RfbException("Сервер VNC прислал неизвестное сообщение " + type + ".");
                }
            }
        }

        private void ReadUpdate()
        {
            ReadExact(1);
            int count = ReadU16();
            var dirty = Rectangle.Empty;
            Size? resized = null;
            for (int i = 0; i < count; i++)
            {
                int x = ReadU16(), y = ReadU16(), w = ReadU16(), h = ReadU16();
                int encoding = ReadS32();
                if (encoding == DesktopSize || encoding == ExtendedDesktopSize)
                {
                    if (encoding == ExtendedDesktopSize)
                    {
                        CanResize = true;
                        int screens = ReadU8();
                        ReadExact(3);
                        ReadExact(screens * 16);
                        // x is why the size changed (0 server, 1 our SetDesktopSize, 2 another client), y the result:
                        // a non-zero result means our request was refused and the size stays.
                        if (y != 0) continue;
                    }
                    // Servers repeat the size in every full update; only a real change replaces the framebuffer.
                    if (w != Frame.Width || h != Frame.Height)
                    {
                        Frame.Resize(w, h);
                        resized = new Size(w, h);
                    }
                    continue;
                }
                if (x + w > Frame.Width || y + h > Frame.Height) throw new RfbException("Сервер VNC прислал область за пределами экрана.");
                lock (Frame.Lock)
                {
                    switch (encoding)
                    {
                        case Raw: ReadRaw(x, y, w, h); break;
                        case CopyRect: ReadCopy(x, y, w, h); break;
                        case Hextile: ReadHextile(x, y, w, h); break;
                        case Zrle: ReadZrle(x, y, w, h); break;
                        default: throw new RfbException("Сервер VNC прислал неподдерживаемую кодировку " + encoding + ".");
                    }
                }
                dirty = dirty.IsEmpty ? new Rectangle(x, y, w, h) : Rectangle.Union(dirty, new Rectangle(x, y, w, h));
            }
            if (resized.HasValue) Resized?.Invoke(resized.Value);
            else if (!dirty.IsEmpty) Updated?.Invoke(dirty);
            RequestUpdate(!resized.HasValue);
        }

        private void ReadRaw(int x, int y, int w, int h)
        {
            var row = new byte[w * 4];
            var pixels = Frame.Pixels;
            for (int line = 0; line < h; line++)
            {
                ReadExact(row, 0, row.Length);
                Buffer.BlockCopy(row, 0, pixels, ((y + line) * Frame.Width + x) * 4, row.Length);
            }
        }

        private void ReadCopy(int x, int y, int w, int h)
        {
            int sx = ReadU16(), sy = ReadU16();
            if (sx + w > Frame.Width || sy + h > Frame.Height) throw new RfbException("Сервер VNC прислал неверную область копирования.");
            var pixels = Frame.Pixels;
            var copy = new int[w * h];
            for (int line = 0; line < h; line++) Array.Copy(pixels, (sy + line) * Frame.Width + sx, copy, line * w, w);
            for (int line = 0; line < h; line++) Array.Copy(copy, line * w, pixels, (y + line) * Frame.Width + x, w);
        }

        private void Fill(int x, int y, int w, int h, int colour)
        {
            var pixels = Frame.Pixels;
            for (int line = 0; line < h; line++)
            {
                int start = (y + line) * Frame.Width + x;
                for (int column = 0; column < w; column++) pixels[start + column] = colour;
            }
        }

        private int ReadPixel()
        {
            var b = ReadExact(4);
            return b[0] | b[1] << 8 | b[2] << 16;
        }

        private void ReadHextile(int x, int y, int w, int h)
        {
            int background = 0, foreground = 0;
            for (int ty = y; ty < y + h; ty += 16)
            {
                for (int tx = x; tx < x + w; tx += 16)
                {
                    int tw = Math.Min(16, x + w - tx), th = Math.Min(16, y + h - ty);
                    int mask = ReadU8();
                    if ((mask & 1) != 0) { ReadRaw(tx, ty, tw, th); continue; }
                    if ((mask & 2) != 0) background = ReadPixel();
                    Fill(tx, ty, tw, th, background);
                    if ((mask & 4) != 0) foreground = ReadPixel();
                    if ((mask & 8) == 0) continue;
                    int subrects = ReadU8();
                    for (int i = 0; i < subrects; i++)
                    {
                        int colour = (mask & 16) != 0 ? ReadPixel() : foreground;
                        int xy = ReadU8(), wh = ReadU8();
                        int sx = xy >> 4, sy = xy & 15, sw = (wh >> 4) + 1, sh = (wh & 15) + 1;
                        if (sx + sw > tw || sy + sh > th) throw new RfbException("Сервер VNC прислал повреждённую плитку Hextile.");
                        Fill(tx + sx, ty + sy, sw, sh, colour);
                    }
                }
            }
        }

        // --- ZRLE ------------------------------------------------------------------------------------------------

        private void ReadZrle(int x, int y, int w, int h)
        {
            int length = (int)ReadU32();
            if (length < 0 || length > 64 * 1024 * 1024) throw new RfbException("Сервер VNC прислал слишком большой блок ZRLE.");
            var data = ReadExact(length);
            if (zrleInput == null)
            {
                zrleInput = new ChunkStream();
                zrleStream = new DeflateStream(zrleInput, CompressionMode.Decompress, true);
            }
            int offset = 0;
            if (!zrleHeaderSkipped)
            {
                // One zlib stream runs through the whole connection; DeflateStream wants it without the 2-byte header.
                offset = Math.Min(2, data.Length);
                zrleHeaderSkipped = offset == 2;
            }
            zrleInput.Append(data, offset, data.Length - offset);
            var pixels = Frame.Pixels;
            var palette = new int[128];
            for (int ty = y; ty < y + h; ty += 64)
            {
                for (int tx = x; tx < x + w; tx += 64)
                {
                    int tw = Math.Min(64, x + w - tx), th = Math.Min(64, y + h - ty);
                    int mode = ZByte();
                    if (mode == 0)
                    {
                        for (int line = 0; line < th; line++)
                            for (int column = 0; column < tw; column++) pixels[(ty + line) * Frame.Width + tx + column] = ZPixel();
                    }
                    else if (mode == 1) Fill(tx, ty, tw, th, ZPixel());
                    else if (mode >= 2 && mode <= 16)
                    {
                        for (int i = 0; i < mode; i++) palette[i] = ZPixel();
                        int bits = mode == 2 ? 1 : mode <= 4 ? 2 : 4;
                        for (int line = 0; line < th; line++)
                        {
                            int current = 0, left = 0;
                            for (int column = 0; column < tw; column++)
                            {
                                if (left == 0) { current = ZByte(); left = 8; }
                                left -= bits;
                                int index = (current >> left) & ((1 << bits) - 1);
                                if (index >= mode) throw new RfbException("Сервер VNC прислал повреждённую плитку ZRLE.");
                                pixels[(ty + line) * Frame.Width + tx + column] = palette[index];
                            }
                        }
                    }
                    else if (mode == 128 || mode >= 130)
                    {
                        int size = mode == 128 ? 0 : mode - 128;
                        for (int i = 0; i < size; i++) palette[i] = ZPixel();
                        int position = 0, total = tw * th;
                        while (position < total)
                        {
                            int colour, run = 1;
                            if (mode == 128) { colour = ZPixel(); run = ZRun(); }
                            else
                            {
                                int index = ZByte();
                                if ((index & 128) != 0) { index &= 127; run = ZRun(); }
                                if (index >= size) throw new RfbException("Сервер VNC прислал повреждённую плитку ZRLE.");
                                colour = palette[index];
                            }
                            if (position + run > total) throw new RfbException("Сервер VNC прислал повреждённую плитку ZRLE.");
                            for (int i = 0; i < run; i++, position++)
                                pixels[(ty + position / tw) * Frame.Width + tx + position % tw] = colour;
                        }
                    }
                    else throw new RfbException("Сервер VNC прислал неизвестную плитку ZRLE " + mode + ".");
                }
            }
        }

        private int ZRun()
        {
            int length = 1, value;
            do { value = ZByte(); length += value; } while (value == 255);
            return length;
        }

        /// <summary>A compressed pixel: 3 bytes, blue, green, red.</summary>
        private int ZPixel() => ZByte() | ZByte() << 8 | ZByte() << 16;

        private int ZByte()
        {
            if (zrleStart == zrleEnd)
            {
                zrleStart = 0;
                zrleEnd = zrleStream.Read(zrleBuffer, 0, zrleBuffer.Length);
                if (zrleEnd <= 0) throw new RfbException("Сервер VNC прислал повреждённые данные ZRLE.");
            }
            return zrleBuffer[zrleStart++];
        }

        /// <summary>The compressed bytes of ZRLE rectangles, one after another, for a single DeflateStream.</summary>
        private sealed class ChunkStream : Stream
        {
            private readonly Queue<ArraySegment<byte>> chunks = new Queue<ArraySegment<byte>>();

            public void Append(byte[] data, int offset, int count)
            {
                if (count > 0) chunks.Enqueue(new ArraySegment<byte>(data, offset, count));
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                // .NET Framework's DeflateStream keeps reading until the caller's buffer is full. Returning 0 here makes it
                // hand back what this rectangle decompressed to; the next rectangle's bytes continue the same stream.
                if (chunks.Count == 0) return 0;
                var chunk = chunks.Dequeue();
                int taken = Math.Min(count, chunk.Count);
                Buffer.BlockCopy(chunk.Array, chunk.Offset, buffer, offset, taken);
                if (taken < chunk.Count)
                {
                    var rest = new Queue<ArraySegment<byte>>();
                    rest.Enqueue(new ArraySegment<byte>(chunk.Array, chunk.Offset + taken, chunk.Count - taken));
                    while (chunks.Count > 0) rest.Enqueue(chunks.Dequeue());
                    while (rest.Count > 0) chunks.Enqueue(rest.Dequeue());
                }
                return taken;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // --- client messages ------------------------------------------------------------------------------------

        private void RequestUpdate(bool incremental)
        {
            var message = new byte[10];
            message[0] = 3;
            message[1] = (byte)(incremental ? 1 : 0);
            PutU16(message, 6, Frame.Width);
            PutU16(message, 8, Frame.Height);
            Write(message);
        }

        public void SendKey(uint keysym, bool down)
        {
            if (ViewOnly) return;
            var message = new byte[8];
            message[0] = 4;
            message[1] = (byte)(down ? 1 : 0);
            PutU32(message, 4, keysym);
            TryWrite(message);
        }

        /// <summary>Buttons: 1 left, 2 middle, 4 right, 8 wheel up, 16 wheel down.</summary>
        public void SendPointer(int buttons, int x, int y)
        {
            if (ViewOnly) return;
            var message = new byte[6];
            message[0] = 5;
            message[1] = (byte)buttons;
            PutU16(message, 2, Math.Max(0, Math.Min(Frame.Width - 1, x)));
            PutU16(message, 4, Math.Max(0, Math.Min(Frame.Height - 1, y)));
            TryWrite(message);
        }

        public void SendClipboard(string text)
        {
            if (ClipboardEnabled && !ViewOnly && text != null) clipboard.Send(text);
        }

        /// <summary>SetDesktopSize (ExtendedDesktopSize servers): one screen of the given size.</summary>
        public void RequestDesktopSize(int width, int height)
        {
            if (!CanResize || width < 64 || height < 64) return;
            var message = new byte[24];
            message[0] = 251;
            PutU16(message, 2, Math.Min(width, 8192));
            PutU16(message, 4, Math.Min(height, 8192));
            message[6] = 1;
            PutU16(message, 16, Math.Min(width, 8192));
            PutU16(message, 18, Math.Min(height, 8192));
            TryWrite(message);
        }

        private void TryWrite(byte[] message)
        {
            try { Write(message); }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is SocketException || error is NullReferenceException) { }
        }

        private void Write(byte[] data)
        {
            lock (writeLock) output.Write(data, 0, data.Length);
        }

        // --- reading helpers --------------------------------------------------------------------------------------

        private byte[] ReadExact(int count)
        {
            var buffer = new byte[count];
            ReadExact(buffer, 0, count);
            return buffer;
        }

        private void ReadExact(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = input.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
                count -= read;
            }
        }

        private int ReadU8()
        {
            int value = input.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            return value;
        }

        private int ReadU16() { var b = ReadExact(2); return b[0] << 8 | b[1]; }
        private uint ReadU32() { var b = ReadExact(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        private int ReadS32() => unchecked((int)ReadU32());
        private string ReadReason() => ReadText((int)Math.Min(ReadU32(), 4096));
        private string ReadText(int length) => Encoding.UTF8.GetString(ReadExact(length));

        private static void PutU16(byte[] buffer, int offset, int value) { buffer[offset] = (byte)(value >> 8); buffer[offset + 1] = (byte)value; }
        private static void PutU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24); buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8); buffer[offset + 3] = (byte)value;
        }
        private static void PutS32(byte[] buffer, int offset, int value) => PutU32(buffer, offset, unchecked((uint)value));

        public void Close()
        {
            closing = true;
            try { tcp?.Close(); } catch (SocketException) { }
        }

        public void Dispose()
        {
            Close();
            zrleStream?.Dispose();
            Frame.Dispose();
        }
    }
}
