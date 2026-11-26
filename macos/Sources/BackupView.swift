import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// «Защита резервной копии» / «Пароль резервной копии» (docs/ui-spec.md §4.6).
struct BackupPasswordRequest: Identifiable {
    let id = UUID()
    let creating: Bool
    let completion: (String) -> Void
}

struct BackupPasswordSheet: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme
    let request: BackupPasswordRequest
    @State private var password = ""
    @State private var confirmation = ""
    @State private var failure: String?

    var body: some View {
        let palette = Palette.of(scheme)
        VStack(alignment: .leading, spacing: 10) {
            Text(request.creating ? "Защита резервной копии" : "Пароль резервной копии").font(.title3.weight(.semibold))
            Text("Пароль")
            SecureField("", text: $password).textFieldStyle(.roundedBorder)
            if request.creating {
                Text("Повторите пароль")
                SecureField("", text: $confirmation).textFieldStyle(.roundedBorder)
                Text("Защищённая копия включает сохранённые пароли SSH и RDP.").font(.callout).foregroundStyle(palette.muted)
            }
            if let failure { Text(failure).font(.callout).foregroundStyle(palette.danger) }
            HStack {
                Spacer()
                Button("Отмена") { dismiss() }.keyboardShortcut(.cancelAction).winButton(.outline)
                Button(request.creating ? "Сохранить" : "Продолжить") {
                    guard !password.isEmpty else { failure = "Введите пароль."; return }
                    guard !request.creating || password == confirmation else { failure = "Пароли не совпадают."; return }
                    let secret = password
                    let completion = request.completion
                    password = ""
                    confirmation = ""
                    dismiss()
                    DispatchQueue.main.async { completion(secret) }
                }.keyboardShortcut(.defaultAction).winButton(.primary)
            }
        }.padding(20).frame(width: 460)
    }
}

/// «Экспорт архива…» / «Импорт архива…» with the Windows file types, messages and the summary before restoring.
extension Manager {
    private static let protectedType = UTType(filenameExtension: "sshbackup") ?? .data

    func exportBackup() {
        let panel = NSSavePanel()
        panel.title = "Экспорт резервной копии"
        let formats = BackupFormatSwitcher(panel: panel)
        panel.accessoryView = formats.view
        panel.allowedContentTypes = [Self.protectedType]
        let stamp = DateFormatter()
        stamp.dateFormat = "yyyyMMdd-HHmmss"
        panel.nameFieldStringValue = "ssh-manager-" + stamp.string(from: Date()) + ".sshbackup"
        guard panel.runModal() == .OK, let destination = panel.url else { return }
        let protect = destination.pathExtension.lowercased() == "sshbackup"
        if protect {
            backupPassword = BackupPasswordRequest(creating: true) { [weak self] secret in self?.writeBackup(destination, password: secret) }
        } else {
            writeBackup(destination, password: "")
        }
    }

    private func writeBackup(_ destination: URL, password: String) {
        runBackground {
            let settings = try ManagerSettings.load()
            let snapshot = BackupSnapshot(config: .standard, settings: settings)
            try snapshot.collect(passwords: !password.isEmpty)
            try writePrivate(try BackupArchive.pack(snapshot.entries, password: password), to: destination)
            return snapshot.omissions.count
        } success: { [weak self] omitted in
            var text = "Резервная копия сохранена.\n\n" + destination.path
            if omitted > 0 { text += "\n\nСсылок на файлы, которые не удалось включить: \(omitted)" }
            text += password.isEmpty ? "\n\nZIP сохраняет подключения без паролей SSH и RDP." : "\n\nЗащищённая копия включает сохранённые пароли SSH и RDP."
            self?.notice = Notice(title: "Экспорт резервной копии", text: text)
        }
    }

    func importBackup() {
        let panel = NSOpenPanel()
        panel.title = "Импорт резервной копии"
        panel.allowedContentTypes = [Self.protectedType, .zip]
        guard panel.runModal() == .OK, let source = panel.url else { return }
        runBackground { try BackupArchive.readFile(source, limit: BackupArchive.maxArchive) } success: { [weak self] archive in
            guard let self else { return }
            if archive.starts(with: BackupArchive.magic) {
                self.backupPassword = BackupPasswordRequest(creating: false) { [weak self] secret in self?.prepareRestore(archive, password: secret) }
            } else {
                self.prepareRestore(archive, password: "")
            }
        }
    }

    private func prepareRestore(_ archive: Data, password: String) {
        runBackground {
            try BackupRestore(entries: try BackupArchive.unpack(archive, password: password), config: .standard)
        } success: { [weak self] plan in
            guard let self else { return }
            if plan.changedCount == 0 && plan.passwordCount == 0 {
                self.notice = Notice(title: "Импорт резервной копии", text: "Данные из резервной копии уже восстановлены.")
                return
            }
            let replaced = plan.changedReplacements
            var summary = "Импортировать резервную копию?\n\nНовых файлов: \(plan.changedCount - replaced.count)\nЗаменяемых файлов: \(replaced.count)"
            let ssh = plan.passwordCount - plan.desktopPasswordCount
            if ssh > 0 { summary += "\nSSH-паролей: \(ssh)" }
            if plan.desktopPasswordCount > 0 { summary += "\nRDP-паролей: \(plan.desktopPasswordCount)" }
            if !replaced.isEmpty {
                summary += "\n\nПредыдущие версии этих файлов будут сохранены:\n" + replaced.prefix(7).joined(separator: "\n")
                if replaced.count > 7 { summary += "\nИ ещё \(replaced.count - 7)" }
            }
            self.notice = Notice(title: "Импорт резервной копии", text: summary) { [weak self] in self?.applyRestore(plan) }
        }
    }

    private func applyRestore(_ plan: BackupRestore) {
        runBackground { try plan.apply() } success: { [weak self] _ in
            guard let self else { return }
            self.reloadSettings()
            self.reload()
            var text = "Резервная копия восстановлена."
            if plan.passwordCount > 0 {
                text += "\nВосстановлено паролей SSH: \(plan.passwordCount - plan.desktopPasswordCount), RDP: \(plan.desktopPasswordCount)"
            }
            if !plan.changedReplacements.isEmpty { text += "\n\nПредыдущие файлы сохранены здесь:\n" + plan.recoveryURL.path }
            self.notice = Notice(title: "Импорт резервной копии", text: text)
        }
    }

    private func runBackground<T>(_ operation: @escaping () throws -> T, success: @escaping (T) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Result { try operation() }
            DispatchQueue.main.async { [weak self] in
                switch result {
                case .success(let value): success(value)
                case .failure(let error): self?.show(error)
                }
            }
        }
    }
}

/// The «Формат» pop-up under the save panel: «Защищённая копия (*.sshbackup)» or «Архив ZIP (*.zip)».
private final class BackupFormatSwitcher: NSObject {
    private static var current: BackupFormatSwitcher?
    private weak var panel: NSSavePanel?
    let view: NSView

    init(panel: NSSavePanel) {
        self.panel = panel
        let popup = NSPopUpButton()
        popup.addItems(withTitles: ["Защищённая копия (*.sshbackup)", "Архив ZIP (*.zip)"])
        let label = NSTextField(labelWithString: "Формат:")
        let stack = NSStackView(views: [label, popup])
        stack.edgeInsets = NSEdgeInsets(top: 8, left: 12, bottom: 8, right: 12)
        view = stack
        super.init()
        popup.target = self
        popup.action = #selector(changed(_:))
        Self.current = self
    }

    @objc private func changed(_ popup: NSPopUpButton) {
        guard let panel else { return }
        let protect = popup.indexOfSelectedItem == 0
        panel.allowedContentTypes = [protect ? (UTType(filenameExtension: "sshbackup") ?? .data) : .zip]
        let name = (panel.nameFieldStringValue as NSString).deletingPathExtension
        panel.nameFieldStringValue = name + (protect ? ".sshbackup" : ".zip")
    }
}
