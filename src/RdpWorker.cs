using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SshManager
{
    internal sealed class RdpWorker : IDisposable
    {
        internal enum Packet
        {
            Config = 1, Mouse, Key, Resize, LocalClipboard, TrustCertificate, Focus, Stop, Credentials,
            Connected = 101, Error, Certificate, RemoteClipboard, Heartbeat, Authenticate
        }

        private const int MaxMessage = 1024 * 1024, MaxSide = 8192, HeaderSize = 12;
        private readonly string id = Guid.NewGuid().ToString("N");
        private readonly BlockingCollection<byte[]> input = new BlockingCollection<byte[]>(512);
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly MemoryMappedFile mapping;
        private readonly MemoryMappedViewAccessor view;
        private readonly Mutex frameMutex;
        private readonly NamedPipeServerStream pipe;
        private readonly WorkerJob job = new WorkerJob();
        private int generation;
        private int disposed;
        private long activity = Stopwatch.GetTimestamp();

        public event Action<Packet, byte[]> Received;
        public event Action<string> Ended;
        public double SecondsSinceActivity => (Stopwatch.GetTimestamp() - Interlocked.Read(ref activity)) /
                                               (double)Stopwatch.Frequency;
        public int ProcessId { get; private set; }

        public RdpWorker()
        {
            try
            {
                var user = WindowsIdentity.GetCurrent().User;
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
                pipe = new NamedPipeServerStream("SshManagerRdp-" + id, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, security);
                mapping = MemoryMappedFile.CreateNew(@"Local\SshManagerRdpFrame-" + id,
                    HeaderSize + (long)MaxSide * MaxSide * 4, MemoryMappedFileAccess.ReadWrite);
                view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                frameMutex = new Mutex(false, @"Local\SshManagerRdpMutex-" + id);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Start(IEnumerable<string> arguments)
        {
            byte[] config = Encode(Packet.Config, Encoding.UTF8.GetBytes(string.Join("\0", arguments) + "\0"));
            input.Add(config);
            Task.Run((Func<Task>)Run);
        }

        private async Task Run()
        {
            string error = null;
            Task writer = null;
            try
            {
                closing.Token.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(WorkerPath.Value, id)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(WorkerPath.Value)
                };
                using (var process = Process.Start(start))
                {
                    try { job.Attach(process); }
                    catch { if (!process.HasExited) process.Kill(); throw; }
                    ProcessId = process.Id;
                    process.EnableRaisingEvents = true;
                    process.Exited += (sender, args) => { if (!pipe.IsConnected) pipe.Dispose(); };
                    if (process.HasExited) throw new IOException("FreeRDP exited before connecting to the session.");
                    await pipe.WaitForConnectionAsync(closing.Token).ConfigureAwait(false);
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != process.Id)
                        throw new IOException("Unexpected connection to the FreeRDP session pipe.");
                    writer = Task.Run((Action)WritePackets);
                    try
                    {
                        using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                        {
                            while (!closing.IsCancellationRequested)
                            {
                                var type = (Packet)reader.ReadInt32();
                                var size = reader.ReadInt32();
                                if (size < 0 || size > MaxMessage) throw new IOException("Invalid FreeRDP packet.");
                                var data = reader.ReadBytes(size);
                                if (data.Length != size) throw new EndOfStreamException();
                                Interlocked.Exchange(ref activity, Stopwatch.GetTimestamp());
                                if (type != Packet.Heartbeat) Received?.Invoke(type, data);
                            }
                        }
                    }
                    catch (EndOfStreamException)
                    {
                        if (!closing.IsCancellationRequested && (!process.WaitForExit(1000) || process.ExitCode != 0))
                            error = "Процесс FreeRDP завершился с ошибкой.";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException || ex is OperationCanceledException || ex is CryptographicException)
            {
                error = ex.Message;
            }
            finally
            {
                if (!closing.IsCancellationRequested) Ended?.Invoke(error);
                closing.Cancel();
                pipe.Dispose();
                if (writer != null) await writer.ConfigureAwait(false);
                while (input.TryTake(out var abandoned)) Array.Clear(abandoned, 0, abandoned.Length);
                job.Dispose();
            }
        }

        private void WritePackets()
        {
            try
            {
                foreach (var bytes in input.GetConsumingEnumerable(closing.Token))
                {
                    try { pipe.Write(bytes, 0, bytes.Length); }
                    finally { Array.Clear(bytes, 0, bytes.Length); }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is OperationCanceledException)
            {
                pipe.Dispose();
            }
        }

        public bool Send(Packet type, params int[] values)
        {
            var data = new byte[values.Length * sizeof(int)];
            Buffer.BlockCopy(values, 0, data, 0, data.Length);
            return Send(type, data);
        }

        public bool Send(Packet type, byte[] data)
        {
            return data.Length <= MaxMessage && !closing.IsCancellationRequested && input.TryAdd(Encode(type, data));
        }

        private static byte[] Encode(Packet type, byte[] data)
        {
            var packet = new byte[8 + data.Length];
            Buffer.BlockCopy(new[] { (int)type, data.Length }, 0, packet, 0, 8);
            Buffer.BlockCopy(data, 0, packet, 8, data.Length);
            if (type == Packet.Config || type == Packet.Credentials) Array.Clear(data, 0, data.Length);
            return packet;
        }

        // The UI tries the frame lock without waiting. The next timer tick can use a newer frame.
        public bool ReadFrame(ref Bitmap image)
        {
            try
            {
                if (!frameMutex.WaitOne(0)) return false;
            }
            catch (AbandonedMutexException)
            {
                frameMutex.ReleaseMutex();
                return false;
            }
            try
            {
                int next = view.ReadInt32(0);
                if (next == generation) return false;
                int width = view.ReadInt32(4), height = view.ReadInt32(8);
                if (width < 1 || width > MaxSide || height < 1 || height > MaxSide) return false;
                if (image == null || image.Width != width || image.Height != height)
                {
                    image?.Dispose();
                    image = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                }
                var bits = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, image.PixelFormat);
                try
                {
                    var source = IntPtr.Add(view.SafeMemoryMappedViewHandle.DangerousGetHandle(), HeaderSize);
                    CopyMemory(bits.Scan0, source, new UIntPtr((uint)(width * height * 4)));
                }
                finally { image.UnlockBits(bits); }
                generation = next;
                return true;
            }
            finally { frameMutex.ReleaseMutex(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            closing.Cancel();
            job.Dispose();
            pipe?.Dispose();
            view?.Dispose();
            mapping?.Dispose();
            frameMutex?.Dispose();
        }

        private static readonly Lazy<string> WorkerPath = new Lazy<string>(ExtractWorker);

        private static string ExtractWorker()
        {
            var assembly = typeof(RdpWorker).Assembly;
            string arch = IntPtr.Size == 8 && IsWow64Process2(GetCurrentProcess(), out _, out ushort nativeMachine) &&
                          nativeMachine == 0x8664 ? "x64" : "x86";
            using (var worker = assembly.GetManifestResourceStream("SshManager.RdpWorker." + arch + ".exe"))
            using (var sha = SHA256.Create())
            {
                if (worker == null) throw new IOException("FreeRDP is missing from this SSH Manager build.");
                byte[] hash = sha.ComputeHash(worker);
                string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SSH Manager", "FreeRDP", BitConverter.ToString(hash).Replace("-", ""));
                Directory.CreateDirectory(baseDir);
                string target = Path.Combine(baseDir, "SshManagerRdp.exe");
                if (File.Exists(target))
                {
                    using (var copy = File.OpenRead(target))
                        if (Convert.ToBase64String(sha.ComputeHash(copy)) == Convert.ToBase64String(hash)) return target;
                    throw new IOException("The cached FreeRDP executable failed its integrity check.");
                }
                string temporary = Path.Combine(baseDir, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    worker.Position = 0;
                    using (var file = new FileStream(temporary, FileMode.CreateNew)) worker.CopyTo(file);
                    try { File.Move(temporary, target); }
                    catch (IOException) when (File.Exists(target))
                    {
                        using (var copy = File.OpenRead(target))
                            if (Convert.ToBase64String(sha.ComputeHash(copy)) != Convert.ToBase64String(hash)) throw;
                    }
                }
                finally { File.Delete(temporary); }
                using (var licenses = assembly.GetManifestResourceStream("SshManager.Rdp-LICENSES.txt"))
                using (var file = new FileStream(Path.Combine(baseDir, "LICENSES.txt"), FileMode.Create, FileAccess.Write, FileShare.Read))
                    licenses?.CopyTo(file);
                return target;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort machine, out ushort nativeMachine);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);

        private sealed class WorkerJob : IDisposable
        {
            private readonly object guard = new object();
            private IntPtr handle;
            private bool closed;

            public void Attach(Process process)
            {
                lock (guard)
                {
                    if (closed) throw new OperationCanceledException();
                    handle = CreateJobObject(IntPtr.Zero, null);
                    if (handle == IntPtr.Zero) throw new Win32Exception();
                    var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
                    if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf(typeof(JobLimits))) ||
                        !AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception();
                }
            }

            public void Dispose()
            {
                lock (guard)
                {
                    closed = true;
                    if (handle != IntPtr.Zero) CloseHandle(handle);
                    handle = IntPtr.Zero;
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BasicLimits
            {
                public long ProcessTime, JobTime;
                public uint Flags;
                public UIntPtr MinWorkingSet, MaxWorkingSet;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint Priority, Scheduling;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobLimits
            {
                public BasicLimits Basic;
                public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
                public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
                public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobLimits limits, int length);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
