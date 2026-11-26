using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SshManager
{
    /// <summary>RFB cut text with optional UTF-8 Extended Clipboard messages.</summary>
    internal sealed class VncClipboard
    {
        internal const int EncodingId = unchecked((int)0xc0a1e5ce);
        internal const int MaxText = 1024 * 1024;
        internal const int MaxMessage = MaxText + 65536;
        private const uint Text = 1, Caps = 1 << 24, Request = 1 << 25, Peek = 1 << 26, Notify = 1 << 27, Provide = 1 << 28;
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly object sync = new object();
        private readonly Action<byte[]> send;
        private uint peerCaps, peerTextLimit;
        private byte[] localText;

        internal VncClipboard(Action<byte[]> send) { this.send = send; }

        internal string Receive(byte[] message, bool extended)
        {
            if (!extended)
            {
                lock (sync) localText = null;
                return Latin1.GetString(message);
            }
            if (message.Length < 4) return null;
            uint flags = ReadU32(message, 0);

            lock (sync)
            {
                if ((flags & Caps) != 0)
                {
                    peerCaps = flags;
                    peerTextLimit = (flags & Text) != 0 && message.Length >= 8 ? ReadU32(message, 4) : 0;
                    SendExtended(Caps | Request | Peek | Notify | Provide | Text, new byte[4]);
                    if (localText != null) Announce();
                    return null;
                }

                switch (flags & 0xff000000)
                {
                    case Request:
                        if ((flags & Text) != 0 && localText != null) SendText(localText);
                        break;
                    case Peek:
                        if ((peerCaps & Notify) != 0) SendExtended(Notify | (localText == null ? 0 : Text));
                        break;
                    case Notify:
                        localText = null;
                        if ((flags & Text) != 0 && (peerCaps & Request) != 0) SendExtended(Request | Text);
                        break;
                    case Provide:
                        if ((flags & Text) != 0)
                        {
                            string text = ReadText(message);
                            if (text != null) localText = null;
                            return text;
                        }
                        break;
                }
            }
            return null;
        }

        internal void Send(string text)
        {
            string lines = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (lines.Length > MaxText) return;
            lock (sync)
            {
                // Store text even before caps arrives, so the first clipboard is offered again after negotiation.
                var bytes = Encoding.UTF8.GetBytes(lines.Replace("\n", "\r\n") + "\0");
                if (bytes.Length > MaxText) return;
                localText = bytes;
                if ((peerCaps & Text) != 0 && (peerCaps & (Notify | Provide)) != 0) Announce();
                else
                {
                    var legacy = Latin1.GetBytes(lines);
                    var message = new byte[8 + legacy.Length];
                    message[0] = 6;
                    PutU32(message, 4, (uint)legacy.Length);
                    Buffer.BlockCopy(legacy, 0, message, 8, legacy.Length);
                    send(message);
                }
            }
        }

        private void Announce()
        {
            if ((peerCaps & Provide) != 0 && localText.Length <= peerTextLimit) SendText(localText);
            else if ((peerCaps & Notify) != 0) SendExtended(Notify | Text);
        }

        private void SendText(byte[] text)
        {
            var payload = new byte[4 + text.Length];
            PutU32(payload, 0, (uint)text.Length);
            Buffer.BlockCopy(text, 0, payload, 4, text.Length);
            using (var buffer = new MemoryStream())
            {
                buffer.WriteByte(0x78);
                buffer.WriteByte(0x9c);
                using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, true))
                    deflate.Write(payload, 0, payload.Length);
                uint a = 1, b = 0;
                foreach (byte value in payload)
                {
                    a = (a + value) % 65521;
                    b = (b + a) % 65521;
                }
                var checksum = new byte[4];
                PutU32(checksum, 0, (b << 16) | a);
                buffer.Write(checksum, 0, checksum.Length);
                SendExtended(Provide | Text, buffer.ToArray());
            }
        }

        private void SendExtended(uint flags, byte[] payload = null)
        {
            int size = 4 + (payload?.Length ?? 0);
            var message = new byte[8 + size];
            message[0] = 6;
            PutU32(message, 4, unchecked((uint)-size));
            PutU32(message, 8, flags);
            if (payload != null) Buffer.BlockCopy(payload, 0, message, 12, payload.Length);
            send(message);
        }

        private static string ReadText(byte[] message)
        {
            if (message.Length < 10) return null; // flags, zlib header and checksum
            try
            {
                using (var buffer = new MemoryStream(message, 6, message.Length - 10, false))
                using (var deflate = new DeflateStream(buffer, CompressionMode.Decompress))
                {
                    var length = new byte[4];
                    if (!ReadAll(deflate, length)) return null;
                    uint size = ReadU32(length, 0);
                    if (size == 0 || size > MaxText) return null;
                    var text = new byte[(int)size];
                    if (!ReadAll(deflate, text) || text[text.Length - 1] != 0) return null;
                    return Utf8.GetString(text, 0, text.Length - 1);
                }
            }
            catch (Exception error) when (error is InvalidDataException || error is DecoderFallbackException) { return null; }
        }

        private static bool ReadAll(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = stream.Read(buffer, offset, buffer.Length - offset);
                if (count == 0) return false;
                offset += count;
            }
            return true;
        }

        private static uint ReadU32(byte[] buffer, int offset) =>
            (uint)(buffer[offset] << 24 | buffer[offset + 1] << 16 | buffer[offset + 2] << 8 | buffer[offset + 3]);

        private static void PutU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}
