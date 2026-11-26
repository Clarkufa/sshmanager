using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SshManager
{
    internal interface IRemoteFiles : IDisposable
    {
        bool Connected { get; }
        bool CanReplace { get; }
        string Protocol { get; }
        void Initialize();
        string CanonicalPath(string path);
        List<RemoteFile> List(string directory, CancellationToken token);
        FileMetadata Info(string path);
        void CreateDirectory(string path);
        void Delete(string path, bool directory);
        void Move(string source, string destination, bool replace = false);
        void Download(string path, Stream destination, Action<long, long> progress, CancellationToken token);
        void Upload(string path, Stream source, Action<long, long> progress, CancellationToken token, Action created, uint? permissions);
    }
}
