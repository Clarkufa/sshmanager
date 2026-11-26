import AppKit
import Combine
import CommonCrypto
import Darwin
import Foundation

final class VncSession: RemoteDesktopSession, Identifiable {
    let id = UUID()
    let profile: VncProfile
    let number: Int
    @Published var name = ""
    @Published private(set) var status = "подключение"
    @Published private(set) var live = true
    @Published private(set) var endedTitle = ""
    @Published private(set) var endedDetail = ""
    @Published private(set) var external = ""

    private let lookupSSH: (String) -> Profile?
    private let password: String?
    private let viewerURL: URL?
    private var viewer: Process?
    private var tunnel: Process?
    private var passwordDirectory: URL?
    private var clipboardChange: Int?
    private var terminationObserver: NSObjectProtocol?
    private var generation = 0

    init(profile: VncProfile, number: Int, password: String?, lookupSSH: @escaping (String) -> Profile?) {
        self.profile = profile
        self.number = number
        self.password = password
        self.lookupSSH = lookupSSH
        viewerURL = VncProfile.tigerVNC()
        terminationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: .main
        ) { [weak self] _ in self?.stop() }
    }

    deinit {
        if let observer = terminationObserver { NotificationCenter.default.removeObserver(observer) }
    }

    var title: String {
        if !name.isEmpty { return "\(profile.name) · \(name)" }
        return number <= 1 ? profile.name : "\(profile.name) · \(number)"
    }
    var address: String { profile.address }
    var route: String { profile.via.isEmpty ? "" : "через " + profile.via }
    var supportsFullScreen: Bool { viewerURL != nil }

    func start(fullScreen: Bool = false) {
        generation += 1
        stopClients()
        live = true
        status = "подключение"
        endedTitle = ""
        endedDetail = ""
        external = ""
        let run = generation

        do {
            if viewerURL == nil, profile.viewOnly {
                throw ManagerError("Для режима «Только просмотр» установите TigerVNC Viewer.")
            }
            if profile.via.isEmpty {
                try open(host: profile.host, port: profile.port, fullScreen: fullScreen, run: run)
            } else {
                try startTunnel(fullScreen: fullScreen, run: run)
            }
        } catch { finish(failed: true, detail: error.localizedDescription) }
    }

    func stop() {
        generation += 1
        live = false
        stopClients()
    }

    private func startTunnel(fullScreen: Bool, run: Int) throws {
        do {
            guard let profile = lookupSSH(self.profile.via) else {
                throw ManagerError("Сервер «\(self.profile.via)» не найден в каталоге SSH.")
            }
            let port = try Self.loopbackPort()
            let destination = "127.0.0.1:\(port):\(VncProfile.formatHost(self.profile.host)):\(self.profile.port)"
            let request = SSHRequest(profile: profile)
            tunnel = try Self.launch(URL(fileURLWithPath: "/usr/bin/ssh"), arguments: request.arguments(tunnel: destination),
                                     environment: request.environment(prompt: true)) { [weak self] code, log in
                guard let self, self.generation == run else { return }
                let reason = log.isEmpty ? "OpenSSH завершился с кодом \(code)." : log
                self.finish(failed: true, detail: "Не удалось открыть SSH-туннель: " + reason)
            }
            waitForTunnel(port: port, fullScreen: fullScreen, run: run, deadline: .now() + 15)
        } catch {
            throw ManagerError("Не удалось открыть SSH-туннель: " + error.localizedDescription)
        }
    }

    private func waitForTunnel(port: Int, fullScreen: Bool, run: Int, deadline: DispatchTime) {
        guard generation == run, tunnel?.isRunning == true else { return }
        if Self.portIsListening(port) {
            do { try open(host: "127.0.0.1", port: port, fullScreen: fullScreen, run: run) }
            catch { finish(failed: true, detail: error.localizedDescription) }
        } else if DispatchTime.now() >= deadline {
            finish(failed: true, detail: "Не удалось открыть SSH-туннель: SSH-сервер не ответил за отведённое время.")
        } else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
                self?.waitForTunnel(port: port, fullScreen: fullScreen, run: run, deadline: deadline)
            }
        }
    }

    private func open(host: String, port: Int, fullScreen: Bool, run: Int) throws {
        guard let viewerURL else { try openScreenSharing(host: host, port: port, run: run); return }

        var args = ["-Shared", "-RemoteResize=\(profile.scale == .fit ? 1 : 0)"]
        if fullScreen || profile.fullScreen { args.append("-FullScreen") }
        if profile.viewOnly { args.append("-ViewOnly") }
        if !profile.clipboard || profile.viewOnly { args += ["-SendClipboard=0", "-AcceptClipboard=0"] }
        var environment = ProcessInfo.processInfo.environment
        if !profile.user.isEmpty { environment["VNC_USERNAME"] = profile.user }
        if let password, !password.isEmpty {
            environment.removeValue(forKey: "VNC_PASSWORD")
            let files = FileManager.default
            let directory = files.temporaryDirectory.appendingPathComponent("SSHManager-vnc-\(id)-\(run)")
            try files.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            passwordDirectory = directory
            let url = directory.appendingPathComponent("passwd")
            try writePrivate(Self.passwordFile(password), to: url)
            args += ["-passwd", url.path]
            DispatchQueue.main.asyncAfter(deadline: .now() + 10) { [weak self] in
                if self?.generation == run { self?.removePasswordFile() }
            }
        }
        args.append("\(VncProfile.formatHost(host))::\(port)")
        external = "Рабочий стол открыт в отдельном окне TigerVNC Viewer."
        viewer = try Self.launch(viewerURL, arguments: args, environment: environment) { [weak self] code, log in
            guard let self, self.generation == run else { return }
            self.finish(failed: code != 0, detail: code == 0 ? "Удалённый сеанс закрыт." :
                [log, "Код: \(code)"].filter { !$0.isEmpty }.joined(separator: "\n"))
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
            guard let self, self.generation == run, self.viewer?.isRunning == true else { return }
            self.status = "подключён"
        }
    }

    private func openScreenSharing(host: String, port: Int, run: Int) throws {
        var target = URLComponents()
        target.scheme = "vnc"
        target.host = VncProfile.formatHost(host)
        target.port = port
        if !profile.user.isEmpty { target.user = profile.user }
        let workspace = NSWorkspace.shared
        guard let url = target.url,
              let client = workspace.urlForApplication(withBundleIdentifier: "com.apple.ScreenSharing") ??
                           workspace.urlForApplication(toOpen: url) else {
            throw ManagerError("Не удалось открыть системное приложение «Общий экран».")
        }
        workspace.open([url], withApplicationAt: client, configuration: NSWorkspace.OpenConfiguration()) { [weak self] _, error in
            DispatchQueue.main.async {
                guard let self, self.generation == run else { return }
                if let error { self.finish(failed: true, detail: error.localizedDescription); return }
                self.status = "открыт в приложении «Общий экран»"
                self.external = "Рабочий стол открыт в приложении «Общий экран»."
                if let password = self.password, !password.isEmpty {
                    let board = NSPasteboard.general
                    board.clearContents()
                    guard board.setString(password, forType: .string) else { return }
                    self.clipboardChange = board.changeCount
                    self.external += " Пароль скопирован в буфер обмена на минуту. Вставьте его (⌘V) при запросе входа."
                    DispatchQueue.main.asyncAfter(deadline: .now() + 60) { [weak self] in
                        guard let self, self.generation == run else { return }
                        self.clearClipboard()
                        self.external = "Рабочий стол открыт в приложении «Общий экран»."
                    }
                }
            }
        }
    }

    private func finish(failed: Bool, detail: String) {
        stop()
        status = failed ? "отключён (ошибка)" : "отключён"
        endedTitle = failed ? "Подключение прервано" : "Сеанс завершён"
        endedDetail = "\(profile.name) · \(profile.address)\n" + detail
    }

    private func stopClients() {
        for process in [viewer, tunnel].compactMap({ $0 }) where process.isRunning { process.terminate() }
        viewer = nil
        tunnel = nil
        removePasswordFile()
        clearClipboard()
    }

    private func removePasswordFile() {
        if let directory = passwordDirectory { try? FileManager.default.removeItem(at: directory) }
        passwordDirectory = nil
    }

    private func clearClipboard() {
        let board = NSPasteboard.general
        if clipboardChange == board.changeCount { board.clearContents() }
        clipboardChange = nil
    }

    private static func launch(_ executable: URL, arguments: [String], environment: [String: String],
                               onExit: @escaping (Int32, String) -> Void) throws -> Process {
        let process = Process()
        let output = Pipe()
        let log = OutputTail()
        process.executableURL = executable
        process.arguments = arguments
        process.environment = environment
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = output
        process.standardError = output
        output.fileHandleForReading.readabilityHandler = { handle in log.append(handle.availableData) }
        process.terminationHandler = { finished in
            output.fileHandleForReading.readabilityHandler = nil
            let lines = log.text.split(separator: "\n").suffix(4).joined(separator: "\n")
            DispatchQueue.main.async { onExit(finished.terminationStatus, lines) }
        }
        do { try process.run() }
        catch {
            output.fileHandleForReading.readabilityHandler = nil
            throw error
        }
        return process
    }

    private static func passwordFile(_ password: String) throws -> Data {
        // TigerVNC's passwd format is one DES block under its fixed obfuscation key.
        let key: [UInt8] = [23, 82, 107, 6, 35, 78, 88, 7].map { byte in
            (0..<8).reduce(UInt8(0)) { result, bit in result | (((byte >> bit) & 1) << (7 - bit)) }
        }
        var input = Array(password.utf8.prefix(8))
        input += Array(repeating: 0, count: 8 - input.count)
        var output = [UInt8](repeating: 0, count: 8)
        var written = 0
        let count = output.count
        let result = CCCrypt(CCOperation(kCCEncrypt), CCAlgorithm(kCCAlgorithmDES), CCOptions(kCCOptionECBMode),
                             key, key.count, nil, input, input.count, &output, count, &written)
        guard result == kCCSuccess, written == count else { throw ManagerError("Не удалось подготовить пароль VNC.") }
        return Data(output)
    }

    private static func loopbackAddress(_ port: Int) -> sockaddr_in {
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = UInt16(port).bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        return address
    }

    private static func loopbackPort() throws -> Int {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        defer { Darwin.close(fd) }
        var address = loopbackAddress(0)
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        try withUnsafeMutablePointer(to: &address) { pointer in
            try pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                guard bind(fd, $0, length) == 0, getsockname(fd, $0, &length) == 0 else {
                    throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
                }
            }
        }
        return Int(UInt16(bigEndian: address.sin_port))
    }

    private static func portIsListening(_ port: Int) -> Bool {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { Darwin.close(fd) }
        var address = loopbackAddress(port)
        return withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }
}
