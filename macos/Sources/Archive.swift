import CommonCrypto
import CryptoKit
import Foundation
import Security

enum BackupArchive {
    static let magic = Data("SSHMBAK1".utf8)
    static let maxFile = 8 * 1024 * 1024
    static let maxTotal = 64 * 1024 * 1024
    static let maxArchive = maxTotal + 2 * 1024 * 1024
    static let maxEntries = 2048
    private static let marker = "ssh-manager-format.txt"
    private static let version = Data("SSH Manager backup 1\n".utf8)

    static func readFile(_ url: URL, limit: Int) throws -> Data {
        try readRegularFile(url, limit: limit)
    }

    static func unpack(_ bytes: Data, password: String) throws -> [String: Data] {
        guard bytes.count <= maxArchive else { throw tooLarge() }
        let protected = bytes.starts(with: magic)
        let zip = protected ? try ArchiveCipher.decrypt(bytes, password: password) : bytes
        guard let reader = archive_read_new() else { throw invalid() }
        defer { archive_read_free(reader) }
        guard archive_read_support_format_zip_seekable(reader) == 0 else { throw invalid() }
        return try zip.withUnsafeBytes { input in
            guard archive_read_open_memory(reader, input.baseAddress, input.count) == 0 else { throw invalid() }
            var result: [String: Data] = [:]
            var paths: Set<String> = []
            var total = 0
            while true {
                var entry: OpaquePointer?
                let status = archive_read_next_header(reader, &entry)
                if status == 1 { break }
                guard status == 0, let entry,
                      let pathname = archive_entry_pathname_utf8(entry) ?? archive_entry_pathname(entry),
                      let name = String(validatingUTF8: pathname), validPath(name),
                      archive_entry_mode(entry) & S_IFMT == S_IFREG,
                      archive_entry_hardlink(entry) == nil else { throw invalid() }
                guard protected || !name.lowercased().hasPrefix("app/passwords/") else {
                    throw ManagerError("Пароли могут храниться только в защищённой копии.")
                }
                let key = name.precomposedStringWithCanonicalMapping.lowercased()
                guard paths.insert(key).inserted else { throw invalid() }
                let length = archive_entry_size(entry)
                guard length >= 0, length <= maxFile, total + Int(length) <= maxTotal + version.count,
                      result.count <= maxEntries else { throw tooLarge() }
                var contents = Data(count: Int(length))
                let count = contents.withUnsafeMutableBytes { buffer -> Int in
                    var offset = 0
                    while offset < buffer.count {
                        let read = archive_read_data(reader, buffer.baseAddress?.advanced(by: offset), buffer.count - offset)
                        if read <= 0 { return -1 }
                        offset += read
                    }
                    return offset
                }
                var excess: UInt8 = 0
                guard count == length, archive_read_data(reader, &excess, 1) == 0 else { throw invalid() }
                result[name] = contents
                total += count
            }
            guard result.removeValue(forKey: marker) == version else { throw invalid() }
            return result
        }
    }

    static func pack(_ entries: [String: Data], password: String) throws -> Data {
        guard !password.isEmpty || !entries.keys.contains(where: { $0.hasPrefix("app/passwords/") }) else {
            throw ManagerError("Пароли могут храниться только в защищённой копии.")
        }
        let total = entries.values.reduce(0) { $0 + $1.count }
        guard total <= maxTotal, entries.count <= maxEntries else { throw tooLarge() }
        let files = entries.merging([marker: version]) { _, version in version }
        guard let writer = archive_write_new() else { throw invalid() }
        guard archive_write_set_format_zip(writer) == 0,
              archive_write_set_bytes_in_last_block(writer, 1) == 0 else {
            archive_write_free(writer)
            throw invalid()
        }
        // Allow for ZIP headers, filenames and compression overhead.
        var zip = Data(count: min(maxArchive, total + files.keys.reduce(0) { $0 + $1.utf8.count * 2 + 256 } + 65536))
        var written = 0
        try zip.withUnsafeMutableBytes { buffer in
            // libarchive keeps both of these pointers until close.
            try withUnsafeMutablePointer(to: &written) { length in
                defer { archive_write_free(writer) }
                guard archive_write_open_memory(writer, buffer.baseAddress, buffer.count, length) == 0 else { throw invalid() }
                for name in files.keys.sorted() {
                    guard let contents = files[name], contents.count <= maxFile, validPath(name),
                          let entry = archive_entry_new() else { throw invalid() }
                    defer { archive_entry_free(entry) }
                    archive_entry_set_pathname_utf8(entry, name)
                    archive_entry_set_mode(entry, S_IFREG | 0o600)
                    archive_entry_set_size(entry, Int64(contents.count))
                    guard archive_write_header(writer, entry) == 0 else { throw invalid() }
                    let count = contents.withUnsafeBytes { archive_write_data(writer, $0.baseAddress, $0.count) }
                    guard count == contents.count else { throw invalid() }
                }
                guard archive_write_close(writer) == 0 else { throw invalid() }
            }
        }
        zip.count = written
        return password.isEmpty ? zip : try ArchiveCipher.encrypt(zip, password: password)
    }

    static func validPath(_ path: String) -> Bool {
        guard !path.isEmpty, path.utf8.count <= 1024 else { return false }
        return path.split(separator: "/", omittingEmptySubsequences: false).allSatisfy { component in
            !component.isEmpty && !component.hasSuffix(".") && !component.hasSuffix(" ") &&
            !component.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) &&
            component.rangeOfCharacter(from: CharacterSet(charactersIn: "\\:\"*?<>|")) == nil &&
            component.range(of: #"^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)(?:\.|$)|^[^.]{1,6}~[0-9]+(?:\.|$)"#,
                            options: [.regularExpression, .caseInsensitive]) == nil
        }
    }

    static func invalid() -> ManagerError { ManagerError("Архив повреждён или имеет неподдерживаемый формат SSH Manager.") }
    static func tooLarge() -> ManagerError { ManagerError("Превышен допустимый объём резервной копии.") }
}

private enum ArchiveCipher {
    static func encrypt(_ zip: Data, password: String) throws -> Data {
        let salt = try random()
        let iv = try random()
        let keys = try derive(password, salt: salt)
        let ciphertext = try crypt(zip, operation: kCCEncrypt, key: Data(keys.prefix(32)), iv: iv)
        let body = BackupArchive.magic + salt + iv + ciphertext
        let tag = HMAC<SHA256>.authenticationCode(for: body, using: SymmetricKey(data: keys.suffix(32)))
        return body + Data(tag)
    }

    static func decrypt(_ archive: Data, password: String) throws -> Data {
        guard !password.isEmpty else { throw ManagerError("Введите пароль резервной копии.") }
        let header = BackupArchive.magic.count + 32
        guard archive.count >= header + 16 + 32, (archive.count - header - 32) % 16 == 0 else {
            throw BackupArchive.invalid()
        }
        let start = BackupArchive.magic.count
        let keys = try derive(password, salt: archive.subdata(in: start..<(start + 16)))
        let authenticated = archive.dropLast(32)
        guard HMAC<SHA256>.isValidAuthenticationCode(archive.suffix(32), authenticating: authenticated,
                                                      using: SymmetricKey(data: keys.suffix(32))) else {
            throw ManagerError("Неверный пароль или повреждённая резервная копия.")
        }
        return try crypt(archive.subdata(in: header..<authenticated.endIndex), operation: kCCDecrypt,
                         key: Data(keys.prefix(32)), iv: archive.subdata(in: (start + 16)..<header))
    }

    private static func random() throws -> Data {
        var data = Data(count: 16)
        let status = data.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, $0.count, $0.baseAddress!) }
        guard status == errSecSuccess else { throw BackupArchive.invalid() }
        return data
    }

    private static func derive(_ password: String, salt: Data) throws -> Data {
        var keys = Data(count: 64)
        let status = keys.withUnsafeMutableBytes { key in
            salt.withUnsafeBytes { saltBytes in
                password.withCString {
                    CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), $0, password.utf8.count,
                                          saltBytes.bindMemory(to: UInt8.self).baseAddress, saltBytes.count,
                                          CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), 600_000,
                                          key.bindMemory(to: UInt8.self).baseAddress, key.count)
                }
            }
        }
        guard status == kCCSuccess else { throw BackupArchive.invalid() }
        return keys
    }

    private static func crypt(_ input: Data, operation: Int, key: Data, iv: Data) throws -> Data {
        var output = Data(count: input.count + kCCBlockSizeAES128)
        var length = 0
        let status = output.withUnsafeMutableBytes { encrypted in
            input.withUnsafeBytes { plain in
                key.withUnsafeBytes { key in
                    iv.withUnsafeBytes { iv in
                        CCCrypt(CCOperation(operation), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                                key.baseAddress, key.count, iv.baseAddress, plain.baseAddress, plain.count,
                                encrypted.baseAddress, encrypted.count, &length)
                    }
                }
            }
        }
        guard status == kCCSuccess else { throw BackupArchive.invalid() }
        output.count = length
        return output
    }
}
