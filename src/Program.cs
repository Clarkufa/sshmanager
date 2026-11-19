using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace SshManager
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (SshAskPass.TryRun(args, out int code)) return code;
            if (args.Length == 3 && args[0] == "--terminal")
                return Terminal.RunInConsole(args[1], args[2]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var groups = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSH Manager", "tabs.xml");
            using (var identity = WindowsIdentity.GetCurrent())
                TrayApplication.Run("SshManager." + identity.User.Value, () => new ConnectionsWindow(Terminal.SshDirectory, groups));
            return 0;
        }
    }

    internal static class Terminal
    {
        public static string SshDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

        public static PasswordTarget FindPasswordTarget(string alias, string id)
        {
            return new ConfigFile(Path.Combine(SshDirectory, "config")).Load().Connections()
                .Where(connection => String.Equals(connection.Alias, alias, StringComparison.OrdinalIgnoreCase))
                .Select(PasswordTarget.From).FirstOrDefault(target => target != null && target.Id == id);
        }

        public static PseudoTerminal StartEmbedded(string arguments, Size size, PasswordTarget password)
        {
            var ssh = SshExecutable();
            if (!SshPasswords.Exists(password)) return PseudoTerminal.Start(ssh, arguments, size.Width, size.Height);
            var askPass = new SshAskPass(password);
            try { return PseudoTerminal.Start(ssh, password.Arguments + arguments, size.Width, size.Height, askPass); }
            catch { askPass.Dispose(); throw; }
        }

        public static void Connect(Connection connection)
        {
            var alias = connection.Alias;
            if (!ConfigDocument.IsAlias(alias)) throw new ArgumentException("Недопустимый псевдоним подключения.");
            var ssh = SshExecutable();
            var password = PasswordTarget.From(connection);
            string command = SshPasswords.Exists(password)
                ? Quote(ConsoleLauncher()) + " " + ConsoleLauncherArguments(Application.ExecutablePath, "--terminal " + alias + " " + password.Id)
                : Quote(ssh) + " " + alias;

            var terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe");
            try
            {
                using (Process.Start(new ProcessStartInfo(terminal, "-w 0 new-tab --title " + alias + " --suppressApplicationTitle " + command)
                {
                    UseShellExecute = false
                })) { }
            }
            catch (Win32Exception ex)
            {
                throw new IOException("Не удалось открыть Windows Terminal. Проверьте, что он установлен и псевдоним wt.exe включён в параметрах Windows.", ex);
            }
        }

        public static int RunInConsole(string alias, string passwordId)
        {
            if (!ConfigDocument.IsAlias(alias)) return 1;
            AttachConsole(uint.MaxValue);
            try
            {
                var password = FindPasswordTarget(alias, passwordId);
                return RunConsoleSsh(alias, password);
            }
            catch (Exception ex) when (Theme.IsFileError(ex) || ex is Win32Exception)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        internal static string ConsoleLauncher() => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        internal static string ConsoleLauncherArguments(string application, string arguments)
        {
            // Start the GUI executable from a process already attached to this tab's console.
            string script = "$connection = Start-Process -FilePath '" + application.Replace("'", "''") +
                "' -ArgumentList '" + arguments.Replace("'", "''") +
                "' -NoNewWindow -Wait -PassThru; exit $connection.ExitCode";
            return "-NoLogo -NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }

        internal static int RunConsoleSsh(string arguments, PasswordTarget password)
        {
            using (var askPass = SshPasswords.Exists(password) ? new SshAskPass(password) : null)
            {
                if (askPass != null) arguments = password.Arguments + arguments;
                var start = new ProcessStartInfo(SshExecutable(), arguments) { UseShellExecute = false };
                askPass?.SetEnvironment((key, value) => start.EnvironmentVariables[key] = value);
                using (var ssh = Process.Start(start))
                {
                    askPass?.Listen(ssh.Id);
                    ssh.WaitForExit();
                    return ssh.ExitCode;
                }
            }
        }

        public static string Quote(string argument)
        {
            var quoted = new StringBuilder().Append('"');
            int backslashes = 0;
            foreach (char ch in argument)
            {
                if (ch == '\\') { backslashes++; continue; }
                quoted.Append('\\', ch == '"' ? backslashes * 2 + 1 : backslashes);
                quoted.Append(ch);
                backslashes = 0;
            }
            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(uint processId);

        public static string SshExecutable()
        {
            var ssh = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
            if (!File.Exists(ssh))
                throw new IOException("Установите компонент «Клиент OpenSSH» в дополнительных компонентах Windows.");
            return ssh;
        }
    }
}
