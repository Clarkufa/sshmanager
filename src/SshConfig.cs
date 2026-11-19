using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SshManager
{
    internal sealed class ConfigFile
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal const string WriteLock = @"Local\SshManager.ConfigWrite";
        private readonly string path;
        private byte[] original;

        public ConfigFile(string path) { this.path = path; }

        public ConfigDocument Load()
        {
            var bytes = Read();
            var document = new ConfigDocument(Decode(bytes));
            original = bytes;
            return document;
        }

        internal static string Decode(byte[] bytes)
        {
            string contents;
            try { contents = bytes == null ? "" : Utf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                throw new IOException("Файл config должен быть в кодировке UTF-8.");
            }
            if (contents.IndexOf('\0') >= 0)
                throw new IOException("Файл config содержит двоичные данные или записан в UTF-16.");

            return contents;
        }

        public void Save(ConfigDocument document, PasswordUpdates passwords = null)
        {
            var bytes = Utf8.GetBytes(document.ToString());
            var directory = Path.GetDirectoryName(path);
            var temp = Path.Combine(directory, "config." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var mutex = new Mutex(false, WriteLock))
            {
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new IOException("Файл config занят другим окном SSH Manager.");
                }
                catch (AbandonedMutexException) { /* The previous writer exited. */ }

                try
                {
                    CheckOriginal();
                    bool configChanged = original == null || !original.SequenceEqual(bytes);
                    if (configChanged)
                    {
                        Directory.CreateDirectory(directory);
                        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush(true);
                        }
                    }
                    void Commit()
                    {
                        CheckOriginal();
                        if (!configChanged) return;
                        if (original == null) File.Move(temp, path);
                        else File.Replace(temp, path, path + ".bak");
                        original = bytes;
                    }
                    if (passwords == null) Commit();
                    else passwords.Apply(Commit);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
        }

        private void CheckOriginal()
        {
            var current = Read();
            if (original == null ? current != null : current == null || !original.SequenceEqual(current))
                throw new IOException("Файл config изменился в другой программе. Закройте окно редактирования и обновите список перед повторной попыткой.");
        }

        private byte[] Read()
        {
            try { return File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }

    internal sealed class ConfigDocument
    {
        private static readonly Regex AliasPattern = new Regex(@"\A[A-Za-z0-9_.][A-Za-z0-9_.-]*\z");
        private static readonly Regex HostNamePattern = new Regex(@"\A[A-Za-z0-9_.:%-]+\z");
        private readonly List<ConfigLine> lines;
        private readonly string newline;
        private readonly bool bom;

        public ConfigDocument(string text)
        {
            bom = text.StartsWith("\uFEFF", StringComparison.Ordinal);
            if (bom) text = text.Substring(1);
            lines = Regex.Matches(text, @"[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$")
                .Cast<Match>().Select(m => new ConfigLine(m.Value)).ToList();
            newline = lines.Select(line => line.Ending).FirstOrDefault(ending => ending.Length > 0) ?? "\r\n";
        }

        public IList<Connection> Connections()
        {
            var connections = new List<Connection>();
            for (int index = 0; index < lines.Count; index++)
            {
                var header = lines[index];
                if (header.Keyword != "host" || header.Args.Count != 1 || !IsAlias(header.Args[0].Value)) continue;
                int end = index + 1;
                while (end < lines.Count && !lines[end].IsBoundary) end++;
                connections.Add(new Connection(index, header.Args[0].Value, lines.GetRange(index + 1, end - index - 1)));
                index = end - 1;
            }
            return connections;
        }

        public void Validate(Connection connection, ConnectionValues values)
        {
            if (!IsAlias(values.Alias))
                throw new ArgumentException("Псевдоним должен начинаться с латинской буквы, цифры, точки или подчёркивания. Внутри псевдонима также допустим дефис.");

            if ((connection == null || !values.Alias.Equals(connection.Alias, StringComparison.OrdinalIgnoreCase)) &&
                lines.Any(line => line.Keyword == "host" && line.Args.Any(arg =>
                    values.Alias.Equals(arg.Value, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("Этот псевдоним уже указан в config.");

            if (Changed(connection, "hostname", values.HostName) && values.HostName.Length > 0 && !HostNamePattern.IsMatch(values.HostName))
                throw new ArgumentException("Укажите DNS-имя, IPv4 или IPv6 без пробелов. Порт задаётся отдельно.");

            if (Changed(connection, "user", values.User) && values.User.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || "#'\"".Contains(ch)))
                throw new ArgumentException("Укажите имя пользователя без пробелов, решётки и кавычек.");

            int port;
            if (Changed(connection, "port", values.Port) && values.Port.Length > 0 &&
                (!int.TryParse(values.Port, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535))
                throw new ArgumentException("Порт должен быть числом от 1 до 65535.");
        }

        public void Update(Connection connection, ConnectionValues values)
        {
            Validate(connection, values);
            if (connection == null)
            {
                Add(values);
                return;
            }

            var header = lines[connection.Index];
            if (values.Alias != connection.Alias) header.ReplaceValue(values.Alias);
            string indent = connection.Body.Where(line => line.Keyword != "").Select(line => line.Indent).FirstOrDefault() ?? "    ";
            int insert = connection.Index + 1;
            WriteSetting(connection, ref insert, indent, "HostName", values.HostName);
            WriteSetting(connection, ref insert, indent, "User", values.User);
            WriteSetting(connection, ref insert, indent, "Port", values.Port);

            var previousKeys = connection.Keys;
            if (previousKeys.Count > 0) insert = lines.IndexOf(connection.Body[previousKeys.Last().LineIndex]) + 1;
            foreach (var key in values.Keys)
            {
                if (key.LineIndex < 0) Insert(ref insert, indent + "IdentityFile " + Quote(key.Value));
                else
                {
                    var line = connection.Body[key.LineIndex];
                    if (line.Value != key.Value) line.ReplaceValue(Quote(key.Value));
                }
            }
            var retained = new HashSet<int>(values.Keys.Select(key => key.LineIndex));
            foreach (var key in previousKeys)
                if (!retained.Contains(key.LineIndex)) RemoveSetting(connection.Body[key.LineIndex]);
        }

        public void Delete(Connection connection)
        {
            lines.RemoveAt(connection.Index);
            foreach (var line in connection.Body)
                if (!line.IsTrivia) lines.Remove(line);
        }

        public static bool IsAlias(string value) { return AliasPattern.IsMatch(value); }

        internal IEnumerable<ConfigLine> Directives { get { return lines.Where(line => line.Keyword.Length != 0); } }

        private static bool Changed(Connection connection, string keyword, string value)
        {
            return connection == null || connection.Get(keyword) != value;
        }

        private void Add(ConnectionValues values)
        {
            int insert = lines.FindIndex(line => line.IsBoundary);
            if (insert < 0) insert = lines.Count;
            else while (insert > 0 && lines[insert - 1].IsTrivia) insert--;

            if (insert > 0 && lines[insert - 1].Content.Trim().Length > 0) Insert(ref insert, "");
            Insert(ref insert, "Host " + values.Alias);
            if (values.HostName.Length > 0) Insert(ref insert, "    HostName " + Quote(values.HostName));
            if (values.User.Length > 0) Insert(ref insert, "    User " + Quote(values.User));
            if (values.Port.Length > 0) Insert(ref insert, "    Port " + Quote(values.Port));
            foreach (var key in values.Keys) Insert(ref insert, "    IdentityFile " + Quote(key.Value));
            if (insert < lines.Count && lines[insert].Content.Trim().Length > 0) Insert(ref insert, "");
        }

        private void WriteSetting(Connection connection, ref int insert, string indent, string name, string value)
        {
            var settings = connection.Settings(name.ToLowerInvariant()).ToList();
            if ((settings.Count == 0 ? "" : settings[0].Value) == value) return;

            if (value.Length == 0)
            {
                foreach (var setting in settings) RemoveSetting(setting);
            }
            else if (settings.Count == 0) Insert(ref insert, indent + name + " " + Quote(value));
            else settings[0].ReplaceValue(Quote(value));
        }

        private void RemoveSetting(ConfigLine line)
        {
            if (line.Comment.Length == 0) lines.Remove(line);
            else line.Content = line.Indent + line.Comment;
        }

        private void Insert(ref int index, string content)
        {
            if (index > 0 && lines[index - 1].Ending.Length == 0) lines[index - 1].Ending = newline;
            lines.Insert(index++, new ConfigLine(content + newline));
        }

        internal static string Quote(string value)
        {
            // Include quotes for spaces and comment characters. Escape literal backslashes and quotes.
            if (value.Any(ch => char.IsControl(ch))) throw new ArgumentException("Параметр содержит недопустимый управляющий символ.");
            if (value.Any(ch => char.IsWhiteSpace(ch) || "#'\"\\".Contains(ch)))
                return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return value;
        }

        public override string ToString()
        {
            return (bom ? "\uFEFF" : "") + string.Concat(lines.Select(line => line.Content + line.Ending));
        }
    }

    internal sealed class ConfigLine
    {
        private static readonly Regex Directive = new Regex(@"^(?<indent>[ \t]*)(?<name>[a-zA-Z][a-zA-Z0-9]*)(?=$|[ \t=])(?:[ \t]*=[ \t]*|[ \t]*)");
        private static readonly Regex Argument = new Regex(@"(?:[^\s""'\\]|\\.|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')+");
        private static readonly Regex ArgumentPart = new Regex(@"\\[\\""']|""|'");
        private string content;

        public ConfigLine(string text)
        {
            Content = text.TrimEnd('\r', '\n');
            Ending = text.Substring(Content.Length);
        }

        public string Content
        {
            get { return content; }
            set
            {
                content = value;
                var directive = Directive.Match(content);
                Keyword = directive.Groups["name"].Value.ToLowerInvariant();
                Indent = directive.Groups["indent"].Value;
                Comment = "";
                Args = new List<ConfigArgument>();
                if (!directive.Success) return;

                int index = directive.Length;
                while (index < content.Length)
                {
                    if (content[index] == '#')
                    {
                        Comment = content.Substring(index);
                        return;
                    }
                    var argument = Argument.Match(content, index);
                    if (!argument.Success || argument.Index != index) return;
                    Args.Add(new ConfigArgument(argument, Decode(argument.Value)));
                    index += argument.Length;
                    while (index < content.Length && char.IsWhiteSpace(content[index])) index++;
                }
            }
        }

        private static string Decode(string raw)
        {
            char quotation = '\0';
            return ArgumentPart.Replace(raw, part =>
            {
                if (part.Length == 2) return part.Value.Substring(1);
                char ch = part.Value[0];
                if (quotation != '\0' && quotation != ch) return part.Value;
                quotation = quotation == ch ? '\0' : ch;
                return "";
            });
        }

        public bool IsTrivia { get { return String.IsNullOrWhiteSpace(Content) || Content.TrimStart().StartsWith("#", StringComparison.Ordinal); } }
        public bool IsBoundary { get { return Keyword == "host" || Keyword == "match"; } }
        public string Ending { get; set; }
        public string Keyword { get; private set; }
        public string Indent { get; private set; }
        public string Comment { get; private set; }
        public List<ConfigArgument> Args { get; private set; }
        public string Value { get { return Args.Count == 1 ? Args[0].Value : ""; } }

        public void ReplaceValue(string value)
        {
            ReplaceArgument(0, value);
        }

        internal void ReplaceArgument(int index, string value)
        {
            var arg = Args[index];
            Content = Content.Substring(0, arg.Offset) + value + Content.Substring(arg.Offset + arg.Length);
        }
    }

    internal sealed class ConfigArgument
    {
        public readonly int Offset;
        public readonly int Length;
        public readonly string Value;

        public ConfigArgument(Match match, string value)
        {
            Offset = match.Index;
            Length = match.Length;
            Value = value;
        }
    }

    internal sealed class Connection
    {
        public readonly int Index;
        public readonly string Alias;
        public readonly List<ConfigLine> Body;

        public Connection(int index, string alias, List<ConfigLine> body)
        {
            Index = index;
            Alias = alias;
            Body = body;
        }

        public IEnumerable<ConfigLine> Settings(string keyword)
        {
            return Body.Where(line => line.Keyword == keyword && line.Args.Count == 1);
        }

        public string Get(string keyword)
        {
            var line = Settings(keyword).FirstOrDefault();
            return line == null ? "" : line.Value;
        }

        public List<Identity> Keys
        {
            get
            {
                return Body.Select((line, index) => line.Keyword == "identityfile" && line.Args.Count == 1
                    ? new Identity(line.Value, index) : null).Where(key => key != null).ToList();
            }
        }
    }

    internal sealed class Identity
    {
        public readonly string Value;
        public readonly int LineIndex;
        public Identity(string value, int lineIndex = -1) { Value = value; LineIndex = lineIndex; }
        public override string ToString() { return Value; }
    }

    internal sealed class ConnectionValues
    {
        public string Alias;
        public string HostName;
        public string User;
        public string Port;
        public List<Identity> Keys;
    }
}
