import Foundation

struct FileRow: Identifiable {
    enum Kind { case file, directory, link, other }
    let name: String
    let kind: Kind
    let size: UInt64?
    let date: Date?
    var id: String { name }
}

enum Collision: String, CaseIterable, Identifiable {
    case keepBoth = "Сохранять оба", skip = "Пропускать", replace = "Заменять файлы"
    var id: Self { self }
}

private struct Packet {
    var data = Data()
    private var offset = 0

    init() {}
    init(_ data: Data) { self.data = data }

    mutating func byte(_ value: UInt8) { data.append(value) }
    mutating func number(_ value: UInt32) {
        var network = value.bigEndian
        withUnsafeBytes(of: &network) { data.append(contentsOf: $0) }
    }
    mutating func position(_ value: UInt64) {
        number(UInt32(value >> 32))
        number(UInt32(truncatingIfNeeded: value))
    }
    mutating func bytes(_ value: Data) { number(UInt32(value.count)); data.append(value) }
    mutating func string(_ value: String) { bytes(Data(value.utf8)) }

    mutating func readByte() throws -> UInt8 {
        guard offset < data.count else { throw malformed() }
        defer { offset += 1 }
        return data[offset]
    }
    mutating func readNumber() throws -> UInt32 {
        guard data.count - offset >= 4 else { throw malformed() }
        defer { offset += 4 }
        return data.withUnsafeBytes { UInt32(bigEndian: $0.loadUnaligned(fromByteOffset: offset, as: UInt32.self)) }
    }
    mutating func readPosition() throws -> UInt64 { try (UInt64(readNumber()) << 32) | UInt64(readNumber()) }
    mutating func readBytes() throws -> Data {
        let size = Int(try readNumber())
        guard size <= data.count - offset else { throw malformed() }
        defer { offset += size }
        return data.subdata(in: offset..<(offset + size))
    }
    mutating func readString() throws -> String {
        guard let text = String(data: try readBytes(), encoding: .utf8) else { throw malformed() }
        return text
    }
    var atEnd: Bool { offset == data.count }
    private func malformed() -> ManagerError { ManagerError("Сервер вернул повреждённый пакет SFTP.") }
}

private struct SFTPStatus: LocalizedError {
    let code: UInt32
    let message: String
    var errorDescription: String? { message.isEmpty ? "Ошибка SFTP (\(code))." : "SFTP: \(message) (\(code))." }
}

private final class ErrorBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var buffer = Data()
    func append(_ data: Data) {
        lock.lock()
        if data.count >= 4096 { buffer = Data(data.suffix(4096)) }
        else { buffer.append(data); if buffer.count > 4096 { buffer = Data(buffer.suffix(4096)) } }
        lock.unlock()
    }
    var description: String {
        lock.lock()
        defer { lock.unlock() }
        return String(decoding: buffer, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

final class SFTPConnection {
    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private let diagnostics = Pipe()
    private let errors = ErrorBuffer()
    private var sequence: UInt32 = 0
    private var supportsReplace = false

    // All protocol I/O belongs to the file controller's serial queue.
    init(request: SSHRequest, executable: URL = URL(fileURLWithPath: "/usr/bin/ssh")) {
        process.executableURL = executable
        process.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
        process.arguments = request.arguments(sftp: true)
        process.environment = request.environment(prompt: true)
        process.standardInput = input
        process.standardOutput = output
        process.standardError = diagnostics
    }

    func open() throws {
        diagnostics.fileHandleForReading.readabilityHandler = { [errors] file in
            let data = file.availableData
            if data.isEmpty { file.readabilityHandler = nil }
            else { errors.append(data) }
        }
        try process.run()
        var hello = Packet()
        hello.byte(1)
        hello.number(3)
        try send(hello)
        var reply = try readPacket()
        guard try reply.readByte() == 2, try reply.readNumber() == 3 else { throw ManagerError("Сервер не поддерживает SFTP версии 3.") }
        while !reply.atEnd {
            let name = try reply.readString()
            let version = try reply.readString()
            if name == "posix-rename@openssh.com", version == "1" { supportsReplace = true }
        }
    }

    var isRunning: Bool { process.isRunning }

    func interrupt() { if process.isRunning { process.terminate() } }
    deinit { interrupt(); diagnostics.fileHandleForReading.readabilityHandler = nil }

    func path(_ path: String) throws -> String {
        var body = Packet()
        body.string(path)
        var reply = try request(16, body, expecting: 104)
        guard try reply.readNumber() == 1 else { throw ManagerError("Сервер не подтвердил путь.") }
        let result = try reply.readString()
        guard result.hasPrefix("/"), !result.contains("\0") else { throw ManagerError("Сервер вернул недопустимый путь.") }
        return result
    }

    func list(_ path: String) throws -> [FileRow] {
        var body = Packet()
        body.string(path)
        var reply = try request(11, body, expecting: 102)
        let handle = try reply.readBytes()
        var items: [FileRow] = []
        try using(handle) {
            var query = Packet()
            query.bytes(handle)
            while true {
                do {
                    var batch = try request(12, query, expecting: 104)
                    let count = Int(try batch.readNumber())
                    guard count <= 100_000 else { throw ManagerError("Слишком много записей в ответе SFTP.") }
                    for _ in 0..<count {
                        let name = try batch.readString()
                        _ = try batch.readBytes() // Long name is display text supplied by the server.
                        let item = try attributes(name, &batch)
                        if Self.valid(name) { items.append(item) }
                    }
                    guard items.count <= 200_000 else { throw ManagerError("Слишком много файлов для отображения.") }
                } catch let error as SFTPStatus where error.code == 1 { return }
            }
        }
        return items
    }

    func stat(_ path: String) throws -> FileRow? {
        var body = Packet()
        body.string(path)
        do {
            var reply = try request(7, body, expecting: 105)
            return try attributes("", &reply)
        } catch let error as SFTPStatus where error.code == 2 { return nil }
    }

    func createDirectory(_ path: String) throws {
        var body = Packet()
        body.string(path)
        body.number(0)
        _ = try request(14, body, expecting: 101)
    }

    func remove(_ path: String, directory: Bool) throws {
        var body = Packet()
        body.string(path)
        _ = try request(directory ? 15 : 13, body, expecting: 101)
    }

    func rename(_ from: String, to: String, replacing: Bool = false) throws {
        var body = Packet()
        if replacing {
            guard supportsReplace else { throw ManagerError("Сервер SFTP не поддерживает безопасную замену файла.") }
            body.string("posix-rename@openssh.com")
        }
        body.string(from)
        body.string(to)
        _ = try request(replacing ? 200 : 18, body, expecting: 101)
    }

    private let blockSize: UInt32 = 32768
    private let pipelineDepth = 16

    func download(_ path: String, to destination: URL, tick: (UInt64) throws -> Void) throws {
        let file = try FileHandle(forWritingTo: destination)
        defer { try? file.close() }
        let handle = try openFile(path, flags: 1)
        try using(handle) {
            var query = Packet()
            query.bytes(handle)
            let size: UInt64?
            do {
                var details = try request(8, query, expecting: 105)
                size = try attributes("", &details).size
            } catch let error as SFTPStatus where error.code == 8 { size = nil }
            if let size {
                try downloadPipelined(handle, size: size, to: file, tick: tick)
            } else {
                var offset: UInt64 = 0
                while true {
                    try tick(offset)
                    do {
                        let reply = try request(5, readRequest(handle, offset: offset, length: blockSize), expecting: 103)
                        let bytes = try readBlock(reply, limit: blockSize)
                        try file.write(contentsOf: bytes)
                        offset += UInt64(bytes.count)
                    } catch let error as SFTPStatus where error.code == 1 { break }
                }
            }
            try file.synchronize()
        }
    }

    private func downloadPipelined(_ handle: Data, size: UInt64, to file: FileHandle, tick: (UInt64) throws -> Void) throws {
        var next: UInt64 = 0
        var complete: UInt64 = 0
        var remaining: [(offset: UInt64, length: UInt32)] = []
        while next < size || !remaining.isEmpty {
            try tick(complete)
            var block = remaining
            remaining.removeAll(keepingCapacity: true)
            while block.count < pipelineDepth && next < size {
                let length = UInt32(min(UInt64(blockSize), size - next))
                block.append((next, length))
                next += UInt64(length)
            }
            var pending: [UInt32: (offset: UInt64, length: UInt32)] = [:]
            for (offset, length) in block {
                let id = try sendRequest(5, readRequest(handle, offset: offset, length: length))
                pending[id] = (offset, length)
            }
            var failure: Error?
            for _ in block {
                let (type, id, reply) = try receive()
                guard let span = pending.removeValue(forKey: id) else { throw malformedResponse() }
                if failure != nil { continue }
                do {
                    let bytes = try readBlock(checkReply(type, packet: reply, expecting: 103), limit: span.length)
                    try file.seek(toOffset: span.offset)
                    try file.write(contentsOf: bytes)
                    complete += UInt64(bytes.count)
                    if bytes.count < span.length {
                        remaining.append((span.offset + UInt64(bytes.count), span.length - UInt32(bytes.count)))
                    }
                } catch let error as SFTPStatus where error.code == 1 {
                    failure = ManagerError("Размер файла на сервере изменился во время скачивания.")
                } catch { failure = error }
            }
            if let failure { throw failure }
        }
        try tick(complete)
    }

    func upload(_ source: URL, to path: String, created: () -> Void, tick: (UInt64) throws -> Void) throws {
        let file = try FileHandle(forReadingFrom: source)
        defer { try? file.close() }
        let handle = try openFile(path, flags: 2 | 8 | 32)
        created()
        try using(handle) {
            var offset: UInt64 = 0
            var complete: UInt64 = 0
            var end = false
            while !end {
                try tick(complete)
                var pending: [UInt32: Int] = [:]
                var failure: Error?
                for _ in 0..<pipelineDepth {
                    let chunk: Data
                    do { chunk = try file.read(upToCount: Int(blockSize)) ?? Data() }
                    catch { failure = error; break }
                    guard !chunk.isEmpty else { end = true; break }
                    var body = Packet()
                    body.bytes(handle)
                    body.position(offset)
                    body.bytes(chunk)
                    pending[try sendRequest(6, body)] = chunk.count
                    offset += UInt64(chunk.count)
                }
                while !pending.isEmpty {
                    let (type, id, reply) = try receive()
                    guard let size = pending.removeValue(forKey: id) else { throw malformedResponse() }
                    do {
                        _ = try checkReply(type, packet: reply, expecting: 101)
                        complete += UInt64(size)
                    } catch { if failure == nil { failure = error } }
                }
                if let failure { throw failure }
            }
            try tick(complete)
        }
    }

    private func readRequest(_ handle: Data, offset: UInt64, length: UInt32) -> Packet {
        var body = Packet()
        body.bytes(handle)
        body.position(offset)
        body.number(length)
        return body
    }

    private func readBlock(_ reply: Packet, limit: UInt32) throws -> Data {
        var packet = reply
        let block = try packet.readBytes()
        guard !block.isEmpty, block.count <= limit else { throw ManagerError("Неверный блок данных SFTP.") }
        return block
    }

    static func valid(_ name: String) -> Bool {
        !name.isEmpty && name != "." && name != ".." && !name.contains("/") && !name.contains("\0")
    }

    private func attributes(_ name: String, _ packet: inout Packet) throws -> FileRow {
        let flags = try packet.readNumber()
        var size: UInt64?
        var kind: FileRow.Kind = .other
        var date: Date?
        if flags & 1 != 0 { size = try packet.readPosition() }
        if flags & 2 != 0 { _ = try packet.readNumber(); _ = try packet.readNumber() }
        if flags & 4 != 0 {
            switch try packet.readNumber() & 0o170000 {
            case 0o100000: kind = .file
            case 0o040000: kind = .directory
            case 0o120000: kind = .link
            default: break
            }
        }
        if flags & 8 != 0 {
            _ = try packet.readNumber()
            date = Date(timeIntervalSince1970: Double(try packet.readNumber()))
        }
        if flags & 0x80000000 != 0 {
            let count = try packet.readNumber()
            guard count <= 256 else { throw ManagerError("Некорректные атрибуты SFTP.") }
            for _ in 0..<count { _ = try packet.readBytes(); _ = try packet.readBytes() }
        }
        return FileRow(name: name, kind: kind, size: size, date: date)
    }

    private func openFile(_ path: String, flags: UInt32) throws -> Data {
        var query = Packet()
        query.string(path)
        query.number(flags)
        query.number(4)
        query.number(0o600)
        var reply = try request(3, query, expecting: 102)
        return try reply.readBytes()
    }

    private func using(_ handle: Data, _ work: () throws -> Void) throws {
        var body = Packet()
        body.bytes(handle)
        do { try work() }
        catch { _ = try? request(4, body, expecting: 101); throw error }
        _ = try request(4, body, expecting: 101)
    }

    private func request(_ type: UInt8, _ body: Packet, expecting expected: UInt8) throws -> Packet {
        let id = try sendRequest(type, body)
        let (responseType, receivedID, reply) = try receive()
        guard receivedID == id else { throw malformedResponse() }
        return try checkReply(responseType, packet: reply, expecting: expected)
    }

    private func sendRequest(_ type: UInt8, _ body: Packet) throws -> UInt32 {
        sequence &+= 1
        var packet = Packet()
        packet.byte(type)
        packet.number(sequence)
        packet.data.append(body.data)
        try send(packet)
        return sequence
    }

    private func receive() throws -> (UInt8, UInt32, Packet) {
        var reply = try readPacket()
        let type = try reply.readByte()
        let id = try reply.readNumber()
        return (type, id, reply)
    }

    private func checkReply(_ type: UInt8, packet: Packet, expecting expected: UInt8) throws -> Packet {
        var reply = packet
        if type == 101 {
            let code = try reply.readNumber()
            let message = try reply.readString()
            if code != 0 { throw SFTPStatus(code: code, message: message) }
        }
        guard type == expected else { throw ManagerError("Неожиданный ответ сервера SFTP.") }
        return reply
    }

    private func malformedResponse() -> ManagerError {
        ManagerError("Получен неожиданный идентификатор ответа SFTP.")
    }

    private func send(_ packet: Packet) throws {
        guard packet.data.count <= 1024 * 1024 else { throw ManagerError("Слишком большой запрос SFTP.") }
        var frame = Packet()
        frame.bytes(packet.data)
        do { try input.fileHandleForWriting.write(contentsOf: frame.data) }
        catch { throw disconnected() }
    }

    private func readPacket() throws -> Packet {
        var length = Packet(try readExact(4))
        let count = Int(try length.readNumber())
        guard (1...(1024 * 1024)).contains(count) else { throw ManagerError("Некорректный размер пакета SFTP.") }
        return Packet(try readExact(count))
    }

    private func readExact(_ count: Int) throws -> Data {
        var result = Data()
        while result.count < count {
            guard let chunk = try? output.fileHandleForReading.read(upToCount: count - result.count), !chunk.isEmpty
            else { throw disconnected() }
            result.append(chunk)
        }
        return result
    }

    private func disconnected() -> ManagerError {
        let reason = errors.description
        return ManagerError(reason.isEmpty ? "Подключение SFTP завершилось." : reason)
    }
}

func remotePath(_ folder: String, _ name: String) -> String { folder == "/" ? "/" + name : folder + "/" + name }

enum LocalFiles {
    static func item(_ url: URL) throws -> FileRow? {
        var info = stat()
        guard lstat(url.path, &info) == 0 else {
            if errno == ENOENT { return nil }
            throw ManagerError("Не удалось прочитать «\(url.lastPathComponent)».")
        }
        let kind: FileRow.Kind
        switch info.st_mode & S_IFMT {
        case S_IFREG: kind = .file
        case S_IFDIR: kind = .directory
        case S_IFLNK: kind = .link
        default: kind = .other
        }
        return FileRow(name: url.lastPathComponent, kind: kind, size: UInt64(max(0, info.st_size)),
                       date: Date(timeIntervalSince1970: Double(info.st_mtimespec.tv_sec)))
    }

    static func list(_ url: URL) throws -> [FileRow] {
        try FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: nil).compactMap(item)
    }
}

final class FileTransfer {
    private let connection: SFTPConnection
    /// Asked for every name that already exists: «Заменить», «Сохранить оба» or «Пропустить»; throws to cancel.
    private let decide: (String) throws -> Collision
    private let check: () throws -> Void
    private let progress: (String, UInt64) -> Void

    init(_ connection: SFTPConnection, decide: @escaping (String) throws -> Collision, check: @escaping () throws -> Void,
         progress: @escaping (String, UInt64) -> Void) {
        self.connection = connection
        self.decide = decide
        self.check = check
        self.progress = progress
    }

    func upload(_ source: URL, to folder: String, depth: Int = 0) throws {
        try check()
        guard depth <= 64 else { throw ManagerError("Достигнут предел вложенности папок.") }
        guard let entry = try LocalFiles.item(source), entry.kind == .directory || entry.kind == .file else { return }
        guard case let (name, replace)? = try availableName(entry, place: remotePath(folder, entry.name),
                                                      checkExisting: { try connection.stat(remotePath(folder, $0)) }) else { return }
        let target = remotePath(folder, name)
        if entry.kind == .directory {
            if let current = try connection.stat(target) {
                guard current.kind == .directory else { throw ManagerError("Удалённый путь «\(name)» изменился.") }
            } else { try connection.createDirectory(target) }
            for child in try FileManager.default.contentsOfDirectory(at: source, includingPropertiesForKeys: nil) {
                try upload(child, to: target, depth: depth + 1)
            }
        } else {
            let stage = remotePath(folder, ".ssh-manager-" + UUID().uuidString)
            var created = false
            defer { if created { try? connection.remove(stage, directory: false) } }
            try connection.upload(source, to: stage, created: { created = true }) { try check(); progress(entry.name, $0) }
            try check()
            let existing = try connection.stat(target)
            guard existing == nil || (replace && existing?.kind == .file)
            else { throw ManagerError("Файл «\(name)» появился во время передачи.") }
            try connection.rename(stage, to: target, replacing: existing != nil)
            created = false
        }
    }

    func download(_ entry: FileRow, from folder: String, to localFolder: URL, depth: Int = 0) throws {
        try check()
        guard depth <= 64 else { throw ManagerError("Достигнут предел вложенности папок.") }
        guard SFTPConnection.valid(entry.name), entry.kind == .directory || entry.kind == .file else { return }
        guard case let (name, replace)? = try availableName(entry, place: localFolder.appendingPathComponent(entry.name).path,
                                                      checkExisting: { try LocalFiles.item(localFolder.appendingPathComponent($0)) }) else { return }
        let source = remotePath(folder, entry.name)
        let target = localFolder.appendingPathComponent(name)
        if entry.kind == .directory {
            if let current = try LocalFiles.item(target) {
                guard current.kind == .directory else { throw ManagerError("Локальный путь «\(name)» изменился.") }
            } else {
                try FileManager.default.createDirectory(at: target, withIntermediateDirectories: false)
            }
            for child in try connection.list(source) { try download(child, from: source, to: target, depth: depth + 1) }
        } else {
            let stage = localFolder.appendingPathComponent(".ssh-manager-" + UUID().uuidString)
            let descriptor = open(stage.path, O_CREAT | O_EXCL | O_WRONLY | O_CLOEXEC, 0o600)
            guard descriptor >= 0 else { throw ManagerError("Не удалось создать локальный файл.") }
            close(descriptor)
            defer { try? FileManager.default.removeItem(at: stage) }
            try connection.download(source, to: stage) { try check(); progress(entry.name, $0) }
            try check()
            let existing = try LocalFiles.item(target)
            if replace && existing?.kind == .file {
                guard rename(stage.path, target.path) == 0 else { throw ManagerError("Не удалось заменить «\(name)».") }
            } else {
                guard renamex_np(stage.path, target.path, UInt32(RENAME_EXCL)) == 0
                else { throw ManagerError("Не удалось сохранить «\(name)».") }
            }
        }
    }

    private func availableName(_ entry: FileRow, place: String,
                               checkExisting: (String) throws -> FileRow?) throws -> (String, Bool)? {
        guard let existing = try checkExisting(entry.name) else { return (entry.name, false) }
        // A folder goes into the folder of the same name, as in the Windows app.
        if entry.kind == .directory && existing.kind == .directory { return (entry.name, false) }
        let choice = try decide(place)
        if choice == .skip { return nil }
        if choice == .replace && existing.kind == entry.kind { return (entry.name, true) }
        let original = entry.name as NSString
        let ext = entry.kind == .file ? original.pathExtension : ""
        let stem = ext.isEmpty ? entry.name : original.deletingPathExtension
        for suffix in 2...10_000 {
            let candidate = "\(stem) (\(suffix))" + (ext.isEmpty ? "" : "." + ext)
            if try checkExisting(candidate) == nil { return (candidate, false) }
        }
        throw ManagerError("Не удалось подобрать свободное имя для «\(entry.name)».")
    }
}
