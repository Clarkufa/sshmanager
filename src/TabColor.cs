using System;
using System.Drawing;

namespace SshManager
{
    internal sealed class TabColor
    {
        public static readonly TabColor[] Palette =
        {
            new TabColor("blue", "Синяя", 0x2563EB),
            new TabColor("green", "Зелёная", 0x20804E),
            new TabColor("teal", "Бирюзовая", 0x0E7B82),
            new TabColor("amber", "Янтарная", 0xB36306),
            new TabColor("red", "Красная", 0xC52E49),
            new TabColor("purple", "Фиолетовая", 0x7E4CC8),
            new TabColor("gray", "Серая", 0x64748B)
        };

        public readonly string Id;
        public readonly string Name;
        public readonly Color Swatch;

        public static TabColor Find(string id) { return Array.Find(Palette, color => color.Id == id); }

        private TabColor(string id, string name, int rgb)
        {
            Id = id;
            Name = name;
            Swatch = Color.FromArgb(unchecked((int)0xFF000000) | rgb);
        }
    }
}
