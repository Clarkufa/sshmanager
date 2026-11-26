import AppKit
import SwiftUI

// Stage 2 helpers shared by the Manager and the views (docs/ui-spec.md §7).

struct QuickTarget {
    let kind: ConnectionProtocol
    let user: String
    let host: String
    let port: Int

    /// «user@host:port» or a target with an ssh://, rdp:// or vnc:// scheme.
    init(_ input: String) throws {
        var text = input.trimmingCharacters(in: .whitespaces)
        var kind: ConnectionProtocol = .ssh
        for protocolKind in ConnectionProtocol.allCases {
            let prefix = protocolKind.rawValue.lowercased() + "://"
            guard text.lowercased().hasPrefix(prefix) else { continue }
            kind = protocolKind
            text = String(text.dropFirst(prefix.count))
            while text.hasSuffix("/") { text.removeLast() }
            break
        }
        let pattern = #"^(?:([^@\s:]+)@)?(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9_.-]+)(?::([0-9]{1,5}))?$"#
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else {
            throw ManagerError("Введите адрес как user@host:port, rdp://host:port или vnc://host:port.")
        }
        func group(_ index: Int) -> String? {
            Range(match.range(at: index), in: text).map { String(text[$0]) }
        }
        let port = group(3).flatMap { Int($0) } ?? kind.defaultPort
        guard (1...65535).contains(port) else { throw ManagerError("Порт должен быть числом от 1 до 65535.") }
        self.kind = kind
        user = group(1) ?? ""
        host = (group(2) ?? "").trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        self.port = port
    }
}

func quickAlias(_ host: String, existing: [String]) -> String {
    let base = host.replacingOccurrences(of: #"[^A-Za-z0-9_.-]"#, with: "-", options: .regularExpression)
        .trimmingCharacters(in: CharacterSet(charactersIn: "-."))
    let start = base.isEmpty ? "server" : base
    let taken = Set(existing.map { $0.lowercased() })
    var alias = start
    var number = 2
    while taken.contains(alias.lowercased()) { alias = "\(start)-\(number)"; number += 1 }
    return alias
}

struct FoundConnection: Identifiable {
    let id = UUID()
    let kind: ConnectionProtocol
    let name: String
    let host: String
    let port: Int
    var user = ""
    var domain = ""
    let source: String
    var chosen = true
}

enum Importers {
    static func putty() -> [FoundConnection] {
        let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".putty/sessions")
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: folder.path) else { return [] }
        return names.sorted().compactMap { name in
            guard let text = try? String(contentsOf: folder.appendingPathComponent(name), encoding: .utf8) else { return nil }
            var values: [String: String] = [:]
            for line in text.components(separatedBy: .newlines) {
                let parts = line.split(separator: "=", maxSplits: 1).map(String.init)
                if parts.count == 2 { values[parts[0].trimmingCharacters(in: .whitespaces)] = parts[1].trimmingCharacters(in: .whitespaces) }
            }
            guard values["Protocol", default: "ssh"] == "ssh", var host = values["HostName"], !host.isEmpty else { return nil }
            var user = values["UserName"] ?? ""
            if user.isEmpty, let at = host.firstIndex(of: "@") { user = String(host[..<at]); host = String(host[host.index(after: at)...]) }
            return FoundConnection(kind: .ssh, name: name.removingPercentEncoding ?? name, host: host,
                                   port: Int(values["PortNumber"] ?? "") ?? 22, user: user, source: "PuTTY")
        }
    }

    static func read(_ url: URL) throws -> [FoundConnection] {
        let data = try readRegularFile(url, limit: 16 * 1024 * 1024)
        if url.pathExtension.lowercased() == "xml" { return try mRemoteNG(data) }
        return mobaXterm(String(decoding: data, as: UTF8.self))
    }

    /// confCons.xml; its passwords are encrypted and are not imported.
    static func mRemoteNG(_ data: Data) throws -> [FoundConnection] {
        let document = try XMLDocument(data: data, options: [.nodeLoadExternalEntitiesNever])
        let nodes = (try? document.nodes(forXPath: "//Node[@Type='Connection']")) ?? []
        return nodes.compactMap { node in
            guard let element = node as? XMLElement else { return nil }
            func value(_ name: String) -> String { element.attribute(forName: name)?.stringValue ?? "" }
            let kind: ConnectionProtocol
            switch value("Protocol").uppercased() {
            case "SSH1", "SSH2": kind = .ssh
            case "RDP": kind = .rdp
            case "VNC": kind = .vnc
            default: return nil
            }
            let host = value("Hostname")
            guard !host.isEmpty else { return nil }
            return FoundConnection(kind: kind, name: value("Name").isEmpty ? host : value("Name"), host: host,
                                   port: Int(value("Port")) ?? kind.defaultPort, user: value("Username"),
                                   domain: kind == .rdp ? value("Domain") : "", source: "mRemoteNG")
        }
    }

    /// .mxtsessions / MobaXterm.ini: Name=#icon#type%host%port%user%…
    static func mobaXterm(_ text: String) -> [FoundConnection] {
        text.components(separatedBy: .newlines).compactMap { line in
            guard let equals = line.firstIndex(of: "="), !line.hasPrefix("["), !line.hasPrefix("SubRep"), !line.hasPrefix("ImgNum")
            else { return nil }
            let name = String(line[..<equals]).trimmingCharacters(in: .whitespaces)
            let fields = String(line[line.index(after: equals)...]).components(separatedBy: "%")
            guard fields.count >= 4 else { return nil }
            guard !fields[1].isEmpty else { return nil }
            let kind: ConnectionProtocol
            switch fields[0].split(separator: "#").last.map(String.init) ?? "" {
            case "0": kind = .ssh
            case "4": kind = .rdp
            case "5": kind = .vnc
            default: return nil
            }
            return FoundConnection(kind: kind, name: name, host: fields[1], port: Int(fields[2]) ?? kind.defaultPort,
                                   user: fields[3], source: "MobaXterm")
        }
    }
}

enum Keys {
    /// ssh-keygen -t ed25519 without a passphrase; returns the private key path.
    static func generate(name: String) throws -> String {
        guard !name.isEmpty, !name.contains("/"), !name.hasPrefix(".") else { throw ManagerError("Укажите имя файла без папок.") }
        let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ssh")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let path = folder.appendingPathComponent(name).path
        guard !FileManager.default.fileExists(atPath: path) else { throw ManagerError("Файл \(path) уже существует.") }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ssh-keygen")
        process.arguments = ["-q", "-t", "ed25519", "-N", "", "-C", NSUserName() + "@" + (Host.current().localizedName ?? "mac"), "-f", path]
        let errors = Pipe()
        process.standardError = errors
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            throw ManagerError(String(decoding: errors.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self))
        }
        return path.replacingOccurrences(of: NSHomeDirectory(), with: "~")
    }

    /// A remote shell command that appends the public key once to ~/.ssh/authorized_keys.
    static func copyCommand(_ publicKey: String) throws -> String {
        let key = publicKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard key.range(of: #"^(ssh|ecdsa|sk)-[A-Za-z0-9@.-]+ [A-Za-z0-9+/=]+( [^'\r\n]*)?$"#, options: .regularExpression) != nil
        else { throw ManagerError("Файл не похож на публичный ключ OpenSSH.") }
        let quoted = "'" + key + "'"
        return "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
            "(grep -qxF \(quoted) ~/.ssh/authorized_keys || echo \(quoted) >> ~/.ssh/authorized_keys) && " +
            "echo 'Ключ добавлен в ~/.ssh/authorized_keys.'"
    }
}

// MARK: - Sheets

struct SettingsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @State private var preferences: Preferences
    @State private var failure: String?
    let save: (Preferences) throws -> Void
    var openKnownHosts: (() -> Void)?

    init(preferences: Preferences, openKnownHosts: (() -> Void)? = nil, save: @escaping (Preferences) throws -> Void) {
        _preferences = State(initialValue: preferences)
        self.openKnownHosts = openKnownHosts
        self.save = save
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Настройки").font(.title3.weight(.semibold))
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                GridRow {
                    Text("Шрифт терминала")
                    Picker("", selection: $preferences.terminalFont) {
                        Text("Системный моноширинный").tag("")
                        ForEach(monospaceFamilies, id: \.self) { Text($0).tag($0) }
                    }.labelsHidden()
                }
                GridRow { Text("Размер шрифта"); Stepper("\(preferences.terminalSize)", value: $preferences.terminalSize, in: 6...48) }
                GridRow { Text("Строк прокрутки"); Stepper("\(preferences.scrollback)", value: $preferences.scrollback, in: 100...100_000, step: 500) }
                GridRow { Text("Пользователь по умолчанию"); TextField("", text: $preferences.defaultUser) }
                GridRow { Text("Порт по умолчанию"); TextField("", text: $preferences.defaultPort) }
                GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Переподключать SSH автоматически", isOn: $preferences.autoReconnect) }
                GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Оставлять работать в строке меню при закрытии окна", isOn: $preferences.closeToTray) }
                GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Проверять обновления при запуске", isOn: $preferences.checkUpdates) }
                if let openKnownHosts {
                    GridRow {
                        Color.clear.frame(width: 1, height: 1)
                        Button { dismiss(); openKnownHosts() } label: { GlyphLabel(title: "Известные хосты…", glyph: .key) }.winButton(.outline)
                    }
                }
            }.textFieldStyle(.roundedBorder)
            if let failure { Text(failure).foregroundStyle(.red).font(.callout) }
            HStack {
                Spacer()
                Button("Отмена") { dismiss() }.keyboardShortcut(.cancelAction).winButton(.outline)
                Button("Сохранить") {
                    let port = preferences.defaultPort.trimmingCharacters(in: .whitespaces)
                    guard port.isEmpty || (Int(port).map { (1...65535).contains($0) } ?? false) else {
                        failure = "Порт должен быть числом от 1 до 65535."
                        return
                    }
                    preferences.defaultPort = port
                    do { try save(preferences); dismiss() } catch { failure = error.localizedDescription }
                }.keyboardShortcut(.defaultAction).winButton(.primary)
            }
        }.padding(20).frame(width: 560)
    }

    private var monospaceFamilies: [String] {
        NSFontManager.shared.availableFontFamilies.filter { family in
            NSFont(name: family, size: 12)?.isFixedPitch ?? false
        }
    }
}

struct ImportSheet: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme
    @State private var found: [FoundConnection] = []
    @State private var status = "Выберите источник. Пароли из других программ не переносятся."
    let importChosen: ([FoundConnection]) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Импорт из других программ").font(.title3.weight(.semibold))
            HStack(spacing: 6) {
                Button { show(Importers.putty()) } label: { GlyphLabel(title: "PuTTY", glyph: .terminal) }.winButton(.outline)
                Button { chooseFile() } label: { GlyphLabel(title: "Файл mRemoteNG или MobaXterm…", glyph: .folder) }.winButton(.outline)
            }
            List($found) { $item in
                Toggle(isOn: $item.chosen) {
                    HStack {
                        Text(item.kind.rawValue).frame(width: 40, alignment: .leading)
                        Text(item.name).frame(width: 160, alignment: .leading).lineLimit(1)
                        Text((item.user.isEmpty ? "" : item.user + "@") + item.host + ":\(item.port)").lineLimit(1)
                        Spacer()
                        Text(item.source).foregroundStyle(Palette.of(scheme).muted)
                    }
                }
            }.frame(height: 260).listStyle(.bordered)
            Text(status).font(.callout).foregroundStyle(Palette.of(scheme).muted)
            HStack {
                Spacer()
                Button("Отмена") { dismiss() }.keyboardShortcut(.cancelAction).winButton(.outline)
                Button("Импортировать") { let chosen = found.filter(\.chosen); dismiss(); importChosen(chosen) }
                    .keyboardShortcut(.defaultAction).winButton(.primary).disabled(!found.contains(where: \.chosen))
            }
        }.padding(20).frame(width: 640)
    }

    private func show(_ items: [FoundConnection]) {
        found = items
        status = items.isEmpty ? "Подключения не найдены." : "Найдено подключений: \(items.count)"
    }

    private func chooseFile() {
        let panel = NSOpenPanel()
        panel.allowsOtherFileTypes = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { show(try Importers.read(url)) } catch { status = "Не удалось прочитать файл. " + error.localizedDescription }
    }
}

struct SnippetSheet: View {
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var command = ""
    let save: (String, String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Новая команда").font(.title3.weight(.semibold))
            TextField("Название", text: $name).textFieldStyle(.roundedBorder)
            TextEditor(text: $command).font(.system(.body, design: .monospaced)).frame(height: 90)
                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(.quaternary).allowsHitTesting(false))
            HStack {
                Spacer()
                Button("Отмена") { dismiss() }.keyboardShortcut(.cancelAction).winButton(.outline)
                Button("Сохранить") { save(String(name.prefix(80)), String(command.prefix(4000))); dismiss() }
                    .keyboardShortcut(.defaultAction).winButton(.primary)
                    .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty || command.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }.padding(20).frame(width: 460)
    }
}
