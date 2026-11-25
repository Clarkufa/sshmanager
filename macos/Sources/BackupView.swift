import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct BackupView: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var manager: Manager
    @State private var password = ""
    @State private var confirmation = ""
    @State private var protected = true
    @State private var busy = false
    @State private var input: Data?
    @State private var restore: BackupRestore?
    @State private var message: String?
    @State private var failure: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Резервные копии").font(.title2.weight(.semibold))
            if let restore { preview(restore) }
            else if input != nil { unlock }
            else { actions }
            if busy { ProgressView("Обработка архива…").controlSize(.small) }
            if let failure { Text(failure).font(.callout).foregroundStyle(.red).textSelection(.enabled) }
            if let message { Text(message).font(.callout).textSelection(.enabled) }
            HStack {
                Spacer()
                Button("Закрыть") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(22).frame(width: 520)
        .disabled(busy)
        .interactiveDismissDisabled(busy)
    }

    private var actions: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("В копию входят OpenSSH config, вкладки и доступные SSH-ключи. Защищённая копия также сохраняет пароли подключений.")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Toggle("Защитить архив паролем", isOn: $protected)
                .onChange(of: protected) { _, _ in clearSecrets() }
            if protected {
                SecureField("Пароль архива", text: $password)
                SecureField("Подтвердите пароль архива", text: $confirmation)
            } else {
                Text("Файл ZIP будет содержать SSH-ключи в открытом виде.")
                    .foregroundStyle(.orange)
            }
            HStack {
                Button("Экспортировать…", action: export)
                    .disabled(protected && (password.isEmpty || password != confirmation))
                Button("Импортировать…", action: chooseArchive)
            }
        }
    }

    private var unlock: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Введите пароль резервной копии.")
            SecureField("Пароль архива", text: $password)
            HStack {
                Button("Назад", action: reset)
                Button("Открыть архив") { if let input { prepare(input, password: password) } }
                    .keyboardShortcut(.defaultAction).disabled(password.isEmpty)
            }
        }
    }

    private func preview(_ plan: BackupRestore) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Файлов для восстановления: \(plan.fileCount). Паролей SSH: \(plan.passwordCount - plan.desktopPasswordCount), RDP: \(plan.desktopPasswordCount).")
            if !plan.replaced.isEmpty {
                Text("Эти файлы будут заменены. Предыдущие версии будут сохранены в папке восстановления.").foregroundStyle(.secondary)
                ScrollView {
                    VStack(alignment: .leading, spacing: 5) {
                        ForEach(plan.replaced, id: \.self) { path in
                            Text(path).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                        }
                    }.frame(maxWidth: .infinity, alignment: .leading)
                }.frame(height: min(125, CGFloat(plan.replaced.count) * 21))
            }
            HStack {
                Button("Назад", action: reset)
                Button("Восстановить", role: .destructive) { apply(plan) }
            }
        }
    }

    private func export() {
        let panel = NSSavePanel()
        let type = protected ? UTType(filenameExtension: "sshbackup") ?? .data : .zip
        panel.allowedContentTypes = [type]
        panel.nameFieldStringValue = protected ? "ssh-manager.sshbackup" : "ssh-manager.zip"
        guard panel.runModal() == .OK, let destination = panel.url else { return }
        let secret = protected ? password : ""
        start {
            let settings = try ManagerSettings.load()
            let snapshot = BackupSnapshot(config: .standard, settings: settings)
            try snapshot.collect(passwords: !secret.isEmpty)
            let archive = try BackupArchive.pack(snapshot.entries, password: secret)
            try writePrivate(archive, to: destination)
            return snapshot.omissions.count
        } success: { omitted in
            clearSecrets()
            message = "Копия сохранена: \(destination.lastPathComponent)." +
                (omitted > 0 ? " Пропущено ссылок на недоступные файлы: \(omitted)." : "")
        }
    }

    private func chooseArchive() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.zip, UTType(filenameExtension: "sshbackup") ?? .data]
        guard panel.runModal() == .OK, let source = panel.url else { return }
        reset()
        start { try BackupArchive.readFile(source, limit: BackupArchive.maxArchive) } success: { archive in
            if archive.starts(with: BackupArchive.magic) { input = archive }
            else { prepare(archive, password: "") }
        }
    }

    private func prepare(_ archive: Data, password: String) {
        start {
            let entries = try BackupArchive.unpack(archive, password: password)
            return try BackupRestore(entries: entries, config: .standard)
        } success: {
            restore = $0
            input = nil
            clearSecrets()
        }
    }

    private func apply(_ plan: BackupRestore) {
        start { try plan.apply() } success: { _ in
            reset()
            manager.reloadSettings()
            manager.reload()
            message = plan.replaced.isEmpty ? "Копия восстановлена." : "Копия восстановлена. Предыдущие файлы сохранены в \(plan.recoveryURL.path)."
        }
    }

    private func start<T>(_ operation: @escaping () throws -> T, success: @escaping (T) -> Void) {
        guard !busy else { return }
        busy = true
        failure = nil
        message = nil
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Result { try operation() }
            DispatchQueue.main.async {
                busy = false
                switch result {
                case .success(let value): success(value)
                case .failure(let error): failure = error.localizedDescription
                }
            }
        }
    }

    private func clearSecrets() {
        password = ""
        confirmation = ""
    }

    private func reset() {
        clearSecrets()
        input = nil
        restore = nil
        failure = nil
        message = nil
    }
}
