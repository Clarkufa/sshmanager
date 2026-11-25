using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace SshManager
{
    internal sealed class TerminalFonts : IDisposable
    {
        private const float TerminalSize = 12;
        private PrivateFontCollection collection;
        private FontFamily family;
        private GCHandle regularData, boldData;

        public Font Regular { get; private set; }
        public Font Bold { get; private set; }

        public TerminalFonts()
        {
            using (var resource = typeof(TerminalFonts).Assembly.GetManifestResourceStream("SshManager.CascadiaMono.zip"))
            {
                if (resource != null)
                {
                    try
                    {
                        collection = new PrivateFontCollection();
                        using (var archive = new ZipArchive(resource, ZipArchiveMode.Read))
                        {
                            regularData = AddFont(archive, "CascadiaMono-Regular.ttf");
                            boldData = AddFont(archive, "CascadiaMono-Bold.ttf");
                        }
                        family = new FontFamily("Cascadia Mono", collection);
                        Regular = new Font(family, TerminalSize);
                        Bold = new Font(family, TerminalSize, FontStyle.Bold);
                        return;
                    }
                    catch (Exception error) when (error is IOException || error is InvalidDataException ||
                        error is ArgumentException || error is ExternalException)
                    {
                        Dispose();
                    }
                }
            }
            Regular = new Font("Consolas", TerminalSize);
            Bold = new Font(Regular, FontStyle.Bold);
        }

        private GCHandle AddFont(ZipArchive archive, string name)
        {
            var entry = archive.GetEntry(name) ?? throw new InvalidDataException("Missing terminal font " + name);
            using (var stream = entry.Open())
            using (var data = new MemoryStream())
            {
                stream.CopyTo(data);
                var bytes = data.ToArray();
                // GDI+ uses the font bytes for the lifetime of its private collection.
                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try { collection.AddMemoryFont(handle.AddrOfPinnedObject(), bytes.Length); }
                catch { handle.Free(); throw; }
                return handle;
            }
        }

        public void Dispose()
        {
            Bold?.Dispose();
            Regular?.Dispose();
            family?.Dispose();
            collection?.Dispose();
            if (boldData.IsAllocated) boldData.Free();
            if (regularData.IsAllocated) regularData.Free();
            Bold = Regular = null;
            family = null;
            collection = null;
        }
    }
}
