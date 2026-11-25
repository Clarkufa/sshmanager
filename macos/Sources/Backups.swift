import CryptoKit
import Foundation

private let configEntry = "ssh/config"
private let settingsEntry = "app/tabs.xml"
private let keyPrefix = "ssh/keys/"
private let includePrefix = "ssh/includes/"
private let passwordPrefix = "app/passwords/"
private let restoreFolder = ".ssh-manager-restore"

private let reservedSSH = Set(["config", "config.bak", "authorized_keys", "authorized_keys2", "known_hosts",
                               "known_hosts.old", "rc", "environment", restoreFolder, ".ssh-manager.lock"])

private func sshAssetPath(_ path: String) -> Bool {
    BackupArchive.validPath(path) && !reservedSSH.contains(String(path.split(separator: "/")[0]).lowercased())
}

private func digest(_ text: String) -> String {
    SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
}

private func backupPasswordPath(_ profile: Profile) -> String { passwordPrefix + digest(profile.account) }
/// RDP passwords: app/passwords/rdp-<profile id>, the same naming as the Windows and Linux archives.
private func backupPasswordPath(_ profile: DesktopProfile) -> String { passwordPrefix + "rdp-" + profile.id }

private func regularPath(_ url: URL, allowMissing: Bool) throws {
    var current = url.standardizedFileURL
    while current.path != "/" {
        let item = try LocalFiles.item(current)
        let isLeaf = current == url.standardizedFileURL
        if let item {
            guard item.kind == (isLeaf ? .file : .directory) else {
                throw ManagerError("Путь «\(current.path)» содержит ссылку или несовместимый тип файла.")
            }
        } else if !allowMissing {
            throw ManagerError("Файл «\(current.path)» отсутствует.")
        }
        current.deleteLastPathComponent()
    }
}

private func configText(_ data: Data) throws -> String {
    guard var text = String(data: data, encoding: .utf8), !text.contains("\0") else { throw BackupArchive.invalid() }
    if text.hasPrefix("\u{FEFF}") { text.removeFirst() }
    return text
}

final class BackupSnapshot {
    private(set) var entries: [String: Data] = [:]
    private(set) var omissions: Set<String> = []
    private let config: ConfigStore
    private let settings: ManagerSettings
    private let ssh: URL
    private let home: URL
    private var collected: [String: String] = [:]
    private var entryNames: Set<String> = []
    private var total = 0

    init(config: ConfigStore, settings: ManagerSettings) {
        self.config = config
        self.settings = settings
        ssh = config.url.deletingLastPathComponent().standardizedFileURL
        home = ssh.deletingLastPathComponent()
    }

    func collect(passwords: Bool) throws {
        try add(settingsEntry, data: settings.data)
        if FileManager.default.fileExists(atPath: config.url.path) {
            let resolved = config.url.resolvingSymlinksInPath()
            try add(configEntry, data: rewrite(resolved, depth: 0))
        } else {
            try add(configEntry, data: Data())
        }

        // Include keys that OpenSSH finds by default and keys selected from ~/.ssh later.
        if let files = FileManager.default.enumerator(at: ssh, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey]) {
            for case let url as URL in files {
                guard let path = relativePath(url),
                      !path.split(separator: "/").contains(where: { $0 == ".git" }) else {
                    files.skipDescendants()
                    continue
                }
                let values = try url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
                if values.isSymbolicLink == true { files.skipDescendants() }
                else if values.isDirectory != true { _ = try key(url) }
            }
        }
        if passwords {
            for profile in try config.read().profiles where profile.passwordHost != nil {
                if let password = try Passwords.read(profile.account) { try add(backupPasswordPath(profile), data: Data(password.utf8)) }
            }
            for profile in settings.desktops {
                if let password = try Passwords.read(profile.keychainAccount) {
                    try add(backupPasswordPath(profile), data: Data(password.utf8))
                }
            }
        }
    }

    private func rewrite(_ url: URL, depth: Int) throws -> Data {
        guard depth <= 16 else { throw ManagerError("Слишком много уровней вложенных файлов Include.") }
        try regularPath(url, allowMissing: false)
        let text = try configText(BackupArchive.readFile(url, limit: BackupArchive.maxFile))
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n")
        let lines = try normalized.components(separatedBy: "\n").map { line -> String in
            let (directive, values) = ConfigDocument.statement(line)
            if (directive == "identityfile" || directive == "certificatefile"), values.count == 1 {
                let value = values[0]
                guard value.lowercased() != "none" else { return line }
                guard let source = resolve(value, base: home), let saved = try key(source) else {
                    omissions.insert(value)
                    return line
                }
                let tag = directive == "identityfile" ? "IdentityFile" : "CertificateFile"
                return "    " + tag + " " + portable(saved) + ConfigDocument.comment(line)
            }
            if directive == "include", !values.isEmpty {
                var arguments: [String] = []
                for value in values {
                    let matches = includeMatches(value)
                    var replaced = false
                    for source in matches {
                        if let relative = try include(source, depth: depth + 1) {
                            arguments.append(portable(relative))
                            replaced = true
                        }
                    }
                    if !replaced {
                        omissions.insert(value)
                        arguments.append(ConfigDocument.quote(value))
                    }
                }
                return "    Include " + arguments.joined(separator: " ") + ConfigDocument.comment(line)
            }
            return line
        }
        return Data(lines.joined(separator: "\n").utf8)
    }

    private func key(_ source: URL) throws -> String? {
        let url = source.standardizedFileURL
        if let relative = collected["key:" + url.path] { return relative }
        guard (try? regularPath(url, allowMissing: false)) != nil, let relative = relativePath(url) else { return nil }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        guard let prefix = try handle.read(upToCount: 150),
              let beginning = String(data: prefix, encoding: .utf8) else { return nil }
        let privateKey = beginning.hasPrefix("-----BEGIN ") && (beginning.components(separatedBy: .newlines).first?.contains("PRIVATE KEY-----") == true)
        let publicKey = beginning.hasPrefix("ssh-") || beginning.hasPrefix("ecdsa-") || beginning.hasPrefix("sk-") ||
                        beginning.hasPrefix("---- BEGIN SSH2 PUBLIC KEY ----") || beginning.hasPrefix("-----BEGIN PUBLIC KEY-----")
        guard privateKey || publicKey else { return nil }
        try add(keyPrefix + relative, data: BackupArchive.readFile(url, limit: BackupArchive.maxFile))
        collected["key:" + url.path] = relative
        if privateKey {
            for suffix in [".pub", "-cert.pub"] { _ = try key(URL(fileURLWithPath: url.path + suffix)) }
        }
        return relative
    }

    private func include(_ source: URL, depth: Int) throws -> String? {
        let url = source.standardizedFileURL
        if let relative = collected["include:" + url.path] { return relative }
        guard (try? regularPath(url, allowMissing: false)) != nil, let relative = relativePath(url) else { return nil }
        collected["include:" + url.path] = relative
        try add(includePrefix + relative, data: rewrite(url, depth: depth))
        return relative
    }

    private func includeMatches(_ value: String) -> [URL] {
        guard let location = resolve(value, base: ssh) else { return [] }
        guard value.rangeOfCharacter(from: CharacterSet(charactersIn: "*?[")) != nil else { return [location] }
        var matches = glob_t()
        guard glob(location.path, GLOB_LIMIT, nil, &matches) == 0 else { globfree(&matches); return [] }
        defer { globfree(&matches) }
        guard let paths = matches.gl_pathv, matches.gl_pathc <= BackupArchive.maxEntries else { return [] }
        return (0..<Int(matches.gl_pathc)).compactMap { index in
            guard let path = paths[index], let text = String(validatingUTF8: path) else { return nil }
            return URL(fileURLWithPath: text)
        }
    }

    private func resolve(_ value: String, base: URL) -> URL? {
        let expanded = value.replacingOccurrences(of: "%d", with: home.path).replacingOccurrences(of: "%%", with: "\u{1}")
        guard !expanded.contains("%"), !expanded.contains("${"), !expanded.contains("\0") else { return nil }
        let path = expanded.replacingOccurrences(of: "\u{1}", with: "%")
        if path.hasPrefix("~/") { return home.appendingPathComponent(String(path.dropFirst(2))) }
        if path.hasPrefix("~") { return nil }
        return path.hasPrefix("/") ? URL(fileURLWithPath: path) : base.appendingPathComponent(path)
    }

    private func relativePath(_ url: URL) -> String? {
        let relative = url.path.hasPrefix(ssh.path + "/") ? String(url.path.dropFirst(ssh.path.count + 1))
            : "ssh-manager-files/" + digest(url.deletingLastPathComponent().path).prefix(24) + "/" + url.lastPathComponent
        return sshAssetPath(relative) ? relative : nil
    }

    private func portable(_ path: String) -> String {
        ConfigDocument.quote("~/.ssh/" + path.replacingOccurrences(of: "%", with: "%%"))
    }

    private func add(_ path: String, data: Data) throws {
        guard entryNames.insert(path.precomposedStringWithCanonicalMapping.lowercased()).inserted else { throw BackupArchive.invalid() }
        total += data.count
        guard data.count <= BackupArchive.maxFile, total <= BackupArchive.maxTotal, entries.count < BackupArchive.maxEntries else {
            throw BackupArchive.tooLarge()
        }
        entries[path] = data
    }
}

struct BackupRestore {
    private struct Change {
        let destination: URL
        let recovery: URL
        let data: Data
        let before: Data?
    }

    private let config: ConfigStore
    private var changes: [Change] = []
    /// Keychain account and the password to store there, for SSH and RDP profiles alike.
    private var passwords: [(account: String, secret: String, desktop: Bool)] = []
    var fileCount: Int { changes.count }
    var replaced: [String] { changes.filter { $0.before != nil }.map { $0.destination.path } }
    var passwordCount: Int { passwords.count }
    var desktopPasswordCount: Int { passwords.filter { $0.desktop }.count }
    let recoveryURL: URL

    init(entries: [String: Data], config: ConfigStore, settingsURL: URL = ManagerSettings.url) throws {
        self.config = config
        let entries = Dictionary(uniqueKeysWithValues: entries.map { key, value in
            let canonical = key.lowercased()
            // Preserve the casing of portable key and include filenames.
            return (canonical.hasPrefix(keyPrefix) || canonical.hasPrefix(includePrefix) ? key : canonical, value)
        })
        guard let configData = entries[configEntry], let settingsData = entries[settingsEntry] else { throw BackupArchive.invalid() }
        let profiles = try ConfigDocument(configText(configData)).profiles
        let desktops = try ManagerSettings.decode(settingsData).desktops
        let folder = config.url.deletingLastPathComponent()
        let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "") + "-" + UUID().uuidString.prefix(8)
        recoveryURL = folder.appendingPathComponent(restoreFolder).appendingPathComponent(stamp)
        var paths: Set<String> = []
        for (name, data) in entries.sorted(by: { $0.key < $1.key }) {
            if name == configEntry || name == settingsEntry { continue }
            if name.hasPrefix(passwordPrefix) {
                let ssh = profiles.first(where: { backupPasswordPath($0) == name })
                let desktop = desktops.first(where: { backupPasswordPath($0) == name })
                guard let account = ssh?.account ?? desktop?.keychainAccount,
                      (1...1022).contains(data.count), let secret = String(data: data, encoding: .utf8),
                      !secret.contains("\0"), !secret.contains(where: \.isNewline) else {
                    throw ManagerError("В копии найден неподдерживаемый пароль подключения.")
                }
                passwords.append((account, secret, ssh == nil))
                continue
            }
            let key = name.lowercased()
            guard key.hasPrefix(keyPrefix) || key.hasPrefix(includePrefix) else { throw BackupArchive.invalid() }
            let isKey = key.hasPrefix(keyPrefix)
            let relative = String(name.dropFirst(isKey ? keyPrefix.count : includePrefix.count))
            guard sshAssetPath(relative) else { throw BackupArchive.invalid() }
            if !isKey { _ = try configText(data) }
            let alias = relative.precomposedStringWithCanonicalMapping.lowercased()
            guard paths.insert(alias).inserted else { throw BackupArchive.invalid() }
            try add(folder.appendingPathComponent(relative), recovery: recoveryURL.appendingPathComponent(relative), data: data)
        }
        for path in paths {
            let components = path.split(separator: "/")
            for count in 1..<components.count where paths.contains(components.prefix(count).joined(separator: "/")) {
                throw BackupArchive.invalid()
            }
        }
        try add(config.url, recovery: recoveryURL.appendingPathComponent("config"), data: configData)
        try add(settingsURL, recovery: recoveryURL.appendingPathComponent("app/tabs.xml"), data: settingsData)
    }

    func apply() throws {
        try config.withLock {
            for item in changes {
                try regularPath(item.destination, allowMissing: true)
                guard try current(item.destination) == item.before else {
                    throw ManagerError("Файл «\(item.destination.lastPathComponent)» изменился после открытия архива.")
                }
            }
            var written: [Change] = []
            var keychain: [(String, String?)] = []
            do {
                for item in changes where item.before != nil {
                    if let previous = item.before {
                        try createParent(item.recovery)
                        try regularPath(item.recovery, allowMissing: true)
                        try writePrivate(previous, to: item.recovery)
                    }
                }
                for item in changes {
                    try createParent(item.destination)
                    try regularPath(item.destination, allowMissing: true)
                    try writePrivate(item.data, to: item.destination)
                    written.append(item)
                }
                for (account, password, _) in passwords {
                    let before = try Passwords.read(account)
                    try Passwords.set(password, for: account)
                    keychain.append((account, before))
                }
            } catch {
                var failed = false
                for (account, password) in keychain.reversed() {
                    do { try Passwords.set(password, for: account) } catch { failed = true }
                }
                for item in written.reversed() {
                    do {
                        if let data = item.before { try writePrivate(data, to: item.destination) }
                        else { try FileManager.default.removeItem(at: item.destination) }
                    } catch { failed = true }
                }
                if failed {
                    throw ManagerError("Восстановление прервано, часть изменений не удалось отменить. Проверьте файлы в «\(recoveryURL.path)».")
                }
                throw error
            }
        }
    }

    private mutating func add(_ destination: URL, recovery: URL, data: Data) throws {
        try regularPath(destination, allowMissing: true)
        changes.append(Change(destination: destination, recovery: recovery, data: data, before: try current(destination)))
    }

    private func current(_ url: URL) throws -> Data? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return try BackupArchive.readFile(url, limit: BackupArchive.maxFile)
    }

    private func createParent(_ url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
    }
}
