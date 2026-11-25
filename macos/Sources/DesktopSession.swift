import AppKit
import Foundation

/// An RDP connection shown in the sessions list. The desktop itself opens in FreeRDP's or Windows App's window,
/// but the session keeps a status, can reconnect, go full screen and explains why it ended (docs/ui-spec.md §3.8).
final class DesktopSession: ObservableObject, Identifiable {
    let id = UUID()
    let profile: DesktopProfile
    let number: Int
    let user: String
    let domain: String
    let password: String?
    @Published var name = ""
    @Published private(set) var status = "подключение"
    @Published private(set) var live = true
    @Published private(set) var endedTitle = ""
    @Published private(set) var endedDetail = ""
    @Published private(set) var external = ""
    private var process: Process?
    private var stopping = false
    private var generation = 0

    init(profile: DesktopProfile, number: Int, user: String, domain: String, password: String?) {
        self.profile = profile
        self.number = number
        self.user = user
        self.domain = domain
        self.password = password
    }

    var title: String {
        if !name.isEmpty { return "\(profile.name) · \(name)" }
        return number <= 1 ? profile.name : "\(profile.name) · \(number)"
    }

    var usesFreeRDP: Bool { DesktopProfile.freeRDPClient() != nil }

    func start(fullScreen: Bool = false) {
        stopProcess()
        generation += 1
        live = true
        status = "подключение"
        endedTitle = ""
        endedDetail = ""
        if let client = DesktopProfile.freeRDPClient() {
            external = "Рабочий стол открыт в отдельном окне FreeRDP."
            launch(client, fullScreen: fullScreen, generation: generation)
        } else {
            external = "Рабочий стол открыт в Windows App."
            openInWindowsApp()
        }
    }

    func stop() {
        stopProcess()
        live = false
    }

    private func stopProcess() {
        guard let running = process else { return }
        stopping = true
        process = nil
        if running.isRunning { running.terminate() }
    }

    private func launch(_ client: URL, fullScreen: Bool, generation: Int) {
        do {
            var shown = profile
            shown.user = user
            shown.domain = domain
            shown.fullScreen = fullScreen || profile.fullScreen
            let args = try shown.freeRDPArguments(password: password)
            let process = Process()
            process.executableURL = client
            var environment = ProcessInfo.processInfo.environment
            if password != nil, DesktopProfile.freeRDPMajor(client) >= 3 {
                // FreeRDP 3 reads every argument from the environment, so the password never appears in argv.
                environment[DesktopProfile.argumentsVariable] = args.joined(separator: "\n")
                process.arguments = ["/args-from:env:" + DesktopProfile.argumentsVariable]
            } else {
                // FreeRDP 2 overwrites /p: in its argv right after parsing.
                process.arguments = args
            }
            process.environment = environment
            process.standardInput = FileHandle.nullDevice
            let output = Pipe()
            process.standardOutput = output
            process.standardError = output
            let log = OutputTail()
            output.fileHandleForReading.readabilityHandler = { handle in log.append(handle.availableData) }
            process.terminationHandler = { [weak self] finished in
                output.fileHandleForReading.readabilityHandler = nil
                let code = finished.terminationStatus
                let lines = log.text.split(separator: "\n").suffix(4).joined(separator: "\n")
                DispatchQueue.main.async { self?.ended(code: code, log: lines, generation: generation) }
            }
            stopping = false
            try process.run()
            self.process = process
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { [weak self] in
                guard let self, self.generation == generation, self.live, self.process?.isRunning == true else { return }
                self.status = "подключён"
            }
        } catch {
            fail(error.localizedDescription)
        }
    }

    private func ended(code: Int32, log: String, generation: Int) {
        guard generation == self.generation, !stopping else { return }
        process = nil
        live = false
        // FreeRDP uses 0 for a normal close and 1-3, 12 for user or server initiated disconnects.
        let normal = [0, 1, 2, 3, 12].contains(code)
        status = normal ? "отключён" : "отключён (код \(code))"
        endedTitle = normal ? "Сеанс завершён" : "Подключение прервано"
        endedDetail = "\(profile.name) · \(profile.address)\n" + (normal ? "Удалённый сеанс закрыт." : log + "\nКод: \(code)")
    }

    private func fail(_ reason: String) {
        live = false
        status = "ошибка подключения"
        endedTitle = "Не удалось подключиться"
        endedDetail = "\(profile.name) · \(profile.address)\n" + reason
    }

    private func openInWindowsApp() {
        let files = FileManager.default
        let directory = files.temporaryDirectory.appendingPathComponent("SSHManager-\(UUID().uuidString)", isDirectory: true)
        let url = directory.appendingPathComponent("connection.rdp")
        do {
            try files.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            var shown = profile
            shown.user = user
            shown.domain = domain
            try writePrivate(shown.data, to: url)
            let workspace = NSWorkspace.shared
            guard let client = workspace.urlForApplication(withBundleIdentifier: "com.microsoft.rdc.macos") ??
                    workspace.urlForApplication(toOpen: url)
            else { throw ManagerError("Установите FreeRDP (brew install freerdp) или Microsoft Windows App для подключения по RDP.") }
            let password = self.password
            workspace.open([url], withApplicationAt: client, configuration: NSWorkspace.OpenConfiguration()) { [weak self] _, error in
                DispatchQueue.main.async {
                    if let error { self?.fail(error.localizedDescription); return }
                    // Windows App does not report when the desktop closes; the session stays open until closed here.
                    self?.status = "открыт в Windows App"
                    guard let password, !password.isEmpty else { return }
                    // Windows App ignores passwords in .rdp files: offer the saved one through the clipboard for a minute.
                    let board = NSPasteboard.general
                    board.clearContents()
                    board.setString(password, forType: .string)
                    let change = board.changeCount
                    DispatchQueue.main.asyncAfter(deadline: .now() + 60) {
                        if board.changeCount == change { board.clearContents() }
                    }
                    self?.external = "Рабочий стол открыт в Windows App. Пароль скопирован в буфер обмена на минуту: вставьте его (⌘V) при запросе входа."
                }
                DispatchQueue.global().asyncAfter(deadline: .now() + 120) {
                    try? files.removeItem(at: url)
                    try? files.removeItem(at: directory)
                }
            }
        } catch {
            try? files.removeItem(at: url)
            try? files.removeItem(at: directory)
            fail(error.localizedDescription)
        }
    }
}

struct DesktopLogonRequest: Identifiable {
    let id = UUID()
    let profile: DesktopProfile
    let completion: (_ user: String, _ domain: String, _ password: String, _ remember: Bool) -> Void
}
