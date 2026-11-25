import AppKit
import SwiftUI
import UniformTypeIdentifiers

enum Destination: Hashable {
    case all, group(String), session(UUID)
}

enum ConnectionProtocol: String, CaseIterable, Identifiable {
    case ssh = "SSH", rdp = "RDP"
    var id: Self { self }
}

struct Notice: Identifiable {
    let id = UUID()
    let title: String
    let text: String
    var confirm: (() -> Void)? = nil
}

final class Manager: ObservableObject {
    @Published var destination: Destination = .all {
        didSet {
            switch destination {
            case .all, .group: lastCatalog = destination
            case .session: break
            }
        }
    }
    @Published private(set) var profiles: [Profile] = []
    @Published private(set) var settings = ManagerSettings()
    @Published private(set) var sessions: [TerminalSession] = []
    @Published var notice: Notice?
    @Published var editor: ProfileEditorState?
    @Published var groupEditor: GroupEditorState?
    @Published var deleteProfile: Profile?
    @Published var deleteGroup: ServerGroup?
    @Published var catalogProtocol: ConnectionProtocol = .ssh
    @Published var desktopEditor: DesktopEditorState?
    @Published var choosingServer = false
    @Published var managingBackups = false
    private let config = ConfigStore.standard
    private let catalogQueue = DispatchQueue(label: "sshmanager.catalog", qos: .userInitiated)
    private var lastCatalog: Destination = .all
    private var lastSessions: [UUID] = []
    private var fileWindows: [String: FilesWindow] = [:]

    init() {
        catalogQueue.async { [weak self] in
            let result = Result { try ManagerSettings.load() }
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                switch result {
                case .success(let initial):
                    self.settings = initial
                    if self.destination == .all, let tab = initial.startup { self.destination = .group(tab) }
                case .failure(let error): self.show(error)
                }
            }
        }
        reload()
    }

    func reload() {
        let store = config
        catalogQueue.async { [weak self] in
            let result = Result {
                try store.read().profiles
            }
            DispatchQueue.main.async { [weak self] in
                switch result {
                case .success(let profiles): self?.profiles = profiles
                case .failure(let error): self?.show(error)
                }
            }
        }
    }

    func reloadSettings() {
        do {
            settings = try ManagerSettings.load()
            if case .group(let id) = destination, !settings.groups.contains(where: { $0.id == id }) { destination = .all }
        } catch { show(error) }
    }

    func updateSettings(_ change: (inout ManagerSettings) throws -> Void) {
        do {
            var latest = try ManagerSettings.load()
            try change(&latest)
            try latest.save()
            settings = latest
        } catch { show(error) }
    }

    func save(_ profile: Profile, original: Profile?, password: String, removePassword: Bool) throws {
        try profile.validate(replacing: original)
        guard password.utf8.count <= 1022, !password.contains(where: \.isNewline), !password.contains("\0")
        else { throw ManagerError("Пароль слишком длинный или содержит недопустимые символы.") }
        if !password.isEmpty, profile.passwordHost == nil {
            throw ManagerError("Для сохранения пароля укажите пользователя и адрес сервера без шаблонов.")
        }
        let changed = original?.account != profile.account
        let sameServer = original?.destinationKey == profile.destinationKey
        let previous = changed && sameServer && !removePassword && password.isEmpty ? try original.flatMap {
            try Passwords.read($0.account)
        } : nil
        let newPassword = password.isEmpty ? previous : password
        var rollback: (() throws -> Void)?
        if newPassword != nil || removePassword {
            let before = try Passwords.read(profile.account)
            try Passwords.set(newPassword, for: profile.account)
            rollback = { try Passwords.set(before, for: profile.account) }
        }
        do { try config.update(profile, replacing: original) }
        catch {
            do { try rollback?() }
            catch let keychainError {
                throw ManagerError(error.localizedDescription + "\n" + keychainError.localizedDescription)
            }
            throw error
        }
        if changed, let original {
            do { try Passwords.set(nil, for: original.account) } catch { show(error) }
        }
        if let original, original.id != profile.id {
            updateSettings { settings in
                let group = settings.groups.first { $0.aliases.contains(original.id) }
                settings.move(original.id, to: nil)
                settings.move(profile.id, to: group?.id)
            }
        } else if original == nil, case .group(let id) = destination {
            updateSettings { $0.move(profile.id, to: id) }
        }
        reload()
    }

    func remove(_ profile: Profile) {
        do { try config.update(nil, replacing: profile) }
        catch { show(error); return }
        do { try Passwords.set(nil, for: profile.account) } catch { show(error) }
        updateSettings { $0.move(profile.id, to: nil) }
        reload()
    }

    func saveGroup(_ name: String, original: ServerGroup?) throws {
        let cleaned = name.trimmingCharacters(in: .whitespacesAndNewlines)
        var current = try ManagerSettings.load()
        try current.validate(cleaned, except: original?.id)
        if let original {
            guard let index = current.groups.firstIndex(where: { $0.id == original.id }) else { throw ManagerError("Эта вкладка уже удалена.") }
            current.groups[index].name = cleaned
        } else {
            current.groups.append(ServerGroup(name: cleaned))
        }
        try current.save()
        settings = current
        if original == nil, let group = current.groups.last { destination = .group(group.id) }
    }

    func removeGroup(_ group: ServerGroup) {
        updateSettings {
            $0.groups.removeAll { $0.id == group.id }
            for index in $0.desktops.indices where $0.desktops[index].group == group.id {
                $0.desktops[index].group = nil
            }
            if $0.startup == group.id { $0.startup = nil }
        }
        if !settings.groups.contains(where: { $0.id == group.id }), destination == .group(group.id) { destination = .all }
    }

    func shiftGroup(_ group: ServerGroup, by offset: Int) {
        updateSettings {
            guard let index = $0.groups.firstIndex(where: { $0.id == group.id }),
                  $0.groups.indices.contains(index + offset) else { return }
            $0.groups.swapAt(index, index + offset)
        }
    }

    func sortCatalog(_ order: ServerSort, kind: ConnectionProtocol, group: String?) {
        updateSettings { latest in
            if let group {
                guard let index = latest.groups.firstIndex(where: { $0.id == group }) else { return }
                if kind == .ssh { latest.groups[index].sshSort = order }
                else { latest.groups[index].rdpSort = order }
            } else if kind == .ssh {
                latest.sshSort = order
            } else {
                latest.rdpSort = order
            }
        }
    }

    func addCatalogEntry() {
        if catalogProtocol == .ssh {
            editor = ProfileEditorState()
        } else {
            let group: String?
            if case .group(let id) = lastCatalog { group = id } else { group = nil }
            desktopEditor = DesktopEditorState(original: nil, group: group)
        }
    }

    func saveDesktop(_ profile: DesktopProfile, editing: Bool) throws {
        try profile.validate()
        var latest = try ManagerSettings.load()
        if let group = profile.group, !latest.groups.contains(where: { $0.id == group }) {
            throw ManagerError("Эта вкладка уже удалена.")
        }
        if editing {
            guard let index = latest.desktops.firstIndex(where: { $0.id == profile.id }) else {
                throw ManagerError("RDP-подключение уже удалено.")
            }
            latest.desktops[index] = profile
        } else {
            latest.desktops.append(profile)
        }
        try latest.save()
        settings = latest
    }

    func removeDesktop(_ profile: DesktopProfile) {
        notice = Notice(title: "Удалить RDP-подключение?", text: "Удалить «\(profile.name)» из каталога?") { [weak self] in
            self?.updateSettings { $0.desktops.removeAll { $0.id == profile.id } }
        }
    }

    func moveDesktop(_ profile: DesktopProfile, to group: String?) {
        updateSettings { latest in
            guard let index = latest.desktops.firstIndex(where: { $0.id == profile.id }),
                  group == nil || latest.groups.contains(where: { $0.id == group }) else {
                throw ManagerError("Подключение или вкладка уже удалены.")
            }
            latest.desktops[index].group = group
        }
    }

    func transferDesktop(_ profile: DesktopProfile?, group: String? = nil) {
        let type = UTType(filenameExtension: "rdp") ?? .data
        if let profile {
            let panel = NSSavePanel()
            panel.allowedContentTypes = [type]
            panel.nameFieldStringValue = "connection.rdp"
            guard panel.runModal() == .OK, let url = panel.url else { return }
            do { try writePrivate(profile.data, to: url) } catch { show(error) }
        } else {
            let panel = NSOpenPanel()
            panel.allowedContentTypes = [type]
            guard panel.runModal() == .OK, let url = panel.url else { return }
            do { try saveDesktop(DesktopProfile(file: url, group: group), editing: false) }
            catch { show(error) }
        }
    }

    func connectDesktop(_ profile: DesktopProfile) {
        let files = FileManager.default
        let directory = files.temporaryDirectory.appendingPathComponent("SSHManager-\(UUID().uuidString)", isDirectory: true)
        let url = directory.appendingPathComponent("connection.rdp")
        do {
            try files.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            try writePrivate(profile.data, to: url)
            let workspace = NSWorkspace.shared
            let client = workspace.urlForApplication(withBundleIdentifier: "com.microsoft.rdc.macos") ??
                workspace.urlForApplication(toOpen: url)
            guard let client else { throw ManagerError("Установите Microsoft Windows App для подключения по RDP.") }
            workspace.open([url], withApplicationAt: client, configuration: NSWorkspace.OpenConfiguration()) { [weak self] _, error in
                DispatchQueue.main.async {
                    if let error { self?.show(error) }
                }
                DispatchQueue.global().asyncAfter(deadline: .now() + 120) {
                    try? files.removeItem(at: url)
                    try? files.removeItem(at: directory)
                }
            }
        } catch {
            try? files.removeItem(at: url)
            try? files.removeItem(at: directory)
            show(error)
        }
    }

    func connect(_ profile: Profile, new: Bool = false, noForwarding: Bool = false) {
        if !new, let latest = lastSessions.compactMap({ id in sessions.first { $0.id == id && $0.profile.id == profile.id && $0.exitCode == nil } }).first {
            activate(latest)
            return
        }
        let session = TerminalSession(profile: profile, noForwarding: noForwarding) { [weak self] id in
            self?.showServers(from: id)
        }
        sessions.append(session)
        activate(session)
    }

    func activate(_ session: TerminalSession) {
        recordSession(session.id)
        destination = .session(session.id)
        DispatchQueue.main.async { session.focus() }
    }

    func recordSession(_ id: UUID) {
        lastSessions.removeAll { $0 == id }
        lastSessions.insert(id, at: 0)
    }

    func switchSession() {
        guard let id = lastSessions.first(where: { .session($0) != destination }),
              let session = sessions.first(where: { $0.id == id }) else { return }
        activate(session)
    }

    var showingSession: Bool {
        if case .session = destination { return true }
        return false
    }

    func showServers(from id: UUID? = nil) {
        guard case .session(let current) = destination, id == nil || id == current else { return }
        if case .group(let group) = lastCatalog, !settings.groups.contains(where: { $0.id == group }) {
            destination = .all
        } else {
            destination = lastCatalog
        }
    }

    func closeSession(_ session: TerminalSession) {
        guard sessions.contains(where: { $0.id == session.id }) else { return }
        notice = Notice(title: "Вы уверены?", text: "Закрыть SSH-сеанс «\(session.label)»?") { [weak self, weak session] in
            guard let self, let session else { return }
            self.removeSession(session)
        }
    }

    private func removeSession(_ session: TerminalSession) {
        session.close()
        sessions.removeAll { $0.id == session.id }
        lastSessions.removeAll { $0 == session.id }
        if destination == .session(session.id) {
            destination = lastSessions.first.map(Destination.session) ?? .all
        }
    }

    func openFiles(_ profile: Profile) {
        let window = fileWindows[profile.id] ?? FilesWindow(profile: profile) { [weak self] in self?.fileWindows[profile.id] = nil }
        fileWindows[profile.id] = window
        window.window?.appearance = settings.appearance == .system ? nil : NSAppearance(named: settings.appearance == .dark ? .darkAqua : .aqua)
        window.showWindow(nil)
        window.window?.makeKeyAndOrderFront(nil)
    }

    func openConfig() {
        if FileManager.default.fileExists(atPath: config.url.path) {
            if !NSWorkspace.shared.open(config.url) { NSWorkspace.shared.activateFileViewerSelecting([config.url]) }
        } else {
            show(ManagerError("Файл ~/.ssh/config появится после добавления первого сервера."))
        }
    }

    func show(_ error: Error) { notice = Notice(title: "SSH Manager", text: error.localizedDescription) }
}

struct ProfileEditorState: Identifiable {
    let id = UUID()
    var original: Profile?
}

struct GroupEditorState: Identifiable {
    let id = UUID()
    var original: ServerGroup?
}

struct DesktopEditorState: Identifiable {
    let id = UUID()
    var original: DesktopProfile?
    var group: String?
}

private extension Profile {
    var destinationKey: String {
        [host.isEmpty ? id : host.lowercased(), user, String(Int(port) ?? 22)].joined(separator: "\n")
    }
}
