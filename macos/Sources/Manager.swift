import AppKit
import SwiftUI
import UniformTypeIdentifiers

enum Destination: Hashable {
    case all, favorites, group(String)
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

/// One row of the sessions list: an SSH terminal or an RDP desktop.
enum SessionEntry: Identifiable {
    case ssh(TerminalSession), rdp(DesktopSession)

    var id: UUID {
        switch self {
        case .ssh(let session): session.id
        case .rdp(let session): session.id
        }
    }
    var title: String {
        switch self {
        case .ssh(let session): session.title
        case .rdp(let session): session.title
        }
    }
    var live: Bool {
        switch self {
        case .ssh(let session): session.live
        case .rdp(let session): session.live
        }
    }
    var protocolName: String {
        if case .rdp = self { return "RDP" }
        return "SSH"
    }
    var address: String {
        switch self {
        case .ssh(let session): session.address
        case .rdp(let session): session.profile.address
        }
    }
    var name: String {
        switch self {
        case .ssh(let session): session.name
        case .rdp(let session): session.name
        }
    }
}

final class Manager: ObservableObject {
    /// The catalog tab: «Все серверы» or a group.
    @Published var destination: Destination = .all
    @Published var showingSessions = false
    @Published private(set) var activeSession: UUID?
    @Published private(set) var profiles: [Profile] = []
    @Published private(set) var settings = ManagerSettings() {
        didSet { AppDelegate.closeToTray = settings.preferences.closeToTray }
    }
    @Published var editingSettings = false
    @Published var importing = false
    @Published var addingSnippet = false
    /// True once tabs.xml has been read, so the update check can follow «Проверять обновления при запуске».
    @Published private(set) var settingsLoaded = false
    /// SSH sessions waiting for an automatic reconnect, with the attempt number.
    @Published private(set) var pendingReconnect: [UUID: Int] = [:]
    private var transient: Set<String> = []
    private var reconnectAttempts: [String: Int] = [:]
    @Published private(set) var sessions: [TerminalSession] = []
    @Published private(set) var desktopSessions: [DesktopSession] = []
    @Published var notice: Notice?
    @Published var editor: ProfileEditorState?
    @Published var groupEditor: GroupEditorState?
    @Published var catalogProtocol: ConnectionProtocol = .ssh
    @Published var desktopEditor: DesktopEditorState?
    @Published var choosingServer = false
    @Published var backupPassword: BackupPasswordRequest?
    /// «Найти сервер»: shared with the footer for «Найдено: X из N».
    @Published var catalogQuery = ""
    @Published var logonRequest: DesktopLogonRequest?
    @Published var renaming: SessionEntry.ID?
    private let config = ConfigStore.standard
    private let catalogQueue = DispatchQueue(label: "sshmanager.catalog", qos: .userInitiated)
    private var order: [UUID] = []
    private var lastSessions: [UUID] = []
    private var fileWindows: [String: FilesWindow] = [:]
    private var pendingMove: Profile?

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
                self.settingsLoaded = true
            }
        }
        reload()
    }

    // MARK: catalog data

    func reload() {
        let store = config
        catalogQueue.async { [weak self] in
            let result = Result { try store.read().profiles }
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

    func reloadAll() {
        reload()
        reloadSettings()
    }

    func updateSettings(_ change: (inout ManagerSettings) throws -> Void) {
        do {
            var latest = try ManagerSettings.load()
            try change(&latest)
            try latest.save()
            settings = latest
        } catch { show(error) }
    }

    var currentGroup: ServerGroup? {
        guard case .group(let id) = destination else { return nil }
        return settings.groups.first { $0.id == id }
    }

    func group(of profile: Profile) -> ServerGroup? { settings.groups.first { $0.aliases.contains(profile.id) } }

    // MARK: favorites, recent, quick connect

    static func key(_ profile: Profile) -> String { "ssh:" + profile.id }
    static func key(_ desktop: DesktopProfile) -> String { "rdp:" + desktop.id }

    func isFavorite(_ key: String) -> Bool { settings.favorites.contains(key) }

    func toggleFavorite(_ key: String) {
        updateSettings { latest in
            if latest.favorites.contains(key) { latest.favorites.removeAll { $0 == key } }
            else { latest.favorites.append(key) }
        }
        if destination == .favorites && settings.favorites.isEmpty { destination = .all }
    }

    private func recordRecent(_ key: String) {
        updateSettings { latest in
            latest.recent.removeAll { $0.key == key }
            latest.recent.insert(RecentEntry(key: key, at: Int(Date().timeIntervalSince1970)), at: 0)
            latest.recent = Array(latest.recent.prefix(10))
        }
    }

    /// Connects without a saved profile, then offers to save it (docs/ui-spec.md §7).
    func quickConnect(_ text: String) {
        do {
            let target = try QuickTarget(text)
            if target.rdp {
                var desktop = DesktopProfile(group: currentGroup?.id)
                desktop.name = target.host
                desktop.host = target.host
                desktop.port = target.port
                desktop.user = target.user
                try desktop.validate()
                connectDesktop(desktop, new: true)
                notice = Notice(title: "SSH Manager", text: "Сохранить подключение «\(target.host)»?") { [weak self] in
                    do { try self?.storeQuickDesktop(desktop) } catch { self?.show(error) }
                }
                return
            }
            let alias = quickAlias(target.host, existing: profiles.map(\.alias) + Array(transient))
            var profile = Profile(alias: alias)
            profile.host = target.host
            profile.user = target.user
            profile.port = target.port == 22 ? "" : String(target.port)
            transient.insert(alias.lowercased())
            startSession(profile, noForwarding: false, direct: true)
            notice = Notice(title: "SSH Manager", text: "Сохранить подключение «\(alias)»?") { [weak self] in
                guard let self else { return }
                do {
                    try self.save(profile, original: nil, password: "", removePassword: false)
                    self.transient.remove(alias.lowercased())
                } catch { self.show(error) }
            }
        } catch { show(error) }
    }

    private func storeQuickDesktop(_ desktop: DesktopProfile) throws { try storeDesktop(desktop, editing: false) }

    // MARK: snippets, preferences, import, keys

    func saveSnippet(name: String, command: String) {
        updateSettings { $0.snippets.append(Snippet(name: name, command: command)) }
    }

    func removeSnippet(_ snippet: Snippet) {
        notice = Notice(title: "SSH Manager", text: "Удалить команду «\(snippet.name)»?") { [weak self] in
            self?.updateSettings { $0.snippets.removeAll { $0.id == snippet.id } }
        }
    }

    func send(_ snippet: Snippet, to session: TerminalSession, everywhere: Bool) {
        let targets = everywhere ? sessions.filter(\.live) : [session]
        for target in targets { target.send(snippet.command) }
    }

    func savePreferences(_ preferences: Preferences) throws {
        var latest = try ManagerSettings.load()
        latest.preferences = preferences
        try latest.save()
        settings = latest
        for session in sessions { session.apply(preferences) }
    }

    func importConnections(_ chosen: [FoundConnection]) {
        var added = 0
        var names = profiles.map(\.alias)
        for item in chosen {
            do {
                if item.rdp {
                    var desktop = DesktopProfile(group: currentGroup?.id)
                    desktop.name = String((item.name.isEmpty ? item.host : item.name).prefix(80))
                    desktop.host = item.host
                    desktop.port = item.port
                    desktop.user = item.user
                    desktop.domain = item.domain
                    try desktop.validate()
                    try storeDesktop(desktop, editing: false)
                } else {
                    let alias = quickAlias(item.name, existing: names)
                    names.append(alias)
                    var profile = Profile(alias: alias)
                    profile.host = item.host
                    profile.user = item.user
                    profile.port = item.port == 22 ? "" : String(item.port)
                    try save(profile, original: nil, password: "", removePassword: false)
                }
                added += 1
            } catch { show(error) }
        }
        reload()
        if notice == nil { notice = Notice(title: "SSH Manager", text: "Импортировано подключений: \(added).") }
    }

    func copyKey(to profile: Profile) {
        let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ssh")
        let keys = ((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []).filter { $0.hasSuffix(".pub") }
        var url: URL? = keys.count == 1 ? folder.appendingPathComponent(keys[0]) : nil
        if url == nil {
            let panel = NSOpenPanel()
            panel.directoryURL = folder
            panel.showsHiddenFiles = true
            panel.message = "Публичный ключ для сервера (*.pub)"
            guard panel.runModal() == .OK else { return }
            url = panel.url
        }
        guard let url else { return }
        do {
            let command = try Keys.copyCommand(String(decoding: try readRegularFile(url, limit: 64 * 1024), as: UTF8.self))
            let number = (sessions.filter { $0.profile.id == profile.id }.map(\.number).max() ?? 0) + 1
            let session = TerminalSession(profile: profile, number: number, remoteCommand: command,
                                          preferences: settings.preferences) { [weak self] _ in self?.showServers() }
            session.name = "ключ"
            sessions.append(session)
            order.append(session.id)
            activate(session.id)
        } catch { show(error) }
    }
    func group(of desktop: DesktopProfile) -> ServerGroup? { settings.groups.first { $0.id == desktop.group } }

    var currentSort: ServerSort {
        if catalogProtocol == .rdp { return currentGroup?.rdpSort ?? settings.rdpSort }
        return currentGroup?.sshSort ?? settings.sshSort
    }

    /// Header click: ascending → descending → original order (src/ConnectionsWindow.cs:301-308).
    func sortBy(address: Bool) {
        let ascending: ServerSort = address ? .address : .name
        let descending: ServerSort = address ? .addressDescending : .nameDescending
        let current = currentSort
        let next: ServerSort = current == ascending ? descending : current == descending ? .original : ascending
        sortCatalog(next, kind: catalogProtocol, group: currentGroup?.id)
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

    // MARK: SSH profiles

    func save(_ profile: Profile, original: Profile?, password: String, removePassword: Bool) throws {
        try profile.validate(replacing: original)
        guard password.utf8.count <= 1022, !password.contains(where: \.isNewline), !password.contains("\0")
        else { throw ManagerError("Пароль слишком длинный или содержит недопустимые символы.") }
        if !password.isEmpty, profile.passwordHost == nil {
            throw ManagerError("Для входа по сохранённому паролю укажите SSH-пользователя и обычный DNS-адрес или IP без подстановок.")
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

    func hasPassword(_ profile: Profile) -> Bool { (try? Passwords.read(profile.account)) != nil }

    func remove(_ profile: Profile) {
        notice = Notice(title: "SSH Manager", text: "Удалить подключение «\(profile.alias)» из SSH config?") { [weak self] in
            guard let self else { return }
            do { try self.config.update(nil, replacing: profile) }
            catch { self.show(error); return }
            do { try Passwords.set(nil, for: profile.account) } catch { self.show(error) }
            self.updateSettings { $0.move(profile.id, to: nil) }
            self.reload()
        }
    }

    func move(_ profile: Profile, to group: String?) {
        updateSettings { $0.move(profile.id, to: group) }
    }

    func moveToNewGroup(_ profile: Profile) {
        pendingMove = profile
        groupEditor = GroupEditorState()
    }

    func copyCommand(_ profile: Profile) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString("ssh " + profile.alias, forType: .string)
    }

    /// Runs `ssh <alias>` in Terminal.app through a private .command file.
    func openInTerminal(_ profile: Profile) {
        let files = FileManager.default
        let directory = files.temporaryDirectory.appendingPathComponent("SSHManager-\(UUID().uuidString)", isDirectory: true)
        let script = directory.appendingPathComponent("\(profile.alias).command")
        do {
            try files.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            let quoted = "'" + profile.alias.replacingOccurrences(of: "'", with: "'\\''") + "'"
            try Data("#!/bin/sh\nrm -f \"$0\"\nexec /usr/bin/ssh \(quoted)\n".utf8).write(to: script)
            try files.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
            let terminal = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Terminal")
            if let terminal {
                NSWorkspace.shared.open([script], withApplicationAt: terminal, configuration: NSWorkspace.OpenConfiguration())
            } else {
                NSWorkspace.shared.open(script)
            }
        } catch { show(error) }
    }

    // MARK: groups

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
        if original == nil, let profile = pendingMove, let group = current.groups.last {
            current.move(profile.id, to: group.id)
            pendingMove = nil
            try current.save()
            settings = current
            return
        }
        try current.save()
        settings = current
        if original == nil, let group = current.groups.last { destination = .group(group.id) }
    }

    func cancelGroupEditor() { pendingMove = nil }

    func removeGroup(_ group: ServerGroup) {
        notice = Notice(title: "SSH Manager",
                        text: "Удалить вкладку «\(group.name)»? Её подключения будут доступны во вкладке «Все серверы».") { [weak self] in
            guard let self else { return }
            self.updateSettings {
                $0.groups.removeAll { $0.id == group.id }
                for index in $0.desktops.indices where $0.desktops[index].group == group.id {
                    $0.desktops[index].group = nil
                }
                if $0.startup == group.id { $0.startup = nil }
            }
            if self.destination == .group(group.id) { self.destination = .all }
        }
    }

    func shiftGroup(_ group: ServerGroup, by offset: Int) {
        updateSettings {
            guard let index = $0.groups.firstIndex(where: { $0.id == group.id }),
                  $0.groups.indices.contains(index + offset) else { return }
            $0.groups.swapAt(index, index + offset)
        }
    }

    func placeGroup(_ id: String, before target: String) {
        guard id != target else { return }
        updateSettings {
            guard let from = $0.groups.firstIndex(where: { $0.id == id }) else { return }
            let moved = $0.groups.remove(at: from)
            let to = $0.groups.firstIndex(where: { $0.id == target }) ?? $0.groups.count
            $0.groups.insert(moved, at: to)
        }
    }

    func mark(_ group: ServerGroup, _ marker: GroupMarker?) {
        updateSettings {
            if let index = $0.groups.firstIndex(where: { $0.id == group.id }) { $0.groups[index].marker = marker }
        }
    }

    func addCatalogEntry() {
        if catalogProtocol == .ssh {
            editor = ProfileEditorState()
        } else {
            desktopEditor = DesktopEditorState(original: nil, group: currentGroup?.id)
        }
    }

    // MARK: RDP profiles

    func saveDesktop(_ profile: DesktopProfile, editing: Bool, password: String = "", removePassword: Bool = false) throws {
        try profile.validate()
        guard password.utf8.count <= 1022, !password.contains(where: \.isNewline), !password.contains("\0")
        else { throw ManagerError("Пароль слишком длинный или содержит недопустимые символы.") }
        if !password.isEmpty, profile.user.isEmpty { throw ManagerError("Укажите пользователя, чтобы сохранить пароль RDP.") }
        var rollback: (() throws -> Void)?
        if !password.isEmpty || removePassword {
            let before = try Passwords.read(profile.keychainAccount)
            try Passwords.set(removePassword ? nil : password, for: profile.keychainAccount)
            rollback = { try Passwords.set(before, for: profile.keychainAccount) }
        }
        do { try storeDesktop(profile, editing: editing) }
        catch {
            do { try rollback?() }
            catch let keychainError { throw ManagerError(error.localizedDescription + "\n" + keychainError.localizedDescription) }
            throw error
        }
    }

    func hasDesktopPassword(_ profile: DesktopProfile) -> Bool {
        (try? Passwords.read(profile.keychainAccount)) != nil
    }

    private func storeDesktop(_ profile: DesktopProfile, editing: Bool) throws {
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
        notice = Notice(title: "SSH Manager", text: "Удалить RDP-подключение «\(profile.name)»?") { [weak self] in
            self?.updateSettings { $0.desktops.removeAll { $0.id == profile.id } }
            do { try Passwords.set(nil, for: profile.keychainAccount) } catch { self?.show(error) }
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
            do { try storeDesktop(DesktopProfile(file: url, group: group ?? currentGroup?.id), editing: false) }
            catch { show(error) }
        }
    }

    // MARK: sessions

    var entries: [SessionEntry] {
        order.compactMap { id in
            if let session = sessions.first(where: { $0.id == id }) { return .ssh(session) }
            if let session = desktopSessions.first(where: { $0.id == id }) { return .rdp(session) }
            return nil
        }
    }

    var activeEntry: SessionEntry? { entries.first { $0.id == activeSession } }

    var liveCount: Int { sessions.filter(\.live).count + desktopSessions.filter(\.live).count }

    func liveCount(for profile: Profile) -> Int { sessions.filter { $0.profile.id == profile.id && $0.live }.count }
    func liveCount(for desktop: DesktopProfile) -> Int { desktopSessions.filter { $0.profile.id == desktop.id && $0.live }.count }

    func connect(_ profile: Profile, new: Bool = false, noForwarding: Bool = false) {
        let related = sessions.filter { $0.profile.id == profile.id }
        if !new, let latest = lastSessions.compactMap({ id in related.first { $0.id == id && $0.live } }).first {
            activate(latest.id)
            return
        }
        recordRecent(Self.key(profile))
        startSession(profile, noForwarding: noForwarding, direct: false)
    }

    private func startSession(_ profile: Profile, noForwarding: Bool, direct: Bool, name: String = "", replacing old: TerminalSession? = nil) {
        let related = sessions.filter { $0.profile.id == profile.id }
        let number = old?.number ?? (related.map(\.number).max() ?? 0) + 1
        let session = TerminalSession(profile: profile, noForwarding: noForwarding, number: number, direct: direct,
                                      preferences: settings.preferences) { [weak self] _ in self?.showServers() }
        session.name = name
        session.onExit = { [weak self, weak session] code in
            guard let self, let session else { return }
            self.objectWillChange.send()
            let key = Self.key(profile)
            guard self.settings.preferences.autoReconnect, code != 0, code != 130,
                  self.reconnectAttempts[key, default: 0] < 3 else { return }
            self.reconnectAttempts[key, default: 0] += 1
            self.pendingReconnect[session.id] = self.reconnectAttempts[key]
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self, weak session] in
                guard let self, let session, self.sessions.contains(where: { $0 === session }) else { return }
                self.reconnect(.ssh(session), automatic: true)
            }
        }
        if let old {
            old.close()
            if let index = sessions.firstIndex(where: { $0.id == old.id }) { sessions[index] = session }
            if let index = order.firstIndex(of: old.id) { order[index] = session.id }
            lastSessions.removeAll { $0 == old.id }
        } else {
            sessions.append(session)
            order.append(session.id)
        }
        activate(session.id)
    }

    /// Opens an RDP session, asking for the password first when none is saved and FreeRDP will connect.
    func connectDesktop(_ profile: DesktopProfile, new: Bool = false) {
        if settings.desktops.contains(where: { $0.id == profile.id }) { recordRecent(Self.key(profile)) }
        let related = desktopSessions.filter { $0.profile.id == profile.id }
        if !new, let latest = lastSessions.compactMap({ id in related.first { $0.id == id && $0.live } }).first {
            activate(latest.id)
            return
        }
        var password: String?
        do { password = try Passwords.read(profile.keychainAccount) } catch { show(error) }
        if DesktopProfile.freeRDPClient() != nil, password == nil || profile.user.isEmpty {
            logonRequest = DesktopLogonRequest(profile: profile) { [weak self] user, domain, entered, remember in
                guard let self else { return }
                if remember, !entered.isEmpty {
                    if user == profile.user && domain == profile.domain {
                        do { try Passwords.set(entered, for: profile.keychainAccount) } catch { self.show(error) }
                    } else {
                        self.show(ManagerError("Пароль сохраняется только для пользователя из настроек подключения. " +
                                               "Измените пользователя в профиле, чтобы запомнить пароль для другой учётной записи."))
                    }
                }
                self.openDesktop(profile, user: user, domain: domain, password: entered.isEmpty ? nil : entered)
            }
            return
        }
        openDesktop(profile, user: profile.user, domain: profile.domain, password: password)
    }

    private func openDesktop(_ profile: DesktopProfile, user: String, domain: String, password: String?) {
        let number = (desktopSessions.filter { $0.profile.id == profile.id }.map(\.number).max() ?? 0) + 1
        let session = DesktopSession(profile: profile, number: number, user: user, domain: domain, password: password)
        desktopSessions.append(session)
        order.append(session.id)
        activate(session.id)
        session.start()
    }

    func duplicate(_ entry: SessionEntry, noForwarding: Bool? = nil) {
        switch entry {
        case .ssh(let session): connect(session.profile, new: true, noForwarding: noForwarding ?? session.noForwarding)
        case .rdp(let session): openDesktop(session.profile, user: session.user, domain: session.domain, password: session.password)
        }
    }

    /// SSH: starts the same server again in place of the ended session.
    func reconnect(_ entry: SessionEntry, automatic: Bool = false) {
        switch entry {
        case .rdp(let session): session.start()
        case .ssh(let old):
            pendingReconnect[old.id] = nil
            if !automatic { reconnectAttempts[Self.key(old.profile)] = 0 }
            startSession(old.profile, noForwarding: old.noForwarding, direct: old.direct, name: old.name, replacing: old)
        }
    }

    func fullScreen(_ session: DesktopSession) { session.start(fullScreen: true) }

    func activate(_ id: UUID) {
        lastSessions.removeAll { $0 == id }
        lastSessions.insert(id, at: 0)
        activeSession = id
        showingSessions = true
        if let session = sessions.first(where: { $0.id == id }) { DispatchQueue.main.async { session.focus() } }
    }

    func switchSession() {
        guard let id = lastSessions.first(where: { id in id != activeSession && entries.contains { $0.id == id && $0.live } })
        else { return }
        activate(id)
    }

    func showServers() { showingSessions = false }
    func showSessionsPage() {
        showingSessions = true
        if activeSession == nil { activeSession = lastSessions.first ?? order.first }
    }

    func closeSession(_ entry: SessionEntry) {
        guard entries.contains(where: { $0.id == entry.id }) else { return }
        guard entry.live else { removeSession(entry); return }
        notice = Notice(title: "Вы уверены?", text: "Закрыть \(entry.protocolName)-сеанс «\(entry.title)»?") { [weak self] in
            self?.removeSession(entry)
        }
    }

    private func removeSession(_ entry: SessionEntry) {
        switch entry {
        case .ssh(let session):
            session.close()
            pendingReconnect[session.id] = nil
            sessions.removeAll { $0.id == session.id }
        case .rdp(let session):
            session.stop()
            desktopSessions.removeAll { $0.id == session.id }
        }
        order.removeAll { $0 == entry.id }
        lastSessions.removeAll { $0 == entry.id }
        if activeSession == entry.id { activeSession = lastSessions.first ?? order.last }
    }

    func rename(_ entry: SessionEntry, to label: String) throws {
        let name = String(label.trimmingCharacters(in: .whitespacesAndNewlines).prefix(80))
        let siblings: [String]
        switch entry {
        case .ssh(let session): siblings = sessions.filter { $0.profile.id == session.profile.id && $0.id != session.id }.map(\.name)
        case .rdp(let session): siblings = desktopSessions.filter { $0.profile.id == session.profile.id && $0.id != session.id }.map(\.name)
        }
        if name.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) ||
            (!name.isEmpty && siblings.contains { $0.caseInsensitiveCompare(name) == .orderedSame }) {
            throw ManagerError("Укажите уникальную метку для сеанса этого сервера.")
        }
        switch entry {
        case .ssh(let session): session.name = name
        case .rdp(let session): session.name = name
        }
        objectWillChange.send()
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

    var configPath: String { config.url.path.replacingOccurrences(of: NSHomeDirectory(), with: "~") }

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
