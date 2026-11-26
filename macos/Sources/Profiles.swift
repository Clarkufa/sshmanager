import AppKit
import Foundation
import Security

struct ManagerError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
    init(_ message: String) { self.message = message }
}

struct Profile: Identifiable, Equatable {
    var alias = ""
    var host = ""
    var user = ""
    var port = ""
    var identities = ""
    var proxyJump = ""
    /// "LocalForward 8080 localhost:80", "RemoteForward …", "DynamicForward 1080"
    var forwards: [String] = []

    var id: String { alias.lowercased() }
    var address: String {
        let destination = host.isEmpty ? alias : host
        let endpoint = port.isEmpty || port == "22" ? destination : "\(destination.contains(":") ? "[\(destination)]" : destination):\(port)"
        return user.isEmpty ? endpoint : "\(user)@\(endpoint)"
    }
    var account: String { [id, host.isEmpty ? id : host.lowercased(), user, String(Int(port) ?? 22)].joined(separator: "\n") }

    static func validAlias(_ alias: String) -> Bool {
        alias.range(of: #"^[A-Za-z0-9_.][A-Za-z0-9_.-]*$"#, options: .regularExpression) != nil &&
            alias.unicodeScalars.allSatisfy { !$0.properties.isWhitespace }
    }

    var passwordHost: String? {
        let target = (host.isEmpty ? alias : host).lowercased()
        guard !user.isEmpty, user.utf8.count <= 255, target.utf8.count <= 253, !target.hasPrefix("-"),
              target.range(of: #"^[A-Za-z0-9_.:-]+$"#, options: .regularExpression) != nil,
              !user.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains),
              user.rangeOfCharacter(from: CharacterSet(charactersIn: "#'\"\\").union(.whitespacesAndNewlines)) == nil,
              port.isEmpty || (1...65535).contains(Int(port) ?? 0)
        else { return nil }
        return target
    }

    func validate(replacing original: Profile? = nil) throws {
        guard Self.validAlias(alias) else { throw ManagerError("Используйте в псевдониме латинские буквы, цифры, точки, дефисы и подчёркивания.") }
        let validHost = host == original?.host || host.isEmpty ||
            (host.range(of: #"^[A-Za-z0-9_.:%-]+$"#, options: .regularExpression) != nil && !host.contains(where: \.isWhitespace))
        let validUser = user == original?.user || (
            !user.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) &&
            user.rangeOfCharacter(from: CharacterSet(charactersIn: "#'\"\\").union(.whitespacesAndNewlines)) == nil
        )
        guard validHost, validUser,
              identities.components(separatedBy: .newlines).allSatisfy({ line in
                  !line.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains)
              })
        else { throw ManagerError("Проверьте адрес сервера, имя пользователя и пути к ключам.") }
        guard proxyJump == original?.proxyJump || proxyJump.isEmpty ||
                proxyJump.range(of: #"^[A-Za-z0-9_.@:%,\[\]-]+$"#, options: .regularExpression) != nil
        else { throw ManagerError("Укажите переходной сервер как псевдоним или user@host:port, несколько — через запятую.") }
        let endpoint = #"(?:[A-Za-z0-9_.*-]+:|\[[0-9A-Fa-f:.]+\]:)?[0-9]{1,5}"#
        let target = #"(?:[A-Za-z0-9_.-]+|\[[0-9A-Fa-f:.]+\]):[0-9]{1,5}"#
        for forward in forwards where !(original?.forwards.contains(forward) ?? false) {
            let parts = forward.split(separator: " ").map(String.init)
            let valid: Bool
            switch parts.first {
            case "DynamicForward": valid = parts.count == 2 && parts[1].range(of: "^" + endpoint + "$", options: .regularExpression) != nil
            case "LocalForward", "RemoteForward":
                valid = parts.count == 3 && parts[1].range(of: "^" + endpoint + "$", options: .regularExpression) != nil &&
                    parts[2].range(of: "^" + target + "$", options: .regularExpression) != nil
            default: valid = false
            }
            guard valid else { throw ManagerError("Проброс порта: укажите «порт хост:порт» (L, R) или «порт» (D).") }
        }
        guard port == original?.port || port.isEmpty ||
                (port.utf8.allSatisfy { (48...57).contains($0) } && (1...65535).contains(Int(port) ?? 0))
        else { throw ManagerError("Порт должен быть числом от 1 до 65535.") }
    }
}

struct ConfigDocument {
    private var lines: [String]
    private let lineEnding: String

    init(_ text: String) {
        lineEnding = text.contains("\r\n") ? "\r\n" : "\n"
        lines = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        if lines.last == "" { lines.removeLast() }
    }

    var text: String { lines.isEmpty ? "" : lines.joined(separator: lineEnding) + lineEnding }
    var profiles: [Profile] {
        var aliases: Set<String> = []
        return sections.map(\.profile).filter { aliases.insert($0.id).inserted }
    }

    private var sections: [(profile: Profile, lines: Range<Int>)] {
        var sections: [(Profile, Range<Int>)] = []
        let headers = lines.indices.filter {
            let keyword = Self.statement(lines[$0]).0
            return keyword == "host" || keyword == "match"
        }
        for (index, start) in headers.enumerated() {
            let (keyword, aliases) = Self.statement(lines[start])
            guard keyword == "host", aliases.count == 1, Profile.validAlias(aliases[0]) else { continue }
            let end = index + 1 < headers.count ? headers[index + 1] : lines.endIndex
            var profile = Profile(alias: aliases[0])
            var identities: [String] = []
            for line in lines[(start + 1)..<end] {
                let (key, arguments) = Self.statement(line)
                guard let value = arguments.first else { continue }
                if let name = Self.forwardNames[key] {
                    profile.forwards.append(([name] + arguments).joined(separator: " "))
                    continue
                }
                switch key {
                case "proxyjump" where profile.proxyJump.isEmpty: profile.proxyJump = value
                case "hostname" where profile.host.isEmpty: profile.host = value
                case "user" where profile.user.isEmpty: profile.user = value
                case "port" where profile.port.isEmpty: profile.port = value
                case "identityfile": identities.append(value)
                default: break
                }
            }
            profile.identities = identities.joined(separator: "\n")
            sections.append((profile, start..<end))
        }
        return sections
    }

    mutating func update(_ profile: Profile?, replacing original: Profile?) throws {
        if let profile {
            try profile.validate(replacing: original)
            if profile.id != original?.id && profiles.contains(where: { $0.id == profile.id }) {
                throw ManagerError("Подключение с таким псевдонимом уже существует.")
            }
        }
        if let original {
            let matches = sections.filter { $0.profile.id == original.id }
            guard matches.count == 1, let section = matches.first, section.profile == original
            else { throw ManagerError("Секция изменилась или повторяется в OpenSSH config. Обновите список и проверьте файл.") }
            var range = section.lines
            while range.count > 1 && Self.statement(lines[range.upperBound - 1]).0.isEmpty {
                range = range.lowerBound..<(range.upperBound - 1)
            }
            guard let profile else { lines.removeSubrange(range); return }
            var result = Array(lines[range])
            if profile.alias != original.alias {
                result[0] = "Host " + profile.alias + Self.comment(result[0])
            }
            let old = Self.fields(original)
            for (index, field) in Self.fields(profile).enumerated() where field.values != old[index].values {
                let indices = result.indices.dropFirst().filter { Self.statement(result[$0]).0 == field.key.lowercased() }
                let insertion = indices.first ?? 1
                let comments = indices.map { Self.comment(result[$0]) }.filter { !$0.isEmpty }
                for at in indices.reversed() { result.remove(at: at) }
                let newLines = field.values.map { "    " + field.key + " " + Self.render(field.key, $0) }
                result.insert(contentsOf: newLines + comments, at: insertion)
            }
            lines.replaceSubrange(range, with: result)
        } else if let profile {
            let entry = ["Host " + profile.alias] + Self.fields(profile).flatMap { field in
                field.values.map { "    " + field.key + " " + Self.render(field.key, $0) }
            }
            // OpenSSH takes the first value from matching sections. Keep the new profile ahead of defaults.
            let insertion = lines.firstIndex {
                let key = Self.statement($0).0
                return key == "host" || key == "match"
            } ?? lines.endIndex
            lines.insert(contentsOf: entry + (insertion < lines.endIndex ? [""] : []), at: insertion)
        }
    }

    private static func fields(_ profile: Profile) -> [(key: String, values: [String])] {
        var result: [(key: String, values: [String])] = [("HostName", [profile.host]), ("User", [profile.user]), ("Port", [profile.port]),
         ("IdentityFile", profile.identities.components(separatedBy: .newlines)), ("ProxyJump", [profile.proxyJump])].map {
            ($0.0, $0.1.filter { !$0.isEmpty })
        }
        for name in ["LocalForward", "RemoteForward", "DynamicForward"] {
            result.append((name, profile.forwards.filter { $0.hasPrefix(name + " ") }.map { String($0.dropFirst(name.count + 1)) }))
        }
        return result
    }

    static let forwardNames = ["localforward": "LocalForward", "remoteforward": "RemoteForward", "dynamicforward": "DynamicForward"]

    /// Forwards keep their tokens separate (LocalForward 8080 localhost:80); other values are one quoted string.
    static func render(_ key: String, _ value: String) -> String {
        if forwardNames.values.contains(key) { return value.split(separator: " ").map { quote(String($0)) }.joined(separator: " ") }
        return quote(value)
    }

    static func quote(_ value: String) -> String {
        "\"" + value.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }

    static func comment(_ line: String) -> String {
        var quoted = false
        var escaped = false
        for index in line.indices {
            let character = line[index]
            if escaped { escaped = false; continue }
            if character == "\\" { escaped = true; continue }
            if character == "\"" { quoted.toggle(); continue }
            if character == "#" && !quoted && (index == line.startIndex || line[line.index(before: index)].isWhitespace) {
                return " " + line[index...]
            }
        }
        return ""
    }

    static func statement(_ line: String) -> (String, [String]) {
        let trimmed = line.drop(while: \.isWhitespace)
        guard let first = trimmed.first, first != "#" else { return ("", []) }
        let keyword = trimmed.prefix { !$0.isWhitespace && $0 != "=" }
        var tail = trimmed.dropFirst(keyword.count).drop(while: \.isWhitespace)
        if tail.first == "=" { tail = tail.dropFirst() }
        var words: [String] = []
        var word = ""
        var wasStarted = false
        var quoted = false
        var index = tail.startIndex
        while index < tail.endIndex {
            let character = tail[index]
            let next = tail.index(after: index)
            if character == "\\", next < tail.endIndex, ["\\", "\""].contains(tail[next]) || tail[next].isWhitespace {
                word.append(tail[next])
                wasStarted = true
                index = tail.index(after: next)
                continue
            }
            if character == "\"" { quoted.toggle(); wasStarted = true }
            else if !quoted && character == "#" && !wasStarted { break }
            else if !quoted && character.isWhitespace {
                if wasStarted { words.append(word); word = ""; wasStarted = false }
            } else { word.append(character); wasStarted = true }
            index = next
        }
        if wasStarted && !quoted { words.append(word) }
        return (keyword.lowercased(), words)
    }
}

struct ConfigStore {
    let url: URL
    static let standard = ConfigStore(url: FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ssh/config"))

    func read() throws -> ConfigDocument {
        let bytes: Data
        do { bytes = try readRegularFile(url, limit: BackupArchive.maxFile) }
        catch let error as NSError where error.domain == NSCocoaErrorDomain && error.code == NSFileReadNoSuchFileError {
            return ConfigDocument("")
        }
        guard let text = String(data: bytes, encoding: .utf8), !text.contains("\0")
        else { throw ManagerError("Не удалось прочитать OpenSSH config как текст UTF-8.") }
        return ConfigDocument(text.hasPrefix("\u{FEFF}") ? String(text.dropFirst()) : text)
    }

    func update(_ profile: Profile?, replacing original: Profile?) throws {
        try withLock {
            var document = try read()
            try document.update(profile, replacing: original)
            let resolved = url.resolvingSymlinksInPath()
            if FileManager.default.fileExists(atPath: resolved.path) {
                try writePrivate(readRegularFile(resolved, limit: BackupArchive.maxFile), to: url.appendingPathExtension("bak"))
            }
            try writePrivate(Data(document.text.utf8), to: resolved)
        }
    }

    func withLock<T>(_ operation: () throws -> T) throws -> T {
        let directory = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let lock = open(directory.appendingPathComponent(".ssh-manager.lock").path, O_WRONLY | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard lock >= 0 else { throw ManagerError("Не удалось открыть файл блокировки OpenSSH config.") }
        defer { close(lock) }
        guard flock(lock, LOCK_EX | LOCK_NB) == 0 else { throw ManagerError("Настройки OpenSSH заняты в другом окне.") }
        return try operation()
    }
}

func readRegularFile(_ url: URL, limit: Int) throws -> Data {
    let descriptor = open(url.path, O_RDONLY | O_NONBLOCK | O_CLOEXEC)
    guard descriptor >= 0 else {
        if errno == ENOENT { throw CocoaError(.fileReadNoSuchFile) }
        throw ManagerError("Не удалось открыть «\(url.lastPathComponent)»: \(String(cString: strerror(errno))).")
    }
    let file = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
    defer { try? file.close() }
    var info = stat()
    guard fstat(descriptor, &info) == 0, info.st_mode & S_IFMT == S_IFREG else {
        throw ManagerError("Путь «\(url.lastPathComponent)» должен указывать на обычный файл.")
    }
    guard info.st_size <= limit else { throw ManagerError("Файл «\(url.lastPathComponent)» превышает допустимый размер.") }
    let data = try file.read(upToCount: limit + 1) ?? Data()
    guard data.count <= limit else { throw ManagerError("Файл «\(url.lastPathComponent)» превышает допустимый размер.") }
    return data
}

func writePrivate(_ data: Data, to destination: URL) throws {
    let temporary = destination.deletingLastPathComponent().appendingPathComponent(".ssh-manager-" + UUID().uuidString)
    guard FileManager.default.createFile(atPath: temporary.path, contents: nil, attributes: [.posixPermissions: 0o600])
    else { throw ManagerError("Не удалось создать файл в папке «\(destination.deletingLastPathComponent().lastPathComponent)».") }
    defer { try? FileManager.default.removeItem(at: temporary) }
    let file = try FileHandle(forWritingTo: temporary)
    do { try file.write(contentsOf: data); try file.synchronize(); try file.close() }
    catch { try? file.close(); throw error }
    guard rename(temporary.path, destination.path) == 0 else {
        throw ManagerError("Не удалось сохранить «\(destination.lastPathComponent)».")
    }
}

enum Passwords {
    private static let service = "com.clarkufa.sshmanager.openssh"

    private static func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
    }

    static func read(_ account: String) throws -> String? {
        var request = query(account)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = item as? Data, let password = String(data: data, encoding: .utf8)
        else { throw keychainError(status) }
        return password
    }

    static func set(_ password: String?, for account: String) throws {
        let request = query(account)
        guard let password else {
            let status = SecItemDelete(request as CFDictionary)
            if status != errSecSuccess && status != errSecItemNotFound { throw keychainError(status) }
            return
        }
        let attributes = [kSecValueData as String: Data(password.utf8)]
        var status = SecItemUpdate(request as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            let item = request.merging(attributes) { _, new in new }
            status = SecItemAdd(item as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw keychainError(status) }
    }

    private static func keychainError(_ status: OSStatus) -> ManagerError {
        let description = SecCopyErrorMessageString(status, nil) as String? ?? String(status)
        return ManagerError("Связка ключей: " + description)
    }
}
