using System.Windows.Forms;

namespace SshManager
{
    /// <summary>WinForms keys → X11 keysyms for VNC KeyEvent messages (docs/vnc-plan.md 3.4).</summary>
    internal static class VncKeys
    {
        /// <summary>Keys that do not produce a character, or that must not wait for KeyPress (with Ctrl or Alt).</summary>
        public static uint? FromKeyDown(Keys key, bool control, bool alt)
        {
            switch (key)
            {
                case Keys.Back: return 0xff08;
                case Keys.Tab: return 0xff09;
                case Keys.Enter: return 0xff0d;
                case Keys.Escape: return 0xff1b;
                case Keys.Insert: return 0xff63;
                case Keys.Delete: return 0xffff;
                case Keys.Home: return 0xff50;
                case Keys.Left: return 0xff51;
                case Keys.Up: return 0xff52;
                case Keys.Right: return 0xff53;
                case Keys.Down: return 0xff54;
                case Keys.PageUp: return 0xff55;
                case Keys.PageDown: return 0xff56;
                case Keys.End: return 0xff57;
                case Keys.ShiftKey: case Keys.LShiftKey: return 0xffe1;
                case Keys.RShiftKey: return 0xffe2;
                case Keys.ControlKey: case Keys.LControlKey: return 0xffe3;
                case Keys.RControlKey: return 0xffe4;
                case Keys.Menu: case Keys.LMenu: return 0xffe9;
                case Keys.RMenu: return 0xffea;
                case Keys.LWin: case Keys.RWin: return 0xffeb;
                case Keys.CapsLock: return 0xffe5;
                case Keys.NumLock: return 0xff7f;
                case Keys.Scroll: return 0xff14;
                case Keys.PrintScreen: return 0xff61;
                case Keys.Pause: return 0xff13;
                case Keys.Apps: return 0xff67;
                case Keys.Multiply: return 0xffaa;
                case Keys.Add: return 0xffab;
                case Keys.Subtract: return 0xffad;
                case Keys.Decimal: return 0xffae;
                case Keys.Divide: return 0xffaf;
            }
            if (key >= Keys.F1 && key <= Keys.F24) return 0xffbe + (uint)(key - Keys.F1);
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return 0xffb0 + (uint)(key - Keys.NumPad0);
            if (control || alt)
            {
                // KeyPress gives control characters or nothing with Ctrl/Alt: send the plain letter or digit.
                if (key >= Keys.A && key <= Keys.Z) return 0x61 + (uint)(key - Keys.A);
                if (key >= Keys.D0 && key <= Keys.D9) return 0x30 + (uint)(key - Keys.D0);
                switch (key)
                {
                    case Keys.Space: return 0x20;
                    case Keys.OemMinus: return 0x2d;
                    case Keys.Oemplus: return 0x3d;
                    case Keys.OemOpenBrackets: return 0x5b;
                    case Keys.OemCloseBrackets: return 0x5d;
                    case Keys.OemSemicolon: return 0x3b;
                    case Keys.OemQuotes: return 0x27;
                    case Keys.Oemcomma: return 0x2c;
                    case Keys.OemPeriod: return 0x2e;
                    case Keys.OemQuestion: return 0x2f;
                    case Keys.OemPipe: return 0x5c;
                    case Keys.Oemtilde: return 0x60;
                }
            }
            return null;
        }

        /// <summary>A typed character: Latin-1 maps to itself, other Unicode to 0x01000000 + code point.</summary>
        public static uint FromChar(char character) => character <= 0xff ? character : 0x01000000u | character;

        public static bool IsModifier(Keys key) => key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
            key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey || key == Keys.Menu ||
            key == Keys.LMenu || key == Keys.RMenu || key == Keys.LWin || key == Keys.RWin;
    }
}
