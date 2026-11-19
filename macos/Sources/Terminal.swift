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

final class TerminalSession: NSObject, ObservableObject, WKScriptMessageHandler, WKNavigationDelegate {
    let id = UUID()
    let profile: Profile
    let noForwarding: Bool
    @Published var label: String
    @Published private(set) var exitCode: Int32?
    private var channel: TerminalChannel?
    private(set) var view: WKWebView!
    private var bridge: TerminalMessageBridge!
    private var started = false
    private var closed = false
    private var renderBatch = 0
    private var rendering: Int?
    private var pending: [Data] = []
    private let source: URL

    init(profile: Profile, noForwarding: Bool = false) {
        self.profile = profile
        self.noForwarding = noForwarding
        label = profile.alias
        source = Bundle.main.resourceURL!.appendingPathComponent("Terminal/index.html").absoluteURL.standardizedFileURL
        super.init()
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        bridge = TerminalMessageBridge(session: self)
        configuration.userContentController.add(bridge, name: "terminal")
        view = WKWebView(frame: .zero, configuration: configuration)
        view.underPageBackgroundColor = NSColor(red: 18.0 / 255, green: 24.0 / 255, blue: 35.0 / 255, alpha: 1)
        view.navigationDelegate = self
        view.loadFileURL(source, allowingReadAccessTo: source.deletingLastPathComponent())
    }

    deinit { channel?.close() }

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
            if type == "ready" { start(columns: UInt16(cols), rows: UInt16(rows)) }
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
        } else if type == "paste", let text = NSPasteboard.general.string(forType: .string) {
            Task { @MainActor [weak self] in
                _ = try? await self?.view.callAsyncJavaScript("window.pasteTerminal(text);", arguments: ["text": text], in: nil, contentWorld: .page)
            }
        }
    }

    private func start(columns: UInt16, rows: UInt16) {
        guard !started, !closed else { return }
        started = true
        focus()
        do {
            channel = try TerminalChannel(request: SSHRequest(profile: profile), noForwarding: noForwarding,
                                          columns: columns, rows: rows, output: { [weak self] in self?.render($0) },
                                          exited: { [weak self] code in
                guard let self, !self.closed else { return }
                self.exitCode = code
                self.render(Data("\r\n[Сеанс завершён, код \(code)]\r\n".utf8))
            })
        } catch {
            exitCode = 1
            render(Data("\r\n\(error.localizedDescription)\r\n".utf8))
        }
    }

    private func render(_ data: Data) {
        guard !closed else { return }
        pending.append(data)
        if rendering == nil { renderNext() }
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
