using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SshManager
{
    /// <summary>
    /// «Через SSH-сервер»: ssh -N -L 127.0.0.1:&lt;free port&gt;:host:port alias, started like the SFTP client (saved
    /// password through SSH_ASKPASS). Lives as long as the VNC session.
    /// </summary>
    internal sealed class SshTunnel : IDisposable
    {
        private readonly Process process;
        private readonly SshAskPass askPass;
        private readonly StringBuilder errors = new StringBuilder();
        public int LocalPort { get; }
        /// <summary>Checks only: another OpenSSH config instead of ~/.ssh/config (passed to ssh with -F).</summary>
        internal static string ConfigOverride;

        private SshTunnel(Process process, SshAskPass askPass, int localPort)
        {
            this.process = process;
            this.askPass = askPass;
            LocalPort = localPort;
        }

        /// <summary>Starts the tunnel and waits up to 15 seconds until the local port accepts connections.</summary>
        public static SshTunnel Open(string alias, string host, int port)
        {
            if (!ConfigDocument.IsAlias(alias)) throw new RfbException("Недопустимый псевдоним SSH-сервера.");
            string configPath = ConfigOverride ?? Path.Combine(Terminal.SshDirectory, "config");
            var connection = new ConfigFile(configPath).Load().Connections()
                .FirstOrDefault(item => String.Equals(item.Alias, alias, StringComparison.OrdinalIgnoreCase));
            if (connection == null) throw new RfbException("Не удалось открыть SSH-туннель: сервер «" + alias + "» не найден в SSH config.");
            var target = PasswordTarget.From(connection);
            var password = SshPasswords.Exists(target) ? target : null;
            int localPort = FreePort();
            string destination = (host.Contains(":") ? "[" + host + "]" : host) + ":" + port;
            string arguments = (password?.Arguments ?? "-o BatchMode=yes ") +
                "-N -T -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -o ConnectTimeout=15 " +
                (ConfigOverride == null ? "" : "-F " + Terminal.Quote(ConfigOverride) + " ") +
                "-L 127.0.0.1:" + localPort + ":" + destination + " " + alias;
            var start = new ProcessStartInfo(Terminal.SshExecutable(), arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var askPass = new SshAskPass(password);
            var process = new Process { StartInfo = start };
            var tunnel = new SshTunnel(process, askPass, localPort);
            try
            {
                askPass.SetEnvironment((key, value) => start.EnvironmentVariables[key] = value);
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data == null) return;
                    lock (tunnel.errors)
                    {
                        if (tunnel.errors.Length > 3000) tunnel.errors.Clear();
                        tunnel.errors.AppendLine(args.Data.Length > 500 ? args.Data.Substring(0, 500) : args.Data);
                    }
                };
                process.Start();
                if (password != null) askPass.Listen(process.Id);
                process.BeginErrorReadLine();
                process.StandardInput.Close();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    if (process.HasExited)
                    {
                        Thread.Sleep(100);
                        string reason;
                        lock (tunnel.errors) reason = tunnel.errors.ToString().Trim();
                        throw new RfbException("Не удалось открыть SSH-туннель: " + (reason.Length == 0 ? "ssh завершился с кодом " + process.ExitCode + "." : reason) +
                            (password == null ? "\nСохраните пароль SSH-сервера «" + alias + "» или используйте ключ." : ""));
                    }
                    if (Accepts(localPort)) return tunnel;
                    Thread.Sleep(200);
                }
                throw new RfbException("Не удалось открыть SSH-туннель: сервер «" + alias + "» не ответил за 20 секунд.");
            }
            catch { tunnel.Dispose(); throw; }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private static bool Accepts(int port)
        {
            using (var probe = new TcpClient())
            {
                try
                {
                    var attempt = probe.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!attempt.AsyncWaitHandle.WaitOne(300)) return false;
                    probe.EndConnect(attempt);
                    return true;
                }
                catch (SocketException) { return false; }
            }
        }

        public void Dispose()
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception) { }
            process.Dispose();
            askPass.Dispose();
        }
    }
}
