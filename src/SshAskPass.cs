using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace SshManager
{
    internal sealed class SshAskPass : IDisposable
    {
        private const string SessionVariable = "SSH_MANAGER_ASKPASS_SESSION";
        private const string AccountVariable = "SSH_MANAGER_ASKPASS_ACCOUNT";
        private const string ThemeVariable = "SSH_MANAGER_ASKPASS_DARK";
        private const string PipePrefix = "SshManager.AskPass.";
        private readonly string session = Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream pipe;
        private readonly PasswordTarget target;
        private int disposed;

        public SshAskPass(PasswordTarget target)
        {
            this.target = target;
            var security = new PipeSecurity();
            using (var identity = WindowsIdentity.GetCurrent())
            {
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            pipe = new NamedPipeServerStream(PipePrefix + session, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 4096, security);
        }

        public void SetEnvironment(Action<string, string> set)
        {
            set("SSH_ASKPASS", Application.ExecutablePath);
            set("SSH_ASKPASS_REQUIRE", "force");
            set(SessionVariable, session);
            set(AccountVariable, target?.Account ?? "");
            set(ThemeVariable, Theme.IsDark ? "1" : "0");
        }

        public void Listen(int sshProcessId)
        {
            new Thread(() => Serve(sshProcessId)) { IsBackground = true }.Start();
        }

        private void Serve(int sshProcessId)
        {
            using (pipe)
            {
                try
                {
                    while (Volatile.Read(ref disposed) == 0)
                    {
                        pipe.WaitForConnection();
                        if (!IsSshChild(pipe.SafePipeHandle, sshProcessId))
                        {
                            pipe.Disconnect();
                            continue;
                        }

                        // Only the askpass child of this SSH process can receive the password, once.
                        var password = SshPasswords.Read(target);
                        if (password == null) return;
                        try
                        {
                            pipe.Write(password, 0, password.Length);
                            pipe.WriteByte((byte)'\n');
                            pipe.Flush();
                            pipe.WaitForPipeDrain();
                        }
                        finally { SshPasswords.Clear(password); }
                        return;
                    }
                }
                catch (IOException) { /* The terminal or the client disconnected. */ }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) pipe.Dispose();
        }

        public static bool TryRun(string[] args, out int code)
        {
            code = 1;
            var session = Environment.GetEnvironmentVariable(SessionVariable);
            if (args.Length != 1 || !Guid.TryParseExact(session, "N", out _)) return false;

            string prompt = args[0];
            var displayPrompt = prompt.Trim();
            var account = Environment.GetEnvironmentVariable(AccountVariable);
            // Windows OpenSSH can retain the hint from the previous askpass invocation.
            bool password = displayPrompt == account + "'s password:" ||
                displayPrompt.Equals("(" + account + ") Password:", StringComparison.OrdinalIgnoreCase) ||
                displayPrompt.Equals("Password:", StringComparison.OrdinalIgnoreCase);
            if (password && !String.IsNullOrEmpty(account) && TryReply(session))
            {
                code = 0;
                return true;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.IsDark = Environment.GetEnvironmentVariable(ThemeVariable) == "1";
            if (displayPrompt.EndsWith("(yes/no/[fingerprint])?", StringComparison.Ordinal) ||
                displayPrompt.EndsWith("(yes/no)?", StringComparison.Ordinal))
            {
                if (MessageBox.Show(displayPrompt, "Подтверждение SSH", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) code = Reply("yes");
                return true;
            }
            if (!password && Environment.GetEnvironmentVariable("SSH_ASKPASS_PROMPT") == "none" &&
                prompt.Length > 0 && !Char.IsWhiteSpace(prompt[prompt.Length - 1]))
            {
                MessageBox.Show(prompt, "SSH", MessageBoxButtons.OK, MessageBoxIcon.Information);
                code = 0;
                return true;
            }
            using (var dialog = new AskPassDialog(prompt))
                if (dialog.ShowDialog() == DialogResult.OK) code = Reply(dialog.Response);
            return true;
        }

        private static bool TryReply(string session)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipePrefix + session, PipeDirection.In, PipeOptions.None,
                    TokenImpersonationLevel.Identification))
                {
                    client.Connect(650);
                    using (var output = Console.OpenStandardOutput())
                    {
                        var buffer = new byte[4096];
                        try
                        {
                            int count = client.Read(buffer, 0, buffer.Length);
                            if (count == 0) return false;
                            do { output.Write(buffer, 0, count); }
                            while ((count = client.Read(buffer, 0, buffer.Length)) != 0);
                            output.Flush();
                            return true;
                        }
                        finally { SshPasswords.Clear(buffer); }
                    }
                }
            }
            catch (IOException) { return false; }
            catch (TimeoutException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static int Reply(string answer)
        {
            var bytes = SshPasswords.Encode(answer);
            try
            {
                using (var output = Console.OpenStandardOutput())
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.WriteByte((byte)'\n');
                    output.Flush();
                }
                return 0;
            }
            catch (IOException) { return 1; }
            finally { SshPasswords.Clear(bytes); }
        }

        private static bool IsSshChild(SafePipeHandle pipeHandle, int sshProcessId)
        {
            if (!GetNamedPipeClientProcessId(pipeHandle, out uint id)) return false;
            try
            {
                using (var process = Process.GetProcessById((int)id))
                {
                    return NtQueryInformationProcess(process.Handle, 0, out var info, Marshal.SizeOf(typeof(ProcessBasicInformation)),
                        IntPtr.Zero) == 0 && info.ParentId.ToInt64() == sshProcessId &&
                        String.Equals(process.MainModule.FileName, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr ExitStatus, Peb, Affinity, Priority, ProcessId, ParentId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out ProcessBasicInformation info,
            int length, IntPtr returnLength);

        private sealed class AskPassDialog : ThemedForm
        {
            private readonly TextBox response;
            public string Response => response.Text;

            public AskPassDialog(string message)
            {
                Text = "Вход по SSH";
                Font = Theme.Body;
                BackColor = Theme.Canvas;
                ForeColor = Theme.Ink;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                MinimizeBox = MaximizeBox = false;
                ClientSize = new Size(500, 175);
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), ColumnCount = 1, RowCount = 3 };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                layout.Controls.Add(new Label { Text = message.Trim(), Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false,
                    TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
                response = new TextBox { UseSystemPasswordChar = true, AccessibleName = "Ответ на запрос SSH", MaxLength = SshPasswords.MaxBytes };
                layout.Controls.Add(Theme.Frame(response, new Padding(0, 0, 0, 5)), 0, 1);
                var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var send = Theme.Button("Продолжить", 118, true);
                send.Click += (sender, args) =>
                {
                    try { SshPasswords.Clear(SshPasswords.Encode(response.Text)); DialogResult = DialogResult.OK; }
                    catch (ArgumentException ex) { Theme.ShowError(this, ex); }
                };
                var cancel = Theme.Button("Отмена", 90);
                cancel.DialogResult = DialogResult.Cancel;
                actions.Controls.AddRange(new Control[] { send, cancel });
                layout.Controls.Add(actions, 0, 2);
                Controls.Add(layout);
                AcceptButton = send;
                CancelButton = cancel;
                ActiveControl = response;
            }
        }
    }
}
