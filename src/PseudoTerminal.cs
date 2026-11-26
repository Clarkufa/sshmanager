using System;
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SshManager
{
    internal sealed class PseudoTerminal : IDisposable
    {
        private readonly object gate = new object();
        private readonly BlockingCollection<byte[]> output = new BlockingCollection<byte[]>(64);
        private readonly BlockingCollection<byte[]> input = new BlockingCollection<byte[]>();
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly SshAskPass askPass;
        private SafeProcessHandle process;
        private IntPtr console;
        private volatile bool exited;
        private uint exitCode;
        private int disposed;

        private PseudoTerminal(IntPtr console, SafeProcessHandle process, Stream reader, Stream writer, SshAskPass askPass)
        {
            this.console = console;
            this.process = process;
            this.askPass = askPass;
            StartWorker(() => ReadOutput(reader));
            StartWorker(() => WriteInput(writer));
            StartWorker(WaitForExit);
        }

        public static PseudoTerminal Start(string executable, string arguments, int columns, int rows, SshAskPass askPass = null)
        {
            SafeFileHandle childInput, hostInput;
            if (!CreatePipe(out childInput, out hostInput, IntPtr.Zero, 0)) throw new Win32Exception();
            using (childInput)
            using (hostInput)
            {
                SafeFileHandle hostOutput, childOutput;
                if (!CreatePipe(out hostOutput, out childOutput, IntPtr.Zero, 0)) throw new Win32Exception();
                using (childOutput)
                using (hostOutput)
                {
                    IntPtr pseudo;
                    try { Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord(columns, rows), childInput, childOutput, 0, out pseudo)); }
                    catch (EntryPointNotFoundException ex)
                    {
                        throw new IOException("Встроенный терминал требует Windows 10 версии 1809 или новее.", ex);
                    }

                    Stream reader = null, writer = null;
                    try
                    {
                        // FileStream owns its handle. Duplicate the host ends so the setup handles can all be closed here.
                        reader = new FileStream(Duplicate(hostOutput), FileAccess.Read, 4096, false);
                        writer = new FileStream(Duplicate(hostInput), FileAccess.Write, 4096, false);
                        var child = CreateChild(executable, arguments, pseudo, askPass, out int processId);
                        askPass?.Listen(processId);
                        return new PseudoTerminal(pseudo, child, reader, writer, askPass);
                    }
                    catch
                    {
                        if (reader != null) reader.Dispose();
                        hostOutput.Dispose();
                        childOutput.Dispose();
                        if (writer != null) writer.Dispose();
                        ClosePseudoConsole(pseudo);
                        throw;
                    }
                }
            }
        }

        public void Send(string text)
        {
            if (text.Length != 0) Send(Encoding.UTF8.GetBytes(text));
        }

        public void Send(byte[] bytes)
        {
            if (closing.IsCancellationRequested || input.IsAddingCompleted || bytes.Length == 0) return;
            try { input.Add(bytes); }
            catch (InvalidOperationException) { /* The process exited while input was queued. */ }
        }

        public void Resize(int columns, int rows)
        {
            lock (gate)
            {
                if (console != IntPtr.Zero) ResizePseudoConsole(console, new Coord(columns, rows));
            }
        }

        public bool TryRead(out byte[] bytes) { return output.TryTake(out bytes); }

        public bool TryGetExitCode(out uint code)
        {
            if (exited && output.IsCompleted) { code = exitCode; return true; }
            code = 0;
            return false;
        }

        private void ReadOutput(Stream reader)
        {
            using (reader)
            {
                var buffer = new byte[8192];
                try
                {
                    int length;
                    while (!closing.IsCancellationRequested && (length = reader.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        var bytes = new byte[length];
                        Buffer.BlockCopy(buffer, 0, bytes, 0, length);
                        output.Add(bytes, closing.Token);
                    }
                }
                catch (IOException) { /* The pseudoconsole closed the pipe. */ }
                catch (OperationCanceledException) { }
                finally { output.CompleteAdding(); }
            }
        }

        private void WriteInput(Stream writer)
        {
            using (writer)
            {
                try
                {
                    foreach (var bytes in input.GetConsumingEnumerable(closing.Token))
                    {
                        writer.Write(bytes, 0, bytes.Length);
                        writer.Flush();
                    }
                }
                catch (IOException) { /* The pseudoconsole closed the pipe. */ }
                catch (OperationCanceledException) { }
            }
        }

        private void WaitForExit()
        {
            WaitForSingleObject(process, uint.MaxValue);
            if (!GetExitCodeProcess(process, out exitCode)) exitCode = 1;
            IntPtr finishedConsole;
            lock (gate)
            {
                process.Dispose();
                process = null;
                finishedConsole = console;
                console = IntPtr.Zero;
            }
            input.CompleteAdding();
            askPass?.Dispose();
            ClosePseudoConsole(finishedConsole);
            exited = true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            closing.Cancel();
            askPass?.Dispose();
            lock (gate)
            {
                if (process != null) TerminateProcess(process, 1);
            }
        }

        private static void StartWorker(ThreadStart callback) { new Thread(callback) { IsBackground = true }.Start(); }

        private static SafeFileHandle Duplicate(SafeFileHandle handle)
        {
            var current = GetCurrentProcess();
            SafeFileHandle result;
            if (!DuplicateHandle(current, handle, current, out result, 0, false, 2)) throw new Win32Exception();
            return result;
        }

        private static SafeProcessHandle CreateChild(string executable, string arguments, IntPtr pseudo, SshAskPass askPass, out int processId)
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception();
            var attributes = Marshal.AllocHGlobal(size);
            try
            {
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
                try
                {
                    var pseudoconsoleAttribute = new IntPtr(0x00020016);
                    if (!UpdateProcThreadAttribute(attributes, 0, pseudoconsoleAttribute, pseudo, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception();
                    // STARTF_USESTDHANDLES with empty handles: a parent with redirected std handles would otherwise pass them
                    // on, and the child would write there instead of into the pseudo console.
                    var startup = new StartupInfoEx { Info = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfoEx)), Flags = 0x100 }, Attributes = attributes };
                    var command = new StringBuilder("\"" + executable + "\" " + arguments);
                    IntPtr environment = IntPtr.Zero;
                    try
                    {
                        const uint extendedStartup = 0x00080000, unicodeEnvironment = 0x00000400;
                        if (askPass != null)
                        {
                            var start = new ProcessStartInfo();
                            askPass.SetEnvironment((key, value) => start.EnvironmentVariables[key] = value);
                            var values = start.EnvironmentVariables.Cast<DictionaryEntry>()
                                .OrderBy(item => (string)item.Key, StringComparer.OrdinalIgnoreCase)
                                .Select(item => item.Key + "=" + item.Value);
                            environment = Marshal.StringToHGlobalUni(String.Join("\0", values) + "\0\0");
                        }
                        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, extendedStartup | unicodeEnvironment,
                            environment, null, ref startup, out ProcessInfo child)) throw new Win32Exception();
                        CloseHandle(child.Thread);
                        processId = child.ProcessId;
                        return new SafeProcessHandle(child.Process, true);
                    }
                    finally { if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment); }
                }
                finally { DeleteProcThreadAttributeList(attributes); }
            }
            finally { Marshal.FreeHGlobal(attributes); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord
        {
            private readonly short columns, rows;
            public Coord(int columns, int rows)
            {
                this.columns = (short)Math.Max(1, Math.Min(columns, short.MaxValue));
                this.rows = (short)Math.Max(1, Math.Min(rows, short.MaxValue));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public int Size;
            private IntPtr reserved, desktop, title;
            private int x, y, width, height, columns, rows, colors;
            public int Flags;
            private short show, reservedSize;
            private IntPtr reservedBytes, input, output, error;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId; private int threadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr security, uint size);
        [DllImport("kernel32.dll")]
        private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudo);
        [DllImport("kernel32.dll")]
        private static extern int ResizePseudoConsole(IntPtr pseudo, Coord size);
        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr pseudo);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr previousSize);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags,
            IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr source, SafeFileHandle handle, IntPtr target, out SafeFileHandle duplicate, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll")]
        private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);
        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(SafeProcessHandle handle, uint code);
    }
}
