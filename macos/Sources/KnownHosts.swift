import AppKit
import CryptoKit
import Foundation
import SwiftUI

/// One key line of a known_hosts file (docs/ui-spec.md §9).
struct KnownHostEntry: Identifiable {
    let line: Int
    let marker: String
    let hosts: String
    let keyType: String
    let key: String
    var revealed: String?

    var id: Int { line }
    var hashed: Bool { hosts.hasPrefix("|1|") }
    var display: String { hashed ? revealed ?? "скрытое имя" : hosts }

    var fingerprint: String {
        guard let blob = Data(base64Encoded: key) else { return "повреждённый ключ" }
        let digest = Data(SHA256.hash(data: blob)).base64EncodedString()
        return "SHA256:" + digest.trimmingCharacters(in: CharacterSet(charactersIn: "="))
    }

    /// True when the entry is for exactly `name` (`host` or `[host]:port`).
    func isFor(_ name: String) -> Bool {
        if hashed {
            let parts = hosts.split(separator: "|", omittingEmptySubsequences: false)
            guard parts.count == 4, let salt = Data(base64Encoded: String(parts[2])),
                  let expected = Data(base64Encoded: String(parts[3])) else { return false }
            let code = HMAC<Insecure.SHA1>.authenticationCode(for: Data(name.utf8), using: SymmetricKey(data: salt))
            return Data(code) == expected
        }
        return hosts.split(separator: ",").contains { pattern in
            guard !pattern.hasPrefix("!") else { return false }
            let expression = "^" + NSRegularExpression.escapedPattern(for: String(pattern))
                .replacingOccurrences(of: "\\*", with: ".*").replacingOccurrences(of: "\\?", with: ".") + "$"
            return name.range(of: expression, options: [.regularExpression, .caseInsensitive]) != nil
        }
    }

    static func parse(_ line: String, at index: Int = -1) -> KnownHostEntry? {
        var fields = line.split(whereSeparator: { $0 == " " || $0 == "\t" }).map(String.init)
        guard let first = fields.first, !first.hasPrefix("#") else { return nil }
        let marker = first.hasPrefix("@") ? fields.removeFirst() : ""
        guard fields.count >= 3 else { return nil }
        return KnownHostEntry(line: index, marker: marker, hosts: fields[0], keyType: fields[1], key: fields[2])
    }
}

/// A known_hosts file: entries are removed or added line by line, everything else stays as it was.
struct KnownHostsFile {
    let url: URL
    private(set) var lines: [String] = []
    private(set) var entries: [KnownHostEntry] = []

    init(url: URL) throws {
        self.url = url
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        let data = try readRegularFile(url, limit: 16 * 1024 * 1024)
        lines = String(decoding: data, as: UTF8.self).components(separatedBy: "\n").map {
            $0.hasSuffix("\r") ? String($0.dropLast()) : $0
        }
        if lines.last == "" { lines.removeLast() }
        entries = lines.enumerated().compactMap { KnownHostEntry.parse($0.element, at: $0.offset) }
    }

    /// The name ssh writes: `host`, or `[host]:port` for another port.
    static func hostName(_ host: String, port: Int) -> String {
        let bare = host.trimmingCharacters(in: CharacterSet(charactersIn: "[] "))
        return port == 22 ? bare : "[\(bare)]:\(port)"
    }

    /// UserKnownHostsFile as OpenSSH reads it: empty — `fallback`; `~` and `%d` — home, `%u` — the user.
    static func resolve(_ configured: String, fallback: URL) -> URL {
        var value = configured.trimmingCharacters(in: .whitespaces)
        guard !value.isEmpty else { return fallback }
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        if value == "~" || value.hasPrefix("~/") { value = home + value.dropFirst() }
        value = value.replacingOccurrences(of: "%d", with: home).replacingOccurrences(of: "%u", with: NSUserName())
            .replacingOccurrences(of: "%%", with: "%")
        return URL(fileURLWithPath: value)
    }

    /// One file: absolute or from ~, no control characters or quotes.
    static func validSetting(_ value: String) -> Bool {
        !value.isEmpty && value.count <= 1024 && !value.contains("\"") &&
            !value.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) &&
            (value.hasPrefix("/") || value.hasPrefix("~/"))
    }

    /// Plain names by substring, hashed names only by the exact host (and then they show it).
    func search(_ text: String) -> [KnownHostEntry] {
        let query = text.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return entries }
        var names = [query]
        if let match = query.range(of: #"^\[?[^\[\]]+?\]?:[0-9]{1,5}$"#, options: .regularExpression), match == query.startIndex..<query.endIndex,
           let colon = query.lastIndex(of: ":"), let port = Int(query[query.index(after: colon)...]) {
            names.append(Self.hostName(String(query[..<colon]), port: port))
        }
        return entries.compactMap { entry in
            if entry.hashed {
                guard let name = names.first(where: entry.isFor) else { return nil }
                var shown = entry
                shown.revealed = name
                return shown
            }
            return entry.hosts.localizedCaseInsensitiveContains(query) || entry.keyType.localizedCaseInsensitiveContains(query) ? entry : nil
        }
    }

    func remove(_ chosen: Set<Int>) throws {
        try write(lines.enumerated().filter { !chosen.contains($0.offset) }.map(\.element))
    }

    /// Replaces the entries for `name` with the scanned key lines.
    func replace(name: String, keyLines: [String]) throws {
        let drop = Set(entries.filter { $0.marker.isEmpty && $0.isFor(name) }.map(\.line))
        try write(lines.enumerated().filter { !drop.contains($0.offset) }.map(\.element) + keyLines)
    }

    private func write(_ next: [String]) throws {
        let manager = FileManager.default
        try manager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true,
                                    attributes: [.posixPermissions: 0o700])
        if manager.fileExists(atPath: url.path) {
            let old = url.deletingLastPathComponent().appendingPathComponent(url.lastPathComponent + ".old")
            try? manager.removeItem(at: old)
            try manager.copyItem(at: url, to: old)
        }
        try Data(next.map { $0 + "\n" }.joined().utf8).write(to: url, options: .atomic)
        try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    /// `ssh-keyscan -T 10`: the key lines for host:port with the name ssh would write.
    static func scan(host: String, port: Int) throws -> [String] {
        let bare = host.trimmingCharacters(in: CharacterSet(charactersIn: "[] "))
        guard bare.range(of: #"^[A-Za-z0-9_.:-]+$"#, options: .regularExpression) != nil, !bare.hasPrefix("-") else {
            throw ManagerError("Укажите DNS-имя, IPv4 или IPv6 без пробелов.")
        }
        guard (1...65535).contains(port) else { throw ManagerError("Порт должен быть числом от 1 до 65535.") }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ssh-keyscan")
        process.arguments = ["-T", "10", "-p", String(port), bare]
        let output = Pipe(), errors = Pipe()
        process.standardOutput = output
        process.standardError = errors
        process.standardInput = FileHandle.nullDevice
        try process.run()
        let stdout = output.fileHandleForReading.readDataToEndOfFile()
        let stderr = errors.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let keys = String(decoding: stdout, as: UTF8.self).components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("#") && KnownHostEntry.parse($0) != nil }
        guard !keys.isEmpty else {
            let last = String(decoding: stderr, as: UTF8.self).components(separatedBy: .newlines)
                .map { $0.trimmingCharacters(in: .whitespaces) }.last { !$0.isEmpty && !$0.hasPrefix("#") }
            throw ManagerError("Сервер не прислал ключи." + (last.map { " " + $0 } ?? ""))
        }
        return keys
    }
}

/// What «Известные хосты» opens with: the files of the profiles, the first file and the search.
struct KnownHostsRequest: Identifiable {
    struct Choice: Hashable {
        let path: String
        let title: String
    }
    let id = UUID()
    var files: [Choice]
    var selected: String
    var query: String
}

/// «Известные хосты» (docs/ui-spec.md §9.2).
struct KnownHostsView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var files: [KnownHostsRequest.Choice]
    @State private var path: String
    @State private var query: String
    @State private var file: KnownHostsFile?
    @State private var selection = Set<Int>()
    @State private var status = ""
    @State private var busy = false
    @State private var confirmingRemove = false
    @State private var adding = false
    @State private var addHost = ""
    @State private var addPort = "22"
    @State private var pending: (name: String, keys: [String])?

    init(request: KnownHostsRequest) {
        _files = State(initialValue: request.files)
        _path = State(initialValue: request.selected)
        _query = State(initialValue: request.query)
    }

    private var shown: [KnownHostEntry] { file?.search(query) ?? [] }

    private var pendingSummary: String {
        guard let pending else { return "" }
        let entries: [KnownHostEntry] = pending.keys.compactMap { KnownHostEntry.parse($0) }
        let keys: [String] = entries.map { entry in entry.keyType + " · " + entry.fingerprint }
        return "Прежние записи этого хоста будут заменены.\n\n" + pending.name + "\n" + keys.joined(separator: "\n")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Известные хосты").font(.title3.weight(.semibold))
            HStack(spacing: 8) {
                Text("Файл")
                Picker("", selection: $path) {
                    ForEach(files, id: \.self) { Text($0.title).tag($0.path) }
                }.labelsHidden()
                Button { chooseFile() } label: { GlyphLabel(title: "Другой файл…", glyph: .browse) }.winButton(.outline)
                Button { openInEditor() } label: { GlyphLabel(title: "Открыть в редакторе", glyph: .edit, kind: .quiet) }.winButton(.quiet)
            }
            HStack(spacing: 6) {
                TextField("Найти хост", text: $query).textFieldStyle(.roundedBorder).frame(width: 265)
                Spacer()
                Button { addHost = query; adding = true } label: { GlyphLabel(title: "Добавить…", glyph: .plus, kind: .quiet) }
                    .winButton(.quiet).disabled(busy)
                Button { load() } label: { GlyphLabel(title: "Обновить", glyph: .refresh, kind: .quiet) }.winButton(.quiet).disabled(busy)
                Button { confirmingRemove = true } label: { GlyphLabel(title: "Удалить", glyph: .delete, kind: .quiet, danger: true) }
                    .winButton(.quiet, danger: true).disabled(busy || selection.isEmpty)
            }
            Table(shown, selection: $selection) {
                TableColumn("Хост") { Text($0.display).lineLimit(1) }
                TableColumn("Тип ключа") { Text($0.keyType).lineLimit(1) }.width(min: 110, ideal: 160)
                TableColumn("Отпечаток") { Text($0.fingerprint).lineLimit(1).truncationMode(.middle).textSelection(.enabled) }
                    .width(min: 160, ideal: 300)
                TableColumn("Метка") { Text($0.marker) }.width(min: 60, ideal: 110)
            }
            .onDeleteCommand { if !selection.isEmpty { confirmingRemove = true } }
            HStack {
                Text(status).font(.system(size: 12)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                Spacer()
                Button("Закрыть") { dismiss() }.keyboardShortcut(.cancelAction).winButton(.outline)
            }
        }
        .padding(20)
        .frame(minWidth: 820, minHeight: 500)
        .onAppear { load() }
        .onChange(of: path) { _, _ in selection = []; load() }
        .onChange(of: query) { _, _ in updateStatus() }
        .alert("Удалить записи (\(selection.count)) из known_hosts?", isPresented: $confirmingRemove) {
            Button("Нет", role: .cancel) {}
            Button("Да", role: .destructive) { removeSelected() }
        } message: { Text(path) }
        .alert("Добавить ключи хоста в known_hosts?", isPresented: Binding(get: { pending != nil }, set: { if !$0 { pending = nil } })) {
            Button("Нет", role: .cancel) { status = "Ключи не добавлены." }
            Button("Да") { addPending() }
        } message: {
            Text(pendingSummary)
        }
        .sheet(isPresented: $adding) { hostKeySheet }
    }

    /// «Добавить ключ хоста»: host and port for ssh-keyscan.
    private var hostKeySheet: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Добавить ключ хоста").font(.title3.weight(.semibold))
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                GridRow { Text("Хост"); TextField("server.example.com или 10.0.0.5", text: $addHost) }
                GridRow { Text("Порт"); TextField("22", text: $addPort) }
            }.textFieldStyle(.roundedBorder)
            HStack {
                Spacer()
                Button("Отмена") { adding = false }.keyboardShortcut(.cancelAction).winButton(.outline)
                Button("Получить ключи") { adding = false; scan() }.keyboardShortcut(.defaultAction).winButton(.primary)
            }
        }
        .padding(20).frame(width: 440)
        .onAppear {
            if let match = addHost.range(of: #"^\[[^\]]+\]:[0-9]{1,5}$"#, options: .regularExpression), match.lowerBound == addHost.startIndex,
               let colon = addHost.lastIndex(of: ":") {
                addPort = String(addHost[addHost.index(after: colon)...])
                addHost = String(addHost[addHost.index(after: addHost.startIndex)..<addHost.index(before: colon)])
            }
        }
    }

    private func load() {
        do { file = try KnownHostsFile(url: URL(fileURLWithPath: path)) } catch {
            file = nil
            status = "Не удалось прочитать файл. " + error.localizedDescription
            return
        }
        updateStatus()
    }

    private func updateStatus() {
        guard let file else { return }
        let total = file.entries.count
        if total == 0 {
            status = FileManager.default.fileExists(atPath: path) ? "Записей нет." : "Файла пока нет: он появится после первой записи."
        } else {
            status = query.trimmingCharacters(in: .whitespaces).isEmpty ? "Записей: \(total)" : "Найдено: \(shown.count) из \(total)"
        }
    }

    private func removeSelected() {
        guard let file, !selection.isEmpty else { return }
        let count = selection.count
        do {
            try file.remove(selection)
            selection = []
            load()
            status = "Удалено записей: \(count). Прежняя версия: \(URL(fileURLWithPath: path).lastPathComponent).old"
        } catch { status = error.localizedDescription }
    }

    private func scan() {
        let host = addHost.trimmingCharacters(in: CharacterSet(charactersIn: "[] "))
        guard !host.isEmpty else { status = "Укажите хост."; return }
        guard let port = Int(addPort.trimmingCharacters(in: .whitespaces)), (1...65535).contains(port) else {
            status = "Порт должен быть числом от 1 до 65535."
            return
        }
        let name = KnownHostsFile.hostName(host, port: port)
        busy = true
        status = "Получение ключей \(name)…"
        Task {
            let result = await Task.detached(priority: .userInitiated) { Result { try KnownHostsFile.scan(host: host, port: port) } }.value
            busy = false
            switch result {
            case .success(let keys): pending = (name, keys)
            case .failure(let error): status = "Не удалось получить ключи. " + error.localizedDescription
            }
        }
    }

    private func addPending() {
        guard let file, let pending else { return }
        do {
            try file.replace(name: pending.name, keyLines: pending.keys)
            load()
            query = pending.name
            status = "Добавлено ключей: \(pending.keys.count)."
        } catch { status = error.localizedDescription }
        self.pending = nil
    }

    private func chooseFile() {
        let panel = NSOpenPanel()
        panel.title = "Файл known_hosts"
        panel.canChooseDirectories = false
        panel.showsHiddenFiles = true
        panel.directoryURL = URL(fileURLWithPath: path).deletingLastPathComponent()
        guard panel.runModal() == .OK, let url = panel.url else { return }
        if !files.contains(where: { $0.path == url.path }) { files.append(.init(path: url.path, title: url.path)) }
        path = url.path
    }

    private func openInEditor() {
        guard FileManager.default.fileExists(atPath: path) else { status = "Файла пока нет: он появится после первой записи."; return }
        NSWorkspace.shared.open(URL(fileURLWithPath: path))
    }
}
