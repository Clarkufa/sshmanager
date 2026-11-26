import AppKit
import SwiftUI
import WebKit

final class TerminalChannel {
    private let queue = DispatchQueue(label: "sshmanager.pty")
    private let pid: pid_t
    private let master: Int32
    private var reader: DispatchSourceRead?
    private var writer: DispatchSourceWrite?
    private var awaitingDrain = false
    private var pendingInput = Data()
    private let output: (Data) -> Void

    init(request: SSHRequest, noForwarding: Bool, columns: UInt16, rows: UInt16,
         output: @escaping (Data) -> Void, exited: @escaping (Int32) -> Void) throws {
        self.output = output
        var arguments = (["/usr/bin/ssh"] + request.arguments(noForwarding: noForwarding)).map { strdup($0) }
        var environment = request.environment(prompt: false).map { strdup("\($0.key)=\($0.value)") }
        defer { arguments.forEach { free($0) }; environment.forEach { free($0) } }
        guard !arguments.contains(nil), !environment.contains(nil) else { throw ManagerError("Недостаточно памяти для запуска OpenSSH.") }
        arguments.append(nil)
        environment.append(nil)
        var descriptor: Int32 = -1
        let child = sshm_spawn_pty(&descriptor, &arguments, &environment, FileManager.default.homeDirectoryForCurrentUser.path, columns, rows)
        guard child > 0 else { throw ManagerError("Не удалось запустить OpenSSH: \(String(cString: strerror(errno))).") }
        pid = child
        master = descriptor
        // Observe exit without reaping until close, so the PID cannot be reused while this channel owns it.
        let process = DispatchSource.makeProcessSource(identifier: child, eventMask: .exit, queue: queue)
        exitSource = process
        process.setEventHandler { [weak self] in self?.processExited = true }
        process.resume()
        self.exited = exited
        guard descriptor >= 0 else {
            queue.async { [self] in finish(signal: SIGHUP) }
            return
        }
        let source = DispatchSource.makeReadSource(fileDescriptor: master, queue: queue)
        reader = source
        source.setEventHandler { [weak self] in self?.read() }
        source.setCancelHandler { Darwin.close(descriptor) }
        source.resume()
    }

    private var exitSource: DispatchSourceProcess?
    private var processExited = false
    private var finished = false
    private var exited: ((Int32) -> Void)?

    func resize(columns: UInt16, rows: UInt16) {
        queue.async { [self] in
            guard reader != nil else { return }
            _ = sshm_resize_pty(master, columns, rows)
        }
    }

    func send(_ data: Data) {
        queue.async { [self] in
            guard reader != nil else { return }
            guard pendingInput.count + data.count <= 16 * 1024 * 1024 else {
                report(Data("\r\n[Слишком большой ввод]\r\n".utf8))
                return
            }
            pendingInput.append(data)
            flushInput()
        }
    }

    func drained() {
        queue.async { [self] in
            guard awaitingDrain, let reader else { return }
            awaitingDrain = false
            reader.resume()
        }
    }

    func close() { queue.async { [self] in finish(signal: SIGHUP) } }

    private func read() {
        guard let reader else { return }
        var bytes = [UInt8](repeating: 0, count: 32768)
        let count = Darwin.read(master, &bytes, bytes.count)
        if count > 0 {
            awaitingDrain = true
            reader.suspend()
            report(Data(bytes.prefix(count)))
        } else if count == 0 || (errno != EAGAIN && errno != EINTR) {
            finish(signal: nil)
        }
    }

    private func flushInput() {
        guard reader != nil else { return }
        while !pendingInput.isEmpty {
            let count = pendingInput.withUnsafeBytes { Darwin.write(master, $0.baseAddress, $0.count) }
            if count > 0 { pendingInput.removeFirst(count); continue }
            if count < 0 && errno == EINTR { continue }
            if count < 0 && errno == EAGAIN {
                if writer == nil {
                    let source = DispatchSource.makeWriteSource(fileDescriptor: master, queue: queue)
                    writer = source
                    source.setEventHandler { [weak self] in self?.flushInput() }
                    source.resume()
                }
                return
            }
            finish(signal: SIGHUP)
            return
        }
        writer?.cancel()
        writer = nil
    }

    private func report(_ data: Data) {
        DispatchQueue.main.async { [output] in output(data) }
    }

    private func finish(signal: Int32?) {
        guard !finished else { return }
        finished = true
        if let signal, !processExited { kill(pid, signal) }
        writer?.cancel()
        writer = nil
        reader?.cancel()
        if awaitingDrain { reader?.resume() }
        reader = nil
        awaitingDrain = false
        exitSource?.cancel()
        exitSource = nil
        let completion = exited
        exited = nil
        let child = pid
        DispatchQueue.global(qos: .utility).async {
            var status: Int32 = 0
            while waitpid(child, &status, 0) == -1 && errno == EINTR {}
            let result = status & 0x7f == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f)
            DispatchQueue.main.async { completion?(result) }
        }
    }
}

private final class TerminalWebView: WKWebView {
    var editCommand: ((String) -> Bool)?

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .control, .option, .shift])
        if modifiers == .command, let responder = window?.firstResponder as? NSView,
           responder === self || responder.isDescendant(of: self) {
            let typed = event.charactersIgnoringModifiers?.lowercased() ?? ""
            let key = typed.range(of: "^[a-z]$", options: .regularExpression) != nil ? typed :
                ([0: "a", 8: "c", 9: "v"][Int(event.keyCode)] ?? typed)
            if editCommand?(key) == true { return true }
        }
        return super.performKeyEquivalent(with: event)
    }
}

final class TerminalSession: NSObject, ObservableObject, WKScriptMessageHandler, WKNavigationDelegate {
    let id = UUID()
    let profile: Profile
    let noForwarding: Bool
    let number: Int
    let direct: Bool
    let remoteCommand: String?
    /// Called once SSH has exited, with its exit code (automatic reconnect).
    var onExit: ((Int32) -> Void)?
    var preferences = Preferences()
    @Published var name = ""
    @Published private(set) var exitCode: Int32?
    @Published private(set) var forwardingFailed = false
    private var inspected = 0
    private var recentOutput = ""
    private var channel: TerminalChannel?
    private(set) var view: WKWebView!
    private var bridge: TerminalMessageBridge!
    private var started = false
    private var closed = false
    private var renderBatch = 0
    private var rendering: Int?
    private var pending: [Data] = []
    private let source: URL
    private let showServers: (UUID) -> Void

    init(profile: Profile, noForwarding: Bool = false, number: Int = 1, direct: Bool = false, remoteCommand: String? = nil,
         preferences: Preferences = Preferences(), showServers: @escaping (UUID) -> Void) {
        self.profile = profile
        self.noForwarding = noForwarding
        self.number = number
        self.direct = direct
        self.remoteCommand = remoteCommand
        self.preferences = preferences
        self.showServers = showServers
        source = Bundle.main.resourceURL!.appendingPathComponent("Terminal/index.html").absoluteURL.standardizedFileURL
        super.init()
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        bridge = TerminalMessageBridge(session: self)
        configuration.userContentController.add(bridge, name: "terminal")
        let terminalView = TerminalWebView(frame: .zero, configuration: configuration)
        terminalView.editCommand = { [weak self] key in self?.edit(key) ?? false }
        view = terminalView
        view.underPageBackgroundColor = NSColor(red: 17.0 / 255, green: 26.0 / 255, blue: 39.0 / 255, alpha: 1)
        view.navigationDelegate = self
        view.loadFileURL(source, allowingReadAccessTo: source.deletingLastPathComponent())
    }

    deinit { channel?.close() }

    /// alias, alias · N for later sessions of the same server, alias · label once renamed (docs/ui-spec.md §3.2).
    var title: String {
        if !name.isEmpty { return "\(profile.alias) · \(name)" }
        return number <= 1 ? profile.alias : "\(profile.alias) · \(number)"
    }

    var live: Bool { exitCode == nil }

    /// user@host:port with IPv6 hosts in brackets.
    var address: String {
        var host = profile.host.isEmpty ? profile.alias : profile.host
        if !profile.port.isEmpty, host.contains(":") { host = "[\(host)]" }
        return (profile.user.isEmpty ? "" : profile.user + "@") + host + (profile.port.isEmpty ? "" : ":" + profile.port)
    }

    func close() {
        closed = true
        channel?.close()
        channel = nil
    }

    func focus() {
        if let window = view.window, window.isKeyWindow { window.makeFirstResponder(view) }
        view.evaluateJavaScript("window.focusTerminal?.()")
    }

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        decisionHandler(action.targetFrame?.isMainFrame == true && action.request.url?.standardizedFileURL == source ? .allow : .cancel)
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        close()
        exitCode = 1
    }

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard !closed, message.frameInfo.isMainFrame, let payload = message.body as? [String: Any],
              let type = payload["type"] as? String else { return }
        if type == "ready" || type == "resize" {
            guard let cols = payload["cols"] as? Int, let rows = payload["rows"] as? Int,
                  (1...1000).contains(cols), (1...1000).contains(rows) else { return }
            if type == "ready" { apply(preferences); start(columns: UInt16(cols), rows: UInt16(rows)) }
            else { channel?.resize(columns: UInt16(cols), rows: UInt16(rows)) }
        } else if type == "input", let text = payload["text"] as? String {
            channel?.send(Data(text.utf8))
        } else if type == "binary", let text = payload["text"] as? String {
            channel?.send(Data(text.unicodeScalars.map { UInt8(truncatingIfNeeded: $0.value) }))
        } else if type == "drained", let batch = payload["batch"] as? Int, rendering == batch {
            rendering = nil
            if pending.isEmpty { channel?.drained() }
            else { renderNext() }
        } else if type == "copy", let text = payload["text"] as? String, !text.isEmpty {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        } else if type == "paste" {
            paste()
        } else if type == "showServers" {
            showServers(id)
        }
    }

    /// Terminal font, size and scrollback from «Настройки».
    func apply(_ preferences: Preferences) {
        self.preferences = preferences
        let family = preferences.terminalFont.isEmpty ? "ui-monospace, Menlo, monospace" : "\"\(preferences.terminalFont)\", monospace"
        view.callAsyncJavaScript("window.configureTerminal?.(family, size, scrollback)",
                                 arguments: ["family": family, "size": preferences.terminalSize, "scrollback": preferences.scrollback],
                                 in: nil, in: .page, completionHandler: nil)
    }

    /// Sends a saved command followed by Enter.
    func send(_ text: String) {
        guard !closed, exitCode == nil, let channel else { return }
        let command = text.replacingOccurrences(of: "\r\n", with: "\r").replacingOccurrences(of: "\n", with: "\r") + "\r"
        channel.send(Data(command.utf8))
    }

    private func edit(_ key: String) -> Bool {
        switch key {
        case "c": view.evaluateJavaScript("window.copyTerminal?.()")
        case "v": paste()
        case "a": view.evaluateJavaScript("window.selectTerminal?.()")
        default: return false
        }
        return true
    }

    private func paste() {
        guard !closed, exitCode == nil, let target = channel,
              let clipboard = NSPasteboard.general.string(forType: .string) else { return }
        let text = clipboard.replacingOccurrences(of: #"(?:\x1b\[|\x9b)[0-?]*[ -/]*[@-~]|\^\[\[20[01]~"#,
                                                  with: "", options: .regularExpression)
            .replacingOccurrences(of: "\r\n", with: "\r").replacingOccurrences(of: "\n", with: "\r")
            .replacingOccurrences(of: #"[\x00-\x08\x0a-\x0c\x0e-\x1f\x7f-\x9f]"#, with: "", options: .regularExpression)
        guard !text.isEmpty else { return }
        if text.contains("\r") {
            let alert = NSAlert()
            alert.messageText = "Вставка в терминал"
            alert.informativeText = "Текст содержит переносы строк. Вставка может сразу выполнить команды. Продолжить?"
            alert.addButton(withTitle: "Отмена")
            alert.addButton(withTitle: "Вставить")
            guard alert.runModal() == .alertSecondButtonReturn else { return }
        }
        if !closed && exitCode == nil && channel === target { target.send(Data(text.utf8)) }
    }

    private func start(columns: UInt16, rows: UInt16) {
        guard !started, !closed else { return }
        started = true
        focus()
        do {
            channel = try TerminalChannel(request: SSHRequest(profile: profile, direct: direct, remoteCommand: remoteCommand),
                                          noForwarding: noForwarding,
                                          columns: columns, rows: rows, output: { [weak self] in self?.render($0) },
                                          exited: { [weak self] code in
                guard let self, !self.closed else { return }
                self.exitCode = code
                self.render(Data("\r\n\u{1b}[0mSSH завершился (код \(code)).\r\n".utf8))
                self.onExit?(code)
            })
        } catch {
            exitCode = 1
            render(Data("\r\n\(error.localizedDescription)\r\n".utf8))
        }
    }

    private func render(_ data: Data) {
        guard !closed else { return }
        inspect(data)
        pending.append(data)
        if rendering == nil { renderNext() }
    }

    /// Notices OpenSSH's port forwarding errors in the first output, like the Windows app.
    private func inspect(_ data: Data) {
        guard !forwardingFailed, !noForwarding, inspected < 65_536 else { return }
        inspected += data.count
        let text = recentOutput + String(decoding: data, as: UTF8.self)
        let plain = text.replacingOccurrences(of: #"\x1b\[[0-?]*[ -/]*[@-~]"#, with: "", options: .regularExpression)
        if plain.range(of: #"cannot\s*listen\s*to\s*port\s*:|could\s*not\s*request\s*local\s*forwarding"#,
                       options: [.regularExpression, .caseInsensitive]) != nil {
            forwardingFailed = true
        }
        recentOutput = String(text.suffix(160))
    }

    private func renderNext() {
        guard !pending.isEmpty else { return }
        let data = pending.removeFirst()
        renderBatch += 1
        rendering = renderBatch
        let batch = renderBatch
        Task { @MainActor [weak self] in
            guard let self, !self.closed else { return }
            do {
                _ = try await self.view.callAsyncJavaScript("window.receive(data, batch);",
                    arguments: ["data": data.base64EncodedString(), "batch": batch], in: nil, contentWorld: .page)
            } catch { self.close(); self.exitCode = 1 }
        }
    }
}

private final class TerminalMessageBridge: NSObject, WKScriptMessageHandler {
    weak var session: TerminalSession?
    init(session: TerminalSession) { self.session = session }
    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        session?.userContentController(controller, didReceive: message)
    }
}

struct TerminalView: NSViewRepresentable {
    let session: TerminalSession
    func makeNSView(context: Context) -> WKWebView {
        let view = session.view!
        DispatchQueue.main.async { session.focus() }
        return view
    }
    func updateNSView(_ view: WKWebView, context: Context) {}
}
