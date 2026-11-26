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
        transfer = "Подготовка передачи…"
        run { [weak self] token in
            var last = Date.distantPast
            let copy = FileTransfer(client, decide: Self.askCollision, check: token.check) { name, bytes in
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

    /// «Файл уже существует»: «Сохранить оба» (default), «Заменить», «Пропустить» or «Отмена», asked on the main thread.
    private static func askCollision(_ path: String) throws -> Collision {
        let answer: Collision? = DispatchQueue.main.sync {
            let alert = NSAlert()
            alert.messageText = "Файл уже существует"
            alert.informativeText = "Элемент с таким именем уже есть:\n" + path
            for title in ["Сохранить оба", "Заменить", "Пропустить", "Отмена"] { alert.addButton(withTitle: title) }
            switch alert.runModal() {
            case .alertFirstButtonReturn: return .keepBoth
            case .alertSecondButtonReturn: return .replace
            case .alertThirdButtonReturn: return .skip
            default: return nil
            }
        }
        guard let answer else { throw CancellationError() }
        return answer
    }

    func reconnect() {
        guard !busy else { return }
        close()
        connect()
    }

    var transferring: Bool { transfer != nil }

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

/// «Файлы · сервер» in the workspace (docs/ui-spec.md §3.2.1).
struct FilesView: View {
    @ObservedObject var files: FilesController
    var onClose: () -> Void
    @Environment(\.colorScheme) private var scheme
    @State private var localPath = ""
    @State private var remotePathText = ""
    @State private var prompt: FileRow?
    @State private var promptOpen = false
    @State private var name = ""
    @State private var removing: FileRow?

    init(files: FilesController, onClose: @escaping () -> Void) {
        self.files = files
        self.onClose = onClose
    }

    private static let dates: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "dd.MM.yy HH:mm"
        return formatter
    }()

    var body: some View {
        let palette = Palette.of(scheme)
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Text("Файлы · " + files.profile.alias).font(.system(size: 13, weight: .semibold)).lineLimit(1)
                Spacer(minLength: 12)
                Button { files.reconnect() } label: { GlyphLabel(title: "Переподключить", glyph: .refresh) }
                    .winButton(.outline).disabled(files.busy)
                Button { onClose() } label: { GlyphLabel(title: "Закрыть", glyph: nil, danger: true) }
                    .winButton(.quiet, danger: true)
            }
            .frame(height: 44).padding(.bottom, 6)
            HStack(alignment: .top, spacing: 16) {
                pane(remote: false, path: $localPath, rows: files.localItems, selection: $files.localSelection, palette: palette)
                pane(remote: true, path: $remotePathText, rows: files.remoteItems, selection: $files.remoteSelection, palette: palette)
                    .dropDestination(for: URL.self) { urls, _ in
                        let filesOnly = urls.filter(\.isFileURL)
                        guard files.online, !files.busy, !filesOnly.isEmpty else { return false }
                        files.transferFiles(upload: true, dropped: filesOnly)
                        return true
                    }
            }
            HStack(spacing: 8) {
                Button { files.transferFiles(upload: true) } label: { GlyphLabel(title: "На сервер", glyph: .move, kind: .primary) }
                    .winButton(.primary).disabled(!files.online || files.busy || files.localSelection.isEmpty)
                Button { files.transferFiles(upload: false) } label: { GlyphLabel(title: "На компьютер", glyph: .moveLeft) }
                    .winButton(.outline).disabled(!files.online || files.busy || files.remoteSelection.isEmpty)
                Button("Отменить") { files.cancelTransfer() }.winButton(.outline).disabled(!files.transferring)
                if files.busy { ProgressView().controlSize(.small).padding(.leading, 6) }
                Text(status).foregroundStyle(palette.muted).lineLimit(1).truncationMode(.middle)
                Spacer()
            }
            .frame(height: 56)
        }
        .background(palette.canvas)
        .foregroundStyle(palette.ink)
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

    private var status: String {
        if let transfer = files.transfer { return transfer }
        if files.online { return files.busy ? "Загрузка…" : "" }
        return files.busy ? "Подключение…" : "SFTP отключён. Нажмите «Переподключить»."
    }

    private func pane(remote: Bool, path: Binding<String>, rows: [FileRow], selection: Binding<Set<String>>, palette: Palette) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(remote ? files.profile.address : "Компьютер").font(.system(size: 13, weight: .semibold)).lineLimit(1)
            TextField("", text: path)
                .textFieldStyle(.roundedBorder)
                .onSubmit { if remote { files.loadRemote(path.wrappedValue) } else { files.loadLocal(path.wrappedValue) } }
                .accessibilityLabel(remote ? "Путь на сервере" : "Путь на компьютере")
            HStack(spacing: 4) {
                Button {
                    if remote { files.loadRemote(remotePath(files.remote, "..")) } else { files.upLocal() }
                } label: { GlyphLabel(title: "Вверх", glyph: .moveLeft) }.winButton(.quiet)
                Button {
                    if remote { files.loadRemote(files.remote) } else { files.loadLocal(files.local) }
                } label: { GlyphLabel(title: "Обновить", glyph: .refresh) }.winButton(.quiet)
                if remote {
                    Button { prompt = nil; name = ""; promptOpen = true } label: { GlyphLabel(title: "Новая папка", glyph: .plus) }.winButton(.quiet)
                } else {
                    Button { files.selectLocalFolder() } label: { GlyphLabel(title: "Выбрать папку…", glyph: .browse) }.winButton(.quiet)
                }
            }
            Table(rows, selection: selection) {
                TableColumn("Имя") { row in
                    HStack(spacing: 8) {
                        GlyphView(glyph: row.kind == .directory ? .folder : .copy, color: row.kind == .directory ? palette.teal : palette.muted)
                        Text(row.name).lineLimit(1)
                    }
                }
                TableColumn("Размер") { row in
                    Text(row.kind == .file ? row.size.map { ByteCountFormatter.string(fromByteCount: Int64(clamping: $0), countStyle: .file) } ?? "—" : "—")
                        .monospacedDigit()
                }.width(min: 70, ideal: 90, max: 130)
                TableColumn("Изменён") { row in
                    Text(row.date.map { Self.dates.string(from: $0) } ?? "—").monospacedDigit()
                }.width(min: 100, ideal: 120, max: 150)
            }
            .contextMenu(forSelectionType: String.self) { ids in
                if remote, let row = rows.first(where: { ids.count == 1 && ids.contains($0.id) }) {
                    Button("Скачать") { files.remoteSelection = [row.id]; files.transferFiles(upload: false) }
                    Button("Переименовать") { prompt = row; name = row.name; promptOpen = true }
                    Button("Удалить", role: .destructive) { removing = row }
                } else if remote, !ids.isEmpty {
                    Button("Скачать") { files.remoteSelection = ids; files.transferFiles(upload: false) }
                }
            } primaryAction: { ids in
                if ids.count == 1, let row = rows.first(where: { ids.contains($0.id) }) { files.open(row, remote: remote) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .disabled(files.busy || (remote && !files.online))
    }
}
