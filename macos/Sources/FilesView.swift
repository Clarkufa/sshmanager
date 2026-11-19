import AppKit
import SwiftUI
import UniformTypeIdentifiers

private final class Cancellation {
    private let lock = NSLock()
    private var cancelled = false
    func cancel() { lock.lock(); cancelled = true; lock.unlock() }
    func check() throws {
        lock.lock()
        defer { lock.unlock() }
        if cancelled { throw CancellationError() }
    }
}

final class FilesController: ObservableObject {
    let profile: Profile
    @Published var local = FileManager.default.homeDirectoryForCurrentUser.path
    @Published var remote = "."
    @Published var localItems: [FileRow] = []
    @Published var remoteItems: [FileRow] = []
    @Published var localSelection = Set<String>()
    @Published var remoteSelection = Set<String>()
    @Published var collision: Collision = .keepBoth
    @Published var busy = false
    @Published var online = false
    @Published var transfer: String?
    @Published var error: String?

    private var connection: SFTPConnection?
    private var cancellation: Cancellation?
    private let queue = DispatchQueue(label: "sshmanager.sftp", qos: .userInitiated)
    private let home = FileManager.default.homeDirectoryForCurrentUser

    init(profile: Profile) {
        self.profile = profile
        loadLocal(local)
    }

    func connect() {
        guard !busy else { return }
        let client = SFTPConnection(request: SSHRequest(profile: profile))
        connection = client
        run(recoverConnection: true) { _ in
            try client.open()
            let directory = try client.path(".")
            let items = try client.list(directory)
            return { model in
                model.online = true
                model.showRemote(directory, items: items)
            }
        }
    }

    func close() {
        cancellation?.cancel()
        connection?.interrupt()
        connection = nil
        cancellation = nil
        online = false
    }

    func cancelTransfer() { cancellation?.cancel() }

    func loadLocal(_ path: String) {
        do {
            let url = localURL(path)
            let items = try LocalFiles.list(url)
            local = url.path
            localItems = sorted(items)
            localSelection.removeAll()
        } catch { self.error = error.localizedDescription }
    }

    func selectLocalFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.directoryURL = localURL(local)
        if panel.runModal() == .OK, let url = panel.url { loadLocal(url.path) }
    }

    func upLocal() { loadLocal(localURL(local).deletingLastPathComponent().path) }

    func loadRemote(_ path: String) {
        guard let client = connection, online else { return }
        run { _ in
            let directory = try client.path(path)
            let items = try client.list(directory)
            return { $0.showRemote(directory, items: items) }
        }
    }

    func open(_ row: FileRow, remote side: Bool) {
        guard row.kind == .directory else { return }
        if side { loadRemote(remotePath(remote, row.name)) }
        else { loadLocal(localURL(local).appendingPathComponent(row.name).path) }
    }

    func transferFiles(upload: Bool, dropped: [URL]? = nil) {
        guard let client = connection, online, !busy else { return }
        let localDirectory = localURL(local)
        let remoteDirectory = remote
        let selected = remoteItems.filter { remoteSelection.contains($0.id) }
        let sources = dropped ?? localItems.filter { localSelection.contains($0.id) }.map { localDirectory.appendingPathComponent($0.name) }
        let choice = collision
        transfer = "Подготовка передачи…"
        run { [weak self] token in
            var last = Date.distantPast
            let copy = FileTransfer(client, collision: choice, check: token.check) { name, bytes in
                guard Date().timeIntervalSince(last) >= 0.12 else { return }
                last = Date()
                let size = ByteCountFormatter.string(fromByteCount: Int64(clamping: bytes), countStyle: .file)
                DispatchQueue.main.async { [weak self] in self?.transfer = "\(name) · \(size)" }
            }
            var failed: Error?
            do {
                if upload {
                    for url in sources { try copy.upload(url, to: remoteDirectory) }
                } else {
                    for row in selected { try copy.download(row, from: remoteDirectory, to: localDirectory) }
                }
            } catch { failed = error }
            let items = upload ? try? client.list(remoteDirectory) : nil
            return { model in
                if let items { model.showRemote(remoteDirectory, items: items) }
                if !upload { model.loadLocal(localDirectory.path) }
                if let failed, !(failed is CancellationError) { model.error = failed.localizedDescription }
            }
        }
    }

    func changeRemote(name: String, action: RemoteAction) {
        guard let client = connection, online, !busy else { return }
        let folder = remote
        if !SFTPConnection.valid(name) { error = "Введите имя файла или папки без косых черт."; return }
        run { _ in
            let destination = remotePath(folder, name)
            switch action {
            case .create: try client.createDirectory(destination)
            case .rename(let old):
                guard name != old else { return { _ in } }
                guard try client.stat(destination) == nil else { throw ManagerError("Элемент с таким именем уже существует.") }
                try client.rename(remotePath(folder, old), to: destination)
            case .delete(let directory): try client.remove(destination, directory: directory)
            }
            let items = try client.list(folder)
            return { $0.showRemote(folder, items: items) }
        }
    }

    enum RemoteAction { case create, rename(String), delete(Bool) }

    private func run(recoverConnection: Bool = false, _ task: @escaping (Cancellation) throws -> (FilesController) -> Void) {
        guard !busy else { return }
        busy = true
        let token = Cancellation()
        cancellation = token
        queue.async { [weak self] in
            let result = Result { try task(token) }
            DispatchQueue.main.async { [weak self] in
                guard let self, self.cancellation === token else { return }
                self.busy = false
                self.cancellation = nil
                self.transfer = nil
                switch result {
                case .success(let apply): apply(self)
                case .failure(let error):
                    if recoverConnection { self.close() }
                    if !(error is CancellationError) { self.error = error.localizedDescription }
                }
                if self.connection?.isRunning == false { self.close() }
            }
        }
    }

    private func showRemote(_ path: String, items: [FileRow]) {
        remote = path
        remoteItems = sorted(items)
        remoteSelection.removeAll()
    }

    private func localURL(_ path: String) -> URL {
        let expanded = (path as NSString).expandingTildeInPath
        if expanded.hasPrefix("/") { return URL(fileURLWithPath: expanded).standardizedFileURL }
        return home.appendingPathComponent(expanded).standardizedFileURL
    }

    private func sorted(_ rows: [FileRow]) -> [FileRow] {
        rows.sorted {
            if ($0.kind == .directory) != ($1.kind == .directory) { return $0.kind == .directory }
            return $0.name.localizedStandardCompare($1.name) == .orderedAscending
        }
    }
}

final class FilesWindow: NSWindowController, NSWindowDelegate {
    private let files: FilesController
    private let didClose: () -> Void

    init(profile: Profile, didClose: @escaping () -> Void) {
        files = FilesController(profile: profile)
        self.didClose = didClose
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1040, height: 620),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Файлы · " + profile.alias
        window.minSize = NSSize(width: 760, height: 440)
        window.isReleasedWhenClosed = false
        window.contentView = NSHostingView(rootView: FilesView(files: files))
        super.init(window: window)
        window.delegate = self
        window.center()
        files.connect()
    }

    required init?(coder: NSCoder) { nil }

    func windowWillClose(_ notification: Notification) {
        files.close()
        didClose()
    }
}

private struct FilesView: View {
    @ObservedObject var files: FilesController
    @State private var localPath = ""
    @State private var remotePathText = ""
    @State private var prompt: FileRow?
    @State private var promptOpen = false
    @State private var name = ""
    @State private var removing: FileRow?

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .center, spacing: 14) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(files.profile.alias).font(.title2.weight(.semibold))
                    Text(files.profile.address).font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
                }
                Spacer()
                Picker("При совпадении имён", selection: $files.collision) {
                    ForEach(Collision.allCases) { Text($0.rawValue).tag($0) }
                }.fixedSize()
                Button { files.transferFiles(upload: true) } label: { Label("На сервер", systemImage: "arrow.right") }
                    .disabled(!files.online || files.busy || files.localSelection.isEmpty)
                Button { files.transferFiles(upload: false) } label: { Label("На Mac", systemImage: "arrow.left") }
                    .disabled(!files.online || files.busy || files.remoteSelection.isEmpty)
            }.padding(16)
            Divider()
            HStack(spacing: 0) {
                pane("Этот Mac", remote: false, path: $localPath, rows: files.localItems, selection: $files.localSelection)
                Divider()
                pane("Сервер", remote: true, path: $remotePathText, rows: files.remoteItems, selection: $files.remoteSelection)
                    .dropDestination(for: URL.self) { urls, _ in
                        let filesOnly = urls.filter(\.isFileURL)
                        guard files.online, !files.busy, !filesOnly.isEmpty else { return false }
                        files.transferFiles(upload: true, dropped: filesOnly)
                        return true
                    }
            }
            Divider()
            HStack(spacing: 8) {
                if files.busy { ProgressView().controlSize(.small) }
                Text(files.transfer ?? (files.online ? (files.busy ? "Загрузка…" : "Подключено по SFTP") : (files.busy ? "Подключение…" : "SFTP отключён")))
                    .lineLimit(1)
                Spacer()
                if files.transfer != nil { Button("Отменить передачу") { files.cancelTransfer() } }
                if !files.online && !files.busy { Button("Подключиться") { files.connect() } }
            }.font(.callout).foregroundStyle(.secondary).padding(.horizontal, 16).frame(minHeight: 40)
        }
        .onAppear { localPath = files.local; remotePathText = files.remote }
        .onChange(of: files.local) { _, path in localPath = path }
        .onChange(of: files.remote) { _, path in remotePathText = path }
        .alert("Ошибка SFTP", isPresented: Binding(get: { files.error != nil }, set: { if !$0 { files.error = nil } })) {
            Button("ОК", role: .cancel) { files.error = nil }
        } message: { Text(files.error ?? "") }
        .alert(prompt == nil ? "Новая папка" : "Переименовать", isPresented: $promptOpen) {
            TextField("Имя", text: $name)
            Button("Отмена", role: .cancel) {}
            Button("Сохранить") { files.changeRemote(name: name, action: prompt.map { .rename($0.name) } ?? .create) }
        }
        .confirmationDialog("Удалить «\(removing?.name ?? "")»?", isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } }), titleVisibility: .visible) {
            Button("Удалить", role: .destructive) {
                if let removing { files.changeRemote(name: removing.name, action: .delete(removing.kind == .directory)) }
                removing = nil
            }
        } message: { Text("Элемент будет удалён с сервера.") }
    }

    private func pane(_ title: String, remote: Bool, path: Binding<String>, rows: [FileRow], selection: Binding<Set<String>>) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Label(title, systemImage: remote ? "server.rack" : "desktopcomputer").font(.headline)
                Spacer()
                if remote {
                    Button { prompt = nil; name = ""; promptOpen = true } label: { Image(systemName: "folder.badge.plus") }
                        .help("Новая папка").accessibilityLabel("Новая папка")
                } else {
                    Button { files.selectLocalFolder() } label: { Image(systemName: "folder") }
                        .help("Выбрать папку").accessibilityLabel("Выбрать папку")
                }
            }.padding(.horizontal, 12).padding(.vertical, 10)
            HStack(spacing: 8) {
                Button {
                    if remote { files.loadRemote(remotePath(files.remote, "..")) } else { files.upLocal() }
                } label: { Image(systemName: "arrow.up") }.help("Родительская папка").accessibilityLabel("Родительская папка")
                TextField("Путь к папке", text: path).font(.system(.callout, design: .monospaced))
                    .onSubmit { if remote { files.loadRemote(path.wrappedValue) } else { files.loadLocal(path.wrappedValue) } }
                Button {
                    if remote { files.loadRemote(files.remote) } else { files.loadLocal(files.local) }
                } label: { Image(systemName: "arrow.clockwise") }.help("Обновить").accessibilityLabel("Обновить")
            }.padding(.horizontal, 12).padding(.bottom, 10)
            Divider()
            List(rows, selection: selection) { row in
                HStack(spacing: 8) {
                    Image(systemName: icon(row.kind)).foregroundStyle(row.kind == .directory ? Color.accentColor : .secondary)
                        .frame(width: 16).accessibilityHidden(true)
                    Text(row.name).lineLimit(1)
                    Spacer()
                    if row.kind == .file, let size = row.size {
                        Text(ByteCountFormatter.string(fromByteCount: Int64(clamping: size), countStyle: .file))
                            .font(.caption).foregroundStyle(.secondary).monospacedDigit()
                    }
                }
                .contentShape(Rectangle())
                .onTapGesture(count: 2) { files.open(row, remote: remote) }
                .contextMenu {
                    if row.kind == .directory { Button("Открыть") { files.open(row, remote: remote) } }
                    if remote {
                        Button("Переименовать") { prompt = row; name = row.name; promptOpen = true }
                        Button("Удалить", role: .destructive) { removing = row }
                    }
                }
                .tag(row.id)
            }.listStyle(.inset(alternatesRowBackgrounds: true))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .disabled(files.busy || (remote && !files.online))
    }

    private func icon(_ kind: FileRow.Kind) -> String {
        switch kind {
        case .directory: "folder.fill"
        case .link: "link"
        case .file, .other: "doc"
        }
    }
}
