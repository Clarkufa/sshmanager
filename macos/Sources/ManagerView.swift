import AppKit
import SwiftUI

struct SSHManagerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var manager = Manager()
    @StateObject private var updates = MacUpdates()
    var body: some Scene {
        Window("SSH Manager", id: "main") {
            ManagerView(manager: manager, updates: updates)
                .frame(minWidth: 800, minHeight: 480)
                .preferredColorScheme(manager.settings.appearance.scheme)
                .onAppear { if manager.settingsLoaded && manager.settings.preferences.checkUpdates { updates.check(manual: false) } }
                .onChange(of: manager.settingsLoaded) { _, loaded in
                    if loaded && manager.settings.preferences.checkUpdates { updates.check(manual: false) }
                }
        }
        .defaultSize(width: 1280, height: 720)
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("Настройки…") { manager.editingSettings = true }.keyboardShortcut(",", modifiers: .command)
            }
            CommandGroup(replacing: .newItem) {
                Button("Новый сеанс…") { manager.choosingServer = true }
                    .keyboardShortcut("n", modifiers: .command)
                    .disabled(manager.profiles.isEmpty && manager.settings.desktops.isEmpty && manager.settings.vnc.isEmpty)
                Button("Добавить сервер…") { manager.addCatalogEntry() }
                    .keyboardShortcut("n", modifiers: [.command, .shift])
                Button("Новая вкладка…") { manager.groupEditor = GroupEditorState() }
            }
            CommandMenu("Подключения") {
                Button("Сеансы") { manager.showSessionsPage() }
                Button("Колонка «Серверы»") { manager.toggleServerColumn(shown: manager.serverColumn) }
                Button("Колонка «Сеансы»") { manager.toggleSessionColumn(shown: manager.sessionColumn) }
                Button("Предыдущий сеанс") { manager.switchSession() }
                    .keyboardShortcut(.tab, modifiers: .control)
                    .disabled(manager.entries.isEmpty)
                Button("Поиск сеанса") {
                    manager.revealSessionColumn()
                    DispatchQueue.main.async { NotificationCenter.default.post(name: .focusSessionSearch, object: nil) }
                }
                    .keyboardShortcut("p", modifiers: [.command, .shift])
                Button("Обновить") { manager.reloadAll() }
                    .keyboardShortcut("r", modifiers: .command)
                Button("Найти сервер") {
                    manager.revealServerColumn()
                    DispatchQueue.main.async { NotificationCenter.default.post(name: .focusServerSearch, object: nil) }
                }.keyboardShortcut("f", modifiers: .command)
                Button("Изменить сервер") { NotificationCenter.default.post(name: .editCatalogRow, object: nil) }
                    .keyboardShortcut("e", modifiers: .command)
                Button("Удалить сервер") { NotificationCenter.default.post(name: .deleteCatalogRow, object: nil) }
                    .keyboardShortcut(.delete, modifiers: .command)
                Button("Открыть OpenSSH config") { manager.openConfig() }
                Button("Известные хосты…") { manager.openKnownHosts(nil) }
                Button("Быстрое подключение") {
                    NotificationCenter.default.post(name: .focusQuickConnect, object: nil)
                }.keyboardShortcut("l", modifiers: .command)
                Button("Экспорт архива…") { manager.exportBackup() }
                Button("Импорт архива…") { manager.importBackup() }
                Button("Импорт из других программ…") { manager.importing = true }
                Button("Обновления…") { updates.check(manual: true) }
                Divider()
                Menu("Оформление") {
                    ForEach(Appearance.allCases) { style in
                        Button {
                            manager.updateSettings { $0.appearance = style }
                        } label: {
                            if style == manager.settings.appearance { Label(style.title, systemImage: "checkmark") }
                            else { Text(style.title) }
                        }
                    }
                }
            }
        }
    }
}

extension Notification.Name {
    static let focusSessionSearch = Notification.Name("SSHManagerFocusSessionSearch")
    static let focusQuickConnect = Notification.Name("SSHManagerFocusQuickConnect")
    static let focusCatalogSearch = Notification.Name("SSHManagerFocusCatalogSearch")
    static let focusServerSearch = Notification.Name("SSHManagerFocusServerSearch")
    static let editCatalogRow = Notification.Name("SSHManagerEditCatalogRow")
    static let deleteCatalogRow = Notification.Name("SSHManagerDeleteCatalogRow")
}

// MARK: - Root

private struct ManagerView: View {
    @ObservedObject var manager: Manager
    @ObservedObject var updates: MacUpdates
    @Environment(\.colorScheme) private var scheme
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        let palette = Palette.of(scheme)
        GeometryReader { geometry in
            let columns = ColumnLayout(width: geometry.size.width, manager: manager)
            VStack(spacing: 0) {
                WorkspaceHeader(manager: manager, columns: columns)
                HStack(alignment: .top, spacing: ColumnLayout.gap) {
                    if columns.servers { ServerColumn(manager: manager).frame(width: ColumnLayout.serverWidth) }
                    if columns.sessions { SessionColumn(manager: manager).frame(width: ColumnLayout.sessionWidth) }
                    WorkspaceArea(manager: manager)
                }
                .padding(.horizontal, ColumnLayout.margin)
                .frame(maxHeight: .infinity, alignment: .top)
                Footer(manager: manager, updates: updates)
            }
        }
        .background(palette.canvas)
        .foregroundStyle(palette.ink)
        .onAppear { AppDelegate.reopen = { openWindow(id: "main") } }
        .sheet(item: $manager.editor) { state in
            ProfileEditor(state: state, hasPassword: state.original.map(manager.hasPassword) ?? false, defaults: manager.settings.preferences) {
                try manager.save($0, original: state.original, password: $1, removePassword: $2)
            }
        }
        .sheet(item: $manager.groupEditor, onDismiss: { manager.cancelGroupEditor() }) { state in
            GroupEditor(state: state) { try manager.saveGroup($0, original: state.original) }
        }
        .sheet(item: $manager.desktopEditor) { state in
            DesktopEditor(state: state, hasPassword: state.original.map(manager.hasDesktopPassword) ?? false,
                          importFile: { manager.transferDesktop(nil, group: state.group) }) {
                try manager.saveDesktop($0, editing: state.original != nil, password: $1, removePassword: $2)
            }
        }
        .sheet(item: $manager.vncEditor) { state in
            VncEditor(state: state, hasPassword: state.original.map(manager.hasVncPassword) ?? false,
                      sshAliases: manager.profiles.map(\.alias)) {
                try manager.saveVnc($0, editing: state.original != nil, password: $1, removePassword: $2)
            }
        }
        .sheet(item: $manager.logonRequest) { request in LogonSheet(request: request) }
        .sheet(item: $manager.knownHostsRequest) { request in KnownHostsView(request: request) }
        .sheet(isPresented: $manager.choosingServer) { ServerPicker(manager: manager) }
        .sheet(item: $manager.backupPassword) { request in BackupPasswordSheet(request: request) }
        .sheet(isPresented: $updates.presented) { MacUpdateView(updates: updates) }
        .sheet(isPresented: $manager.editingSettings) {
            SettingsSheet(preferences: manager.settings.preferences, openKnownHosts: {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { manager.openKnownHosts(nil) }
            }) { try manager.savePreferences($0) }
        }
        .sheet(isPresented: $manager.importing) { ImportSheet { manager.importConnections($0) } }
        .sheet(isPresented: $manager.addingSnippet) { SnippetSheet { manager.saveSnippet(name: $0, command: $1) } }
        .sheet(item: Binding(get: { manager.renaming.flatMap { id in manager.entries.first { $0.id == id } }.map(RenameRequest.init) },
                             set: { if $0 == nil { manager.renaming = nil } })) { request in
            RenameSheet(entry: request.entry) { try manager.rename(request.entry, to: $0) }
        }
        .alert(manager.notice?.title ?? "",
               isPresented: Binding(get: { manager.notice != nil }, set: { if !$0 { manager.notice = nil } }),
               presenting: manager.notice) { notice in
            if let confirm = notice.confirm {
                Button("Нет", role: .cancel) {}.keyboardShortcut(.defaultAction)
                Button("Да", role: .destructive, action: confirm)
            } else {
                Button("ОК", role: .cancel) {}
            }
        } message: { notice in
            Text(notice.text)
        }
    }
}

private struct RenameRequest: Identifiable {
    let entry: SessionEntry
    var id: UUID { entry.id }
}

/// Three columns under the header: «Серверы» 248, «Сеансы» 203 and the workspace. A narrow window folds «Серверы»,
/// then «Сеансы», so the workspace keeps 620 for the catalog table or 460 for a session or files (docs/ui-spec.md §2.2).
private struct ColumnLayout {
    static let serverWidth: CGFloat = 248
    static let sessionWidth: CGFloat = 203
    static let gap: CGFloat = 12
    static let margin: CGFloat = 12
    let servers: Bool
    let sessions: Bool

    init(width: CGFloat, manager: Manager) {
        let free = width - 2 * Self.margin
        let need: CGFloat = 460
        var servers = manager.serverColumn
        var sessions = manager.sessionColumn
        func room() -> CGFloat {
            free - (servers ? Self.serverWidth + Self.gap : 0) - (sessions ? Self.sessionWidth + Self.gap : 0)
        }
        // The server list is the way to every connection: the session column folds first.
        if sessions && !manager.sessionColumnForced && room() < need { sessions = false }
        if servers && !manager.serverColumnForced && room() < need { servers = false }
        self.servers = servers
        self.sessions = sessions
    }
}

/// Logo, «SSH Manager», quick connect and the window buttons (docs/ui-spec.md §2.1).
private struct WorkspaceHeader: View {
    @ObservedObject var manager: Manager
    let columns: ColumnLayout
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        HStack(spacing: 8) {
            AppMark(size: 32)
            Text("SSH Manager").font(.system(size: 14, weight: .semibold)).lineLimit(1).fixedSize()
            Spacer(minLength: 6)
            QuickConnectField(manager: manager)
            Spacer(minLength: 6)
            ViewThatFits(in: .horizontal) {
                buttons(compact: false)
                buttons(compact: true)
            }
            .layoutPriority(1)
        }
        .padding(.horizontal, 16)
        .frame(height: 54)
    }

    private func buttons(compact: Bool) -> some View {
        HStack(spacing: 6) {
            Button { manager.toggleServerColumn(shown: columns.servers) } label: { label("Серверы", .server, compact) }
                .winButton(.outline).help("Показать или скрыть колонку «Серверы»")
            Button { manager.toggleSessionColumn(shown: columns.sessions) } label: {
                label(manager.liveCount > 0 ? "Сеансы · \(manager.liveCount)" : "Сеансы", .terminal, compact)
            }.winButton(.outline).help("Показать или скрыть колонку «Сеансы»")
            Button { manager.editingSettings = true } label: {
                if compact { GlyphView(glyph: .settings, color: Palette.of(scheme).teal) }
                else { GlyphLabel(title: "Настройки", glyph: .settings, kind: .quiet) }
            }.winButton(.quiet).help("Настройки")
            ThemeButton(manager: manager, compact: compact)
            Button { manager.addCatalogEntry() } label: { GlyphLabel(title: "Добавить сервер", glyph: .plus, kind: .primary) }
                .winButton(.primary)
        }
        .fixedSize()
    }

    @ViewBuilder
    private func label(_ title: String, _ glyph: Glyph, _ compact: Bool) -> some View {
        if compact { GlyphView(glyph: glyph, color: Palette.of(scheme).teal) } else { GlyphLabel(title: title, glyph: glyph) }
    }
}

/// Column title with one action on the right («Каталог», «+»).
private struct ColumnTitle<Action: View>: View {
    let title: String
    @ViewBuilder let action: Action

    var body: some View {
        HStack(spacing: 6) {
            Text(title).font(.system(size: 13, weight: .semibold)).lineLimit(1)
            Spacer(minLength: 4)
            action
        }
        .frame(height: 36)
    }
}

/// «Серверы»: protocol switch, search, recent and all servers; opening one returns to its session.
private struct ServerColumn: View {
    @ObservedObject var manager: Manager

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            ColumnTitle(title: "Серверы") {
                Menu { ColumnMenu(manager: manager) } label: { Text("⋯").font(.system(size: 15, weight: .semibold)) }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                    .help("Вкладки, сортировка, резервные копии и импорт")
            }
            ServerPicker(manager: manager, onSelected: {})
        }
        .padding(.bottom, 8)
    }
}

/// «Сеансы»: search, open and ended sessions, «+» for a new one and «Открытые файлы» (docs/ui-spec.md §3).
private struct SessionColumn: View {
    @ObservedObject var manager: Manager
    @State private var query = ""
    @FocusState private var searchFocused: Bool
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let palette = Palette.of(scheme)
        let entries = manager.entries.filter { entry in
            let search = query.trimmingCharacters(in: .whitespaces)
            return search.isEmpty || [entry.title, entry.address, manager.groupName(of: entry), entry.protocolName]
                .contains { $0.localizedCaseInsensitiveContains(search) }
        }
        VStack(alignment: .leading, spacing: 6) {
            ColumnTitle(title: "Сеансы") {
                Button { manager.choosingServer = true } label: { GlyphView(glyph: .plus, color: palette.teal) }
                    .winButton(.quiet).help("Новый сеанс")
            }
            SearchField(placeholder: "Найти сеанс", text: $query, focus: $searchFocused) {
                if let first = entries.first { manager.activate(first.id) }
            }
            Text("Открыто: \(manager.liveCount)" + (manager.liveCount == manager.entries.count ? "" : " из \(manager.entries.count)"))
                .font(.system(size: 12)).foregroundStyle(palette.muted).padding(.leading, 2)
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(entries) { entry in
                        SessionListRow(entry: entry, color: manager.dotColor(of: entry) ?? palette.teal,
                                       selected: manager.workspace == .session && entry.id == manager.activeSession, palette: palette)
                            .contentShape(Rectangle())
                            .onTapGesture { manager.activate(entry.id) }
                            .contextMenu { SessionMenu(manager: manager, entry: entry) }
                    }
                }
            }
            .background(palette.surface)
            .overlay(Rectangle().strokeBorder(palette.line, lineWidth: 1).allowsHitTesting(false))
            if !manager.openedFiles.isEmpty {
                Text("Открытые файлы").font(.system(size: 13, weight: .semibold)).padding(.top, 8).padding(.leading, 2)
                VStack(spacing: 0) {
                    ForEach(manager.openedFiles, id: \.profile.id) { files in
                        FilesRow(files: files, color: manager.group(of: files.profile)?.marker?.color ?? palette.teal,
                                 selected: manager.workspace == .files(files.profile.id), palette: palette)
                            .contentShape(Rectangle())
                            .onTapGesture { manager.showFiles(files) }
                            .contextMenu {
                                Button { manager.showFiles(files) } label: { Label("Открыть", systemImage: "folder") }
                                Button(role: .destructive) { manager.closeFiles(files) } label: { Label("Закрыть файлы", systemImage: "xmark") }
                            }
                    }
                }
                .background(palette.surface)
                .overlay(Rectangle().strokeBorder(palette.line, lineWidth: 1).allowsHitTesting(false))
            }
        }
        .padding(.bottom, 8)
        .onReceive(NotificationCenter.default.publisher(for: .focusSessionSearch)) { _ in searchFocused = true }
    }
}

/// «⋯» of the «Серверы» column (docs/ui-spec.md §2.2).
private struct ColumnMenu: View {
    @ObservedObject var manager: Manager

    private static let orders: [(ServerSort, String)] = [(.original, "Исходный порядок"), (.name, "По названию ↑"),
        (.nameDescending, "По названию ↓"), (.address, "По адресу ↑"), (.addressDescending, "По адресу ↓")]

    var body: some View {
        Button { manager.groupEditor = GroupEditorState() } label: { Label("Новая вкладка…", systemImage: "plus") }
        Menu {
            ForEach(Self.orders, id: \.0) { pair in
                Button { manager.sortCatalog(pair.0, kind: manager.catalogProtocol, group: nil) } label: {
                    if manager.columnSort == pair.0 { Label(pair.1, systemImage: "checkmark") } else { Text(pair.1) }
                }
            }
        } label: { Label("Сортировка", systemImage: "arrow.up.arrow.down") }
        Divider()
        Button { manager.reloadAll() } label: { Label("Обновить список", systemImage: "arrow.clockwise") }
        Button { manager.exportBackup() } label: { Label("Экспорт архива…", systemImage: "square.and.arrow.up") }
        Button { manager.importBackup() } label: { Label("Импорт архива…", systemImage: "square.and.arrow.down") }
        Button { manager.importing = true } label: { Label("Импорт из других программ…", systemImage: "folder") }
        Button { manager.transferDesktop(nil, group: nil) } label: { Label("Импорт .rdp…", systemImage: "display") }
        Divider()
        Button { manager.openConfig() } label: { Label("Открыть SSH config", systemImage: "doc.text") }
        Button { manager.openKnownHosts(nil) } label: { Label("Известные хосты…", systemImage: "key.horizontal") }
    }
}

/// The workspace without a session or files (docs/ui-spec.md §2.3).
private struct StartPage: View {
    @ObservedObject var manager: Manager
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let palette = Palette.of(scheme)
        let empty = manager.profiles.isEmpty && manager.settings.desktops.isEmpty && manager.settings.vnc.isEmpty
        VStack(spacing: 10) {
            Text(empty ? "Добавьте первый сервер" : "Выберите сервер слева").font(.system(size: 26, weight: .semibold))
            Text(empty ? "SSH-серверы сохраняются в ~/.ssh/config, RDP и VNC — в настройках приложения. Можно перенести подключения из PuTTY, mRemoteNG или MobaXterm."
                 : "Двойной щелчок или Enter открывает сеанс. Правая кнопка — изменить, открыть файлы, перенести во вкладку. Быстрое подключение — ⌘L.")
                .foregroundStyle(palette.muted).multilineTextAlignment(.center).frame(maxWidth: 560)
            HStack(spacing: 8) {
                Button { manager.addCatalogEntry() } label: { GlyphLabel(title: "Добавить сервер", glyph: .plus, kind: .primary) }
                    .winButton(.primary)
                Button { manager.importing = true } label: { GlyphLabel(title: "Импорт из других программ…", glyph: .folder) }
                    .winButton(.outline)
            }.padding(.top, 8)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// «Открытые файлы» row: the server and «Файлы · SFTP» once connected.
private struct FilesRow: View {
    @ObservedObject var files: FilesController
    let color: Color
    let selected: Bool
    let palette: Palette

    var body: some View {
        RowLayout(title: files.profile.alias, subtitle: files.online ? "Файлы · SFTP" : "Файлы", color: color, selected: selected, palette: palette)
    }
}

/// The catalog table, the active session or a «Файлы» panel.
private struct WorkspaceArea: View {
    @ObservedObject var manager: Manager

    var body: some View {
        switch manager.workspace {
        case .catalog:
            StartPage(manager: manager)
        case .session:
            VStack(alignment: .leading, spacing: 7) {
                SessionInfo(manager: manager, entry: manager.activeEntry, groupName: manager.activeEntry.map { manager.groupName(of: $0) } ?? "")
                SessionContent(manager: manager, entry: manager.activeEntry)
            }
            .padding(.bottom, 8)
        case .files(let id):
            if let files = manager.openedFiles.first(where: { $0.profile.id == id }) {
                FilesView(files: files, onClose: { manager.closeFiles(files) }).id(id).padding(.bottom, 8)
            } else {
                Color.clear
            }
        }
    }
}

private extension Manager {
    func groupName(of entry: SessionEntry) -> String {
        switch entry {
        case .ssh(let session): group(of: session.profile)?.name ?? ""
        case .rdp(let session): group(of: session.profile)?.name ?? ""
        case .vnc(let session): group(of: session.profile)?.name ?? ""
        }
    }

    func dotColor(of entry: SessionEntry) -> Color? {
        switch entry {
        case .ssh(let session): group(of: session.profile)?.marker?.color
        case .rdp(let session): group(of: session.profile)?.marker?.color
        case .vnc(let session): group(of: session.profile)?.marker?.color
        }
    }
}

private struct ThemeButton: View {
    @ObservedObject var manager: Manager
    var compact = false
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let dark = scheme == .dark
        Button {
            manager.updateSettings { $0.appearance = dark ? .light : .dark }
        } label: {
            if compact { GlyphView(glyph: dark ? .sun : .moon, color: Palette.of(scheme).teal) }
            else { GlyphLabel(title: dark ? "Светлая тема" : "Тёмная тема", glyph: dark ? .sun : .moon) }
        }
            .winButton(.outline)
            .help(dark ? "Светлая тема" : "Тёмная тема")
    }
}

private struct SearchField: View {
    let placeholder: String
    @Binding var text: String
    var focus: FocusState<Bool>.Binding
    var submit: () -> Void = {}
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let palette = Palette.of(scheme)
        HStack(spacing: 8) {
            GlyphView(glyph: .browse, color: palette.muted)
            TextField(placeholder, text: $text)
                .textFieldStyle(.plain)
                .focused(focus)
                .onSubmit(submit)
                .accessibilityLabel(placeholder)
        }
        .padding(.horizontal, 10)
        .frame(height: 34)
        .background(RoundedRectangle(cornerRadius: 7).fill(palette.surface))
        .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(focus.wrappedValue ? palette.teal : palette.line, lineWidth: 1))
    }
}

private struct Footer: View {
    @ObservedObject var manager: Manager
    @ObservedObject var updates: MacUpdates
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let palette = Palette.of(scheme)
        HStack(spacing: 12) {
            Text(manager.catalogProtocol == .ssh ? manager.configPath : "~/Library/Application Support/SSH Manager/tabs.xml")
                .foregroundStyle(palette.muted).lineLimit(1).truncationMode(.middle)
            Spacer()
            Text("Подключений: \(manager.serverCount)").foregroundStyle(palette.muted)
            Button {
                if updates.available != nil { updates.presented = true } else { updates.check(manual: true) }
            } label: {
                GlyphLabel(title: updates.checking ? "Проверка…" : updates.installing ? "Загрузка…" :
                            updates.available.map { "Обновить до \($0.version)" } ?? "Обновления", glyph: .refresh)
            }
            .winButton(.outline)
            .frame(width: 170)
            .disabled(updates.checking || updates.installing)
        }
        .font(.system(size: 12))
        .padding(.horizontal, 16)
        .frame(height: 47)
    }
}

// MARK: - Sessions


/// The row menu of the catalog table, also used by the «Серверы» column (docs/ui-spec.md §2.7, §2.8, §8).
private struct CatalogRowMenu: View {
    @ObservedObject var manager: Manager
    var ssh: Profile?
    var rdp: DesktopProfile?
    var vnc: VncProfile?

    var body: some View {
        if let ssh { sshMenu(ssh) }
        if let rdp { rdpMenu(rdp) }
        if let vnc { vncMenu(vnc) }
    }

    @ViewBuilder
    func sshMenu(_ profile: Profile) -> some View {
        let current = manager.group(of: profile)?.id
        Button { manager.connect(profile) } label: { Label("Подключиться", systemImage: "terminal") }
        Button { manager.connect(profile, new: true) } label: { Label("Новый сеанс", systemImage: "plus") }
        Button { manager.openFiles(profile) } label: { Label("Файлы (SFTP)", systemImage: "folder") }
        Button { manager.openInTerminal(profile) } label: { Label("Открыть в Терминале", systemImage: "apple.terminal") }
        Button { manager.editor = ProfileEditorState(original: profile) } label: { Label("Изменить", systemImage: "pencil") }
        favoriteItem(Manager.key(profile))
        Menu {
            Button { manager.move(profile, to: nil) } label: {
                if current == nil { Label("Без вкладки", systemImage: "checkmark") } else { Text("Без вкладки") }
            }
            ForEach(manager.settings.groups) { group in
                Button { manager.move(profile, to: group.id) } label: {
                    if current == group.id { Label(group.name, systemImage: "checkmark") } else { Text(group.name) }
                }
            }
            Divider()
            Button { manager.moveToNewGroup(profile) } label: { Label("Новая вкладка…", systemImage: "plus") }
        } label: { Label("Переместить во вкладку", systemImage: "arrow.right") }
        Button { manager.copyCommand(profile) } label: { Label("Копировать команду SSH", systemImage: "doc.on.doc") }
        Button { manager.copyKey(to: profile) } label: { Label("Скопировать ключ на сервер", systemImage: "key") }
        Button { manager.openKnownHosts(profile) } label: { Label("Известные хосты…", systemImage: "key.horizontal") }
        Divider()
        Button(role: .destructive) { manager.remove(profile) } label: { Label("Удалить", systemImage: "trash") }
    }

    private func favoriteItem(_ key: String) -> some View {
        let favorite = manager.isFavorite(key)
        return Button { manager.toggleFavorite(key) } label: {
            Label(favorite ? "Убрать из избранного" : "В избранное", systemImage: favorite ? "star.slash" : "star")
        }
    }

    @ViewBuilder
    func rdpMenu(_ desktop: DesktopProfile) -> some View {
        Button { manager.connectDesktop(desktop) } label: { Label("Подключиться", systemImage: "server.rack") }
        Button { manager.connectDesktop(desktop, new: true) } label: { Label("Новый сеанс", systemImage: "plus") }
        Button { manager.desktopEditor = DesktopEditorState(original: desktop, group: desktop.group) } label: {
            Label("Изменить", systemImage: "pencil")
        }
        favoriteItem(Manager.key(desktop))
        Menu {
            Button { manager.moveDesktop(desktop, to: nil) } label: {
                if desktop.group == nil { Label("Без вкладки", systemImage: "checkmark") } else { Text("Без вкладки") }
            }
            ForEach(manager.settings.groups) { group in
                Button { manager.moveDesktop(desktop, to: group.id) } label: {
                    if desktop.group == group.id { Label(group.name, systemImage: "checkmark") } else { Text(group.name) }
                }
            }
        } label: { Label("Переместить во вкладку", systemImage: "arrow.right") }
        Button { manager.transferDesktop(desktop) } label: { Label("Экспорт .rdp…", systemImage: "doc.on.doc") }
        Divider()
        Button(role: .destructive) { manager.removeDesktop(desktop) } label: { Label("Удалить", systemImage: "trash") }
    }
    @ViewBuilder
    func vncMenu(_ profile: VncProfile) -> some View {
        Button { manager.connectVnc(profile) } label: { Label("Подключиться", systemImage: "server.rack") }
        Button { manager.connectVnc(profile, new: true) } label: { Label("Новый сеанс", systemImage: "plus") }
        Button { manager.connectVnc(profile, new: true) } label: { Label("Открыть в отдельном окне", systemImage: "macwindow") }
        Button { manager.openFiles(profile) } label: { Label("Файлы (SFTP)", systemImage: "folder") }
        Button { manager.editVnc(profile) } label: { Label("Изменить", systemImage: "pencil") }
        favoriteItem(Manager.key(profile))
        Menu {
            Button { manager.moveVnc(profile, to: nil) } label: {
                if profile.group == nil { Label("Без вкладки", systemImage: "checkmark") } else { Text("Без вкладки") }
            }
            ForEach(manager.settings.groups) { group in
                Button { manager.moveVnc(profile, to: group.id) } label: {
                    if profile.group == group.id { Label(group.name, systemImage: "checkmark") } else { Text(group.name) }
                }
            }
        } label: { Label("Переместить во вкладку", systemImage: "arrow.right") }
        Divider()
        Button(role: .destructive) { manager.removeVnc(profile) } label: { Label("Удалить", systemImage: "trash") }
    }}

/// Sidebar row: group dot, title, «SSH · user@host:port» or «… · Сеанс завершён».
private struct SessionListRow: View {
    let entry: SessionEntry
    let color: Color
    let selected: Bool
    let palette: Palette

    var body: some View {
        switch entry {
        case .ssh(let session): SSHRow(session: session, color: color, selected: selected, palette: palette)
        case .rdp(let session): RemoteRow(session: session, protocolName: "RDP", color: color, selected: selected, palette: palette)
        case .vnc(let session): RemoteRow(session: session, protocolName: "VNC", color: color, selected: selected, palette: palette)
        }
    }
}

private struct SSHRow: View {
    @ObservedObject var session: TerminalSession
    let color: Color
    let selected: Bool
    let palette: Palette
    var body: some View {
        RowLayout(title: session.title, subtitle: "SSH · " + (session.live ? session.address : "Сеанс завершён"),
                  color: session.live ? color : palette.line, selected: selected, palette: palette)
    }
}

private struct RemoteRow<Session: RemoteDesktopSession>: View {
    @ObservedObject var session: Session
    let protocolName: String
    let color: Color
    let selected: Bool
    let palette: Palette
    var body: some View {
        RowLayout(title: session.title, subtitle: protocolName + " · " + (session.live ? session.address : "Сеанс завершён"),
                  color: session.live ? color : palette.line, selected: selected, palette: palette)
    }
}

private struct RowLayout: View {
    let title, subtitle: String
    let color: Color
    let selected: Bool
    let palette: Palette
    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Circle().fill(color).frame(width: 7, height: 7).padding(.top, 6)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.system(size: 13, weight: .semibold)).lineLimit(1)
                Text(subtitle).font(.system(size: 12)).foregroundStyle(palette.muted).lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 9)
        .frame(height: 46)
        .background(selected ? palette.selection : Color.clear)
    }
}

private struct SessionMenu: View {
    @ObservedObject var manager: Manager
    let entry: SessionEntry

    var body: some View {
        Button { manager.renaming = entry.id } label: { Label("Переименовать сеанс", systemImage: "pencil") }
        Button { manager.duplicate(entry) } label: { Label("Новый сеанс этого сервера", systemImage: "plus") }
        switch entry {
        case .ssh(let session):
            Button { manager.openFiles(session.profile) } label: { Label("Файлы (SFTP)", systemImage: "folder") }
            if session.forwardingFailed {
                Button { manager.duplicate(entry, noForwarding: true) } label: {
                    Label("Новый сеанс без проброса портов", systemImage: "terminal")
                }
            }
        case .rdp(let session):
            remoteActions(fullScreen: session.live && session.supportsFullScreen, label: "Полноэкранный режим RDP")
        case .vnc(let session):
            Button { manager.openFiles(session.profile) } label: { Label("Файлы (SFTP)", systemImage: "folder") }
            remoteActions(fullScreen: session.live && session.supportsFullScreen, label: "Весь экран")
        }
        Divider()
        Button(role: .destructive) { manager.closeSession(entry) } label: { Label("Закрыть сеанс", systemImage: "trash") }
    }
    @ViewBuilder
    private func remoteActions(fullScreen: Bool, label: String) -> some View {
        if fullScreen {
            Button { manager.fullScreen(entry) } label: { Label(label, systemImage: "rectangle.inset.filled") }
        }
        Button { manager.reconnect(entry) } label: { Label("Переподключить", systemImage: "arrow.clockwise") }
    }
}

/// Title, buttons and the detail line «протокол  ·  адрес  ·  вкладка  ·  статус» (docs/ui-spec.md §3.3).
private struct SessionInfo: View {
    @ObservedObject var manager: Manager
    let entry: SessionEntry?
    let groupName: String

    var body: some View {
        switch entry {
        case .ssh(let session): SSHInfo(manager: manager, session: session, entry: entry!, groupName: groupName)
        case .rdp(let session): RemoteInfo(manager: manager, session: session, entry: entry!, groupName: groupName)
        case .vnc(let session): RemoteInfo(manager: manager, session: session, entry: entry!, groupName: groupName, route: session.route)
        case nil: InfoLayout(title: "Сеансы SSH, RDP и VNC", detail: "") { EmptyView() }
        }
    }
}

private struct InfoLayout<Buttons: View>: View {
    let title: String
    let detail: String
    @ViewBuilder let buttons: Buttons
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 6) {
                Text(title).font(.system(size: 13, weight: .semibold)).lineLimit(1).layoutPriority(-1)
                Spacer(minLength: 8)
                buttons
            }
            Text(detail).font(.system(size: 12)).foregroundStyle(Palette.of(scheme).muted).lineLimit(1)
        }
        .frame(height: 58)
    }
}

private func detailLine(_ parts: [String]) -> String { parts.filter { !$0.isEmpty }.joined(separator: "  ·  ") }

private struct SSHInfo: View {
    @ObservedObject var manager: Manager
    @ObservedObject var session: TerminalSession
    let entry: SessionEntry
    let groupName: String

    var body: some View {
        let forwarding = session.forwardingFailed ? "ошибка проброса порта" : session.noForwarding ? "без проброса" : ""
        InfoLayout(title: session.title,
                   detail: detailLine(["SSH", session.address, groupName, forwarding, session.live ? "" : "завершён"])) {
            Button { manager.openFiles(session.profile) } label: { GlyphLabel(title: "Файлы", glyph: .folder) }.winButton(.outline)
            SnippetsMenu(manager: manager, session: session)
            Button { manager.duplicate(entry) } label: { GlyphLabel(title: "Новый сеанс этого сервера", glyph: .plus) }.winButton(.quiet)
            Button { manager.closeSession(entry) } label: { GlyphLabel(title: "Закрыть", glyph: nil, danger: true) }
                .winButton(.quiet, danger: true)
        }
    }
}

private struct RemoteInfo<Session: RemoteDesktopSession>: View {
    @ObservedObject var manager: Manager
    @ObservedObject var session: Session
    let entry: SessionEntry
    let groupName: String
    var route = ""

    var body: some View {
        InfoLayout(title: session.title, detail: detailLine([entry.protocolName, session.address, groupName, session.status, route])) {
            if case .vnc(let vnc) = entry {
                Button { manager.openFiles(vnc.profile) } label: { GlyphLabel(title: "Файлы", glyph: .folder) }.winButton(.outline)
            }
            Button { manager.reconnect(entry) } label: { GlyphLabel(title: "Переподключить", glyph: .refresh) }.winButton(.outline)
            Button { manager.fullScreen(entry) } label: { GlyphLabel(title: "Весь экран", glyph: .server) }
                .winButton(.outline).disabled(!session.live || !session.supportsFullScreen)
            Button { manager.duplicate(entry) } label: { GlyphLabel(title: "Новый сеанс этого сервера", glyph: .plus) }.winButton(.quiet)
            Button { manager.closeSession(entry) } label: { GlyphLabel(title: "Закрыть", glyph: nil, danger: true) }
                .winButton(.quiet, danger: true)
        }
    }
}

private struct SessionContent: View {
    @ObservedObject var manager: Manager
    let entry: SessionEntry?

    var body: some View {
        ZStack {
            Palette.terminalBackground
            switch entry {
            case .ssh(let session): SSHContent(manager: manager, session: session, entry: entry!).id(session.id)
            case .rdp(let session): RemoteContent(manager: manager, session: session, entry: entry!)
            case .vnc(let session): RemoteContent(manager: manager, session: session, entry: entry!)
            case nil:
                Text("Выберите сервер в каталоге, чтобы начать сеанс.").foregroundStyle(Palette.terminalText)
            }
        }
    }
}

private struct SSHContent: View {
    @ObservedObject var manager: Manager
    @ObservedObject var session: TerminalSession
    let entry: SessionEntry

    var body: some View {
        VStack(spacing: 0) {
            TerminalView(session: session)
            if !session.live { EndedBar(manager: manager, session: session, entry: entry) }
        }
    }
}

/// «Сеанс SSH завершён» under the terminal with «Переподключить» and «Закрыть сеанс» (docs/ui-spec.md §7).
private struct EndedBar: View {
    @ObservedObject var manager: Manager
    @ObservedObject var session: TerminalSession
    let entry: SessionEntry
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let palette = Palette.of(scheme)
        let text = manager.pendingReconnect[session.id].map { "Соединение прервано · повтор через 5 с (попытка \($0) из 3)" }
            ?? "Сеанс SSH завершён (код \(session.exitCode ?? 0))"
        HStack(spacing: 8) {
            Text(text).lineLimit(1).truncationMode(.tail).foregroundStyle(palette.ink)
            Spacer(minLength: 8)
            if session.remoteCommand == nil {
                Button { manager.reconnect(entry) } label: { GlyphLabel(title: "Переподключить", glyph: .refresh, kind: .primary) }
                    .winButton(.primary)
            }
            Button { manager.closeSession(entry) } label: { GlyphLabel(title: "Закрыть сеанс", glyph: nil) }.winButton(.outline)
        }
        .padding(.top, 7)
        .frame(height: 44)
        .background(palette.canvas)
    }
}

/// «Команды»: saved commands, «Во все открытые сеансы», «Добавить…» and «Удалить».
private struct SnippetsMenu: View {
    @ObservedObject var manager: Manager
    @ObservedObject var session: TerminalSession
    @State private var everywhere = false

    var body: some View {
        Menu {
            if manager.settings.snippets.isEmpty { Text("Сохранённых команд пока нет.") }
            ForEach(manager.settings.snippets) { snippet in
                Button(snippet.name) { manager.send(snippet, to: session, everywhere: everywhere) }.help(snippet.command)
            }
            Divider()
            Toggle("Во все открытые сеансы", isOn: $everywhere)
            Button("Добавить…") { manager.addingSnippet = true }
            if !manager.settings.snippets.isEmpty {
                Menu("Удалить") {
                    ForEach(manager.settings.snippets) { snippet in Button(snippet.name) { manager.removeSnippet(snippet) } }
                }
            }
        } label: { GlyphLabel(title: "Команды", glyph: .terminal) }
            .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
            .padding(.horizontal, 8)
            .disabled(!session.live)
            .help("Сохранённые команды")
    }
}

/// «Быстрое подключение»: user@host:port or rdp://host, Enter connects (docs/ui-spec.md §7).
private struct QuickConnectField: View {
    @ObservedObject var manager: Manager
    @State private var text = ""
    @FocusState private var focused: Bool
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let palette = Palette.of(scheme)
        HStack(spacing: 8) {
            GlyphView(glyph: .terminal, color: palette.muted)
            TextField("Быстрое подключение: user@host, rdp:// или vnc://", text: $text)
                .textFieldStyle(.plain)
                .focused($focused)
                .onSubmit {
                    let value = text.trimmingCharacters(in: .whitespaces)
                    guard !value.isEmpty else { return }
                    text = ""
                    manager.quickConnect(value)
                }
                .onExitCommand { text = "" }
                .accessibilityLabel("Быстрое подключение")
        }
        .padding(.horizontal, 10)
        .frame(minWidth: 240, maxWidth: 460, minHeight: 32, maxHeight: 32)
        .background(RoundedRectangle(cornerRadius: 7).fill(palette.surface))
        .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(focused ? palette.teal : palette.line, lineWidth: 1))
        .help("Подключиться без сохранения профиля. Enter — подключиться.")
        .onReceive(NotificationCenter.default.publisher(for: .focusQuickConnect)) { _ in focused = true }
    }
}

/// Shows where the desktop opened or why the connection ended.
private struct RemoteContent<Session: RemoteDesktopSession>: View {
    @ObservedObject var manager: Manager
    @ObservedObject var session: Session
    let entry: SessionEntry

    var body: some View {
        VStack(spacing: 8) {
            if session.live {
                Text(session.external).foregroundStyle(Palette.terminalText).multilineTextAlignment(.center)
                Text(session.status).font(.system(size: 12)).foregroundStyle(.gray)
            } else {
                Text(session.endedTitle).font(.system(size: 26, weight: .semibold)).foregroundStyle(Palette.terminalText)
                Text(session.endedDetail).foregroundStyle(.gray).multilineTextAlignment(.center).textSelection(.enabled)
                HStack(spacing: 8) {
                    Button { manager.reconnect(entry) } label: { GlyphLabel(title: "Переподключить", glyph: .refresh, kind: .primary) }
                        .winButton(.primary)
                    Button { manager.closeSession(entry) } label: { GlyphLabel(title: "Закрыть сеанс", glyph: nil) }
                        .winButton(.outline)
                }.padding(.top, 12)
            }
        }
        .padding(24)
    }
}

// MARK: - Dialogs

private struct DialogButtons: View {
    let commit: String
    var enabled = true
    let cancel: () -> Void
    let submit: () -> Void

    var body: some View {
        HStack {
            Spacer()
            Button("Отмена", action: cancel).keyboardShortcut(.cancelAction).winButton(.outline)
            Button(commit, action: submit).keyboardShortcut(.defaultAction).winButton(.primary).disabled(!enabled)
        }
    }
}

private func privateKeys() -> [String] {
    let folder = ConfigStore.standard.url.deletingLastPathComponent()
    guard let names = try? FileManager.default.contentsOfDirectory(atPath: folder.path) else { return [] }
    return names.sorted().compactMap { name in
        let url = folder.appendingPathComponent(name)
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        let head = String(decoding: (try? handle.read(upToCount: 80)) ?? Data(), as: UTF8.self)
        return head.hasPrefix("-----BEGIN") && head.contains("PRIVATE KEY-----") ? "~/.ssh/" + name : nil
    }
}

private struct ProfileEditor: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme
    @State private var profile: Profile
    @State private var password = ""
    @State private var removePassword = false
    @State private var failure: String?
    @State private var keys: [String]
    @State private var chosenKey: String?
    @State private var available: [String] = privateKeys()
    @State private var forwardKind = 0
    @State private var forwardValue = ""
    @State private var selectedForward: String?
    @State private var creatingKey = false
    @State private var keyName = ""
    @State private var keyMessage: String?
    private static let forwardNames = ["LocalForward", "RemoteForward", "DynamicForward"]
    private static let forwardTitles = ["L — локальный", "R — удалённый", "D — SOCKS"]
    private let state: ProfileEditorState
    private let hasPassword: Bool
    private let save: (Profile, String, Bool) throws -> Void

    init(state: ProfileEditorState, hasPassword: Bool, defaults: Preferences = Preferences(),
         save: @escaping (Profile, String, Bool) throws -> Void) {
        self.state = state
        self.hasPassword = hasPassword
        self.save = save
        var start = state.original ?? Profile()
        if state.original == nil {
            start.user = defaults.defaultUser
            start.port = defaults.defaultPort
        }
        _profile = State(initialValue: start)
        _keys = State(initialValue: start.identities.components(separatedBy: .newlines).filter { !$0.isEmpty })
    }

    private var sameDestination: Bool {
        guard let original = state.original else { return false }
        return original.host == profile.host && original.user == profile.user && original.port == profile.port
    }

    var body: some View {
        let palette = Palette.of(scheme)
        VStack(alignment: .leading, spacing: 10) {
            Text(state.original == nil ? "Новое подключение" : "Изменить подключение").font(.title2.weight(.semibold))
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                GridRow { Text("Псевдоним"); TextField("", text: $profile.alias) }
                GridRow { Text("Хост"); TextField("", text: $profile.host) }
                GridRow { Text("Пользователь"); TextField("", text: $profile.user) }
                GridRow { Text("Порт"); TextField("", text: $profile.port) }
                GridRow { Text("Пароль"); SecureField("", text: $password).disabled(removePassword) }
                GridRow {
                    Color.clear.frame(width: 1, height: 1)
                    HStack {
                        Text(!hasPassword ? "Пароль необязателен. Хранится в Связке ключей." :
                                sameDestination ? "Пароль сохранён. Пустое поле оставит прежний." :
                                "Для нового адреса, порта или пользователя введите пароль заново.")
                            .foregroundStyle(palette.muted)
                        if hasPassword && sameDestination { Toggle("Удалить пароль", isOn: $removePassword) }
                    }.font(.callout)
                }
                GridRow {
                    Color.clear.frame(width: 1, height: 1)
                    Text("Пустые поля используют настройки OpenSSH.").foregroundStyle(palette.muted).font(.callout)
                }
                GridRow { Text("Через сервер"); TextField("ProxyJump: bastion или user@host:port", text: $profile.proxyJump) }
                GridRow { Text("Файл known_hosts"); TextField("~/.ssh/known_hosts (по умолчанию)", text: $profile.knownHosts) }
                GridRow(alignment: .top) {
                    Text("Пробросы портов")
                    VStack(alignment: .leading, spacing: 6) {
                        List(profile.forwards, id: \.self, selection: $selectedForward) { forward in
                            Text(forward).lineLimit(1).truncationMode(.middle)
                        }.frame(height: 60).listStyle(.bordered)
                        HStack(spacing: 6) {
                            Picker("", selection: $forwardKind) {
                                ForEach(0..<3, id: \.self) { index in Text(Self.forwardTitles[index]).tag(index) }
                            }.labelsHidden().frame(width: 150)
                            TextField(forwardKind == 2 ? "1080" : "8080 localhost:80", text: $forwardValue).onSubmit(addForward)
                            Button { addForward() } label: { GlyphLabel(title: "Добавить", glyph: .plus) }.winButton(.outline)
                            Button { profile.forwards.removeAll { $0 == selectedForward } } label: { GlyphLabel(title: "Убрать", glyph: .delete) }
                                .winButton(.outline).disabled(selectedForward == nil)
                        }
                    }
                }
                GridRow {
                    Text("Доступные ключи")
                    HStack {
                        Picker("", selection: $chosenKey) {
                            ForEach(available, id: \.self) { key in Text(key).tag(Optional(key)) }
                        }.labelsHidden()
                        Button { browse() } label: { GlyphLabel(title: "Обзор…", glyph: .browse) }.winButton(.outline)
                    }
                }
                GridRow {
                    Color.clear.frame(width: 1, height: 1)
                    Text(available.isEmpty ? "В папке .ssh ключи не найдены. Можно указать файл через «Обзор»." : "Ключи найдены в папке .ssh.")
                        .foregroundStyle(palette.muted).font(.callout)
                }
                GridRow(alignment: .top) {
                    Text("Выбранные ключи")
                    List(keys, id: \.self, selection: Binding(get: { selectedKey }, set: { selectedKey = $0 })) { key in
                        Text(key).lineLimit(1).truncationMode(.middle)
                    }.frame(height: 86).listStyle(.bordered)
                }
                GridRow {
                    Color.clear.frame(width: 1, height: 1)
                    HStack(spacing: 6) {
                        Button { addKey() } label: { GlyphLabel(title: "Добавить ключ", glyph: .key) }.winButton(.outline)
                        Button { replaceKey() } label: { GlyphLabel(title: "Заменить", glyph: .edit) }.winButton(.outline)
                        Button { keys.removeAll { $0 == selectedKey } } label: { GlyphLabel(title: "Убрать", glyph: .delete) }.winButton(.outline)
                        Button {
                            let alias = profile.alias.trimmingCharacters(in: .whitespaces)
                            keyName = alias.isEmpty ? "id_ed25519" : "id_ed25519_" + alias
                            creatingKey = true
                        } label: { GlyphLabel(title: "Создать ключ…", glyph: .plus) }.winButton(.outline)
                    }
                }
                if let keyMessage {
                    GridRow {
                        Color.clear.frame(width: 1, height: 1)
                        Text(keyMessage).foregroundStyle(palette.muted).font(.callout).fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .textFieldStyle(.roundedBorder)
            if let failure { Text(failure).foregroundStyle(palette.danger).font(.callout) }
            DialogButtons(commit: "Сохранить", enabled: !profile.alias.trimmingCharacters(in: .whitespaces).isEmpty,
                          cancel: { dismiss() }, submit: submit)
        }
        .padding(22).frame(width: 680)
        .onAppear { if chosenKey == nil { chosenKey = available.first } }
        .alert("Создать ключ", isPresented: $creatingKey) {
            TextField("Имя файла в ~/.ssh", text: $keyName)
            Button("Создать") { createKey() }
            Button("Отмена", role: .cancel) {}
        } message: {
            Text("Ключ ed25519 без пароля в папке ~/.ssh.")
        }
    }

    private func addForward() {
        let value = forwardValue.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        guard !value.isEmpty else { return }
        let forward = Self.forwardNames[forwardKind] + " " + value
        if !profile.forwards.contains(forward) { profile.forwards.append(forward) }
        selectedForward = forward
        forwardValue = ""
    }

    /// «Создать ключ…»: ssh-keygen ed25519, then the new key is selected.
    private func createKey() {
        do {
            let path = try Keys.generate(name: keyName.trimmingCharacters(in: .whitespaces))
            if !available.contains(path) { available.insert(path, at: 0) }
            chosenKey = path
            if !keys.contains(path) { keys.append(path) }
            keyMessage = "Ключ создан: \(path). Публичную часть можно скопировать на сервер через меню строки «Скопировать ключ на сервер»."
        } catch { failure = error.localizedDescription }
    }

    @State private var selectedKey: String?

    private func addKey() {
        if let key = chosenKey, !keys.contains(key) { keys.append(key) }
    }

    private func replaceKey() {
        guard let key = chosenKey, let selectedKey, let index = keys.firstIndex(of: selectedKey), !keys.contains(key) else { return }
        keys[index] = key
    }

    private func browse() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.showsHiddenFiles = true
        panel.directoryURL = ConfigStore.standard.url.deletingLastPathComponent()
        if panel.runModal() == .OK, let url = panel.url {
            let path = url.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")
            if !available.contains(path) { available.insert(path, at: 0) }
            chosenKey = path
        }
    }

    private func submit() {
        profile.alias = profile.alias.trimmingCharacters(in: .whitespacesAndNewlines)
        profile.host = profile.host.trimmingCharacters(in: .whitespacesAndNewlines)
        profile.user = profile.user.trimmingCharacters(in: .whitespacesAndNewlines)
        profile.port = profile.port.trimmingCharacters(in: .whitespacesAndNewlines)
        profile.proxyJump = profile.proxyJump.trimmingCharacters(in: .whitespacesAndNewlines)
        profile.knownHosts = profile.knownHosts.trimmingCharacters(in: .whitespacesAndNewlines)
        profile.identities = keys.joined(separator: "\n")
        do { try save(profile, removePassword ? "" : password, removePassword); dismiss() }
        catch { failure = error.localizedDescription }
    }
}

private struct GroupEditor: View {
    @Environment(\.dismiss) private var dismiss
    @State private var name: String
    @State private var failure: String?
    let state: GroupEditorState
    let save: (String) throws -> Void

    init(state: GroupEditorState, save: @escaping (String) throws -> Void) {
        self.state = state
        self.save = save
        _name = State(initialValue: state.original?.name ?? "")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(state.original == nil ? "Новая вкладка" : "Переименовать вкладку").font(.title3.weight(.semibold))
            Text("Название вкладки").font(.headline)
            TextField("", text: $name).textFieldStyle(.roundedBorder)
                .onChange(of: name) { _, value in if value.count > 32 { name = String(value.prefix(32)) } }
            if let failure { Text(failure).foregroundStyle(.red).font(.callout) }
            DialogButtons(commit: state.original == nil ? "Создать" : "Сохранить", enabled: !name.isEmpty, cancel: { dismiss() }) {
                do { try save(name); dismiss() } catch { failure = error.localizedDescription }
            }
        }.padding(20).frame(width: 410)
    }
}

private struct DesktopEditor: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme
    @State private var profile: DesktopProfile
    @State private var port: String
    @State private var width: String
    @State private var height: String
    @State private var error = ""
    @State private var password = ""
    @State private var removePassword = false
    @State private var showAdvanced: Bool
    @State private var selectedDrive: String?
    let creating: Bool
    let hasPassword: Bool
    let importFile: () -> Void
    let save: (DesktopProfile, String, Bool) throws -> Void

    init(state: DesktopEditorState, hasPassword: Bool, importFile: @escaping () -> Void,
         save: @escaping (DesktopProfile, String, Bool) throws -> Void) {
        self.hasPassword = hasPassword
        self.importFile = importFile
        creating = state.original == nil
        let start = state.original ?? DesktopProfile(group: state.group)
        _profile = State(initialValue: start)
        _port = State(initialValue: String(start.port))
        _width = State(initialValue: start.width == 0 ? "" : String(start.width))
        _height = State(initialValue: start.height == 0 ? "" : String(start.height))
        _showAdvanced = State(initialValue: !start.drives.isEmpty || start.printers || start.multimon || !start.gateway.isEmpty)
        self.save = save
    }

    var body: some View {
        let palette = Palette.of(scheme)
        VStack(alignment: .leading, spacing: 10) {
            Text(creating ? "Новое RDP-подключение" : "Изменить RDP-подключение").font(.title2.weight(.semibold))
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                GridRow { Text("Название"); TextField("", text: $profile.name) }
                GridRow { Text("Адрес"); TextField("", text: $profile.host) }
                GridRow { Text("Порт"); TextField("", text: $port) }
                GridRow { Text("Пользователь"); TextField("", text: $profile.user) }
                GridRow {
                    Text("Домен")
                    TextField("", text: $profile.domain)
                        .help("Пусто — учётная запись на самом сервере. Для доменной учётной записи укажите имя домена, например CORP.")
                }
                GridRow { Text("Пароль"); SecureField("", text: $password).disabled(removePassword) }
                GridRow {
                    Color.clear.frame(width: 1, height: 1)
                    HStack {
                        Text(hasPassword ? "Сохранён. Пустое поле — не менять." : "Необязательно. Хранится в Связке ключей.")
                            .foregroundStyle(palette.muted)
                        if hasPassword { Toggle("Удалить пароль", isOn: $removePassword) }
                    }.font(.callout)
                }
                GridRow { Text("Ширина экрана"); TextField("По размеру окна", text: $width) }
                GridRow { Text("Высота экрана"); TextField("По размеру окна", text: $height) }
                GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Общий буфер обмена", isOn: $profile.clipboard) }
                GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Открывать на весь экран", isOn: $profile.fullScreen) }
            }
            .textFieldStyle(.roundedBorder)
            DisclosureGroup("Дополнительно", isExpanded: $showAdvanced) {
                Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                    GridRow(alignment: .top) {
                        Text("Подключать папки")
                        VStack(alignment: .leading, spacing: 6) {
                            List(profile.drives, id: \.self, selection: $selectedDrive) { path in
                                Text(path).lineLimit(1).truncationMode(.middle)
                            }.frame(height: 56).listStyle(.bordered)
                            HStack(spacing: 6) {
                                Button { addDrive() } label: { GlyphLabel(title: "Добавить…", glyph: .plus) }.winButton(.outline)
                                Button { profile.drives.removeAll { $0 == selectedDrive } } label: { GlyphLabel(title: "Убрать", glyph: .delete) }
                                    .winButton(.outline).disabled(selectedDrive == nil)
                            }
                        }
                    }
                    GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Принтеры", isOn: $profile.printers) }
                    GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Все мониторы", isOn: $profile.multimon) }
                    GridRow { Text("Шлюз RD Gateway"); TextField("", text: $profile.gateway) }
                    GridRow { Text("Пользователь шлюза"); TextField("", text: $profile.gatewayUser) }
                }
                .textFieldStyle(.roundedBorder)
                .padding(.top, 6)
            }
            if !error.isEmpty { Text(error).foregroundStyle(palette.danger).font(.callout) }
            HStack {
                if creating {
                    Button { dismiss(); importFile() } label: { GlyphLabel(title: "Импорт .rdp…", glyph: .folder) }.winButton(.outline)
                }
                DialogButtons(commit: "Сохранить", cancel: { dismiss() }, submit: submit)
            }
        }.padding(22).frame(width: 565)
    }

    private func submit() {
        let trim: (String) -> String = { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        var value = profile
        value.name = trim(value.name)
        value.host = trim(value.host).trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        value.domain = trim(value.domain)
        value.user = trim(value.user)
        value.gateway = trim(value.gateway)
        value.gatewayUser = trim(value.gatewayUser)
        guard let parsedPort = trim(port).isEmpty ? 3389 : Int(trim(port)),
              let parsedWidth = trim(width).isEmpty ? 0 : Int(trim(width)),
              let parsedHeight = trim(height).isEmpty ? 0 : Int(trim(height)) else {
            error = "Укажите числовой порт и размеры экрана."
            return
        }
        value.port = parsedPort
        value.width = parsedWidth
        value.height = parsedHeight
        do { try save(value, removePassword ? "" : password, removePassword); dismiss() }
        catch { self.error = error.localizedDescription }
    }

    private func addDrive() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.message = "Папка, которая будет доступна в удалённом сеансе"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        if !profile.drives.contains(url.path) { profile.drives.append(url.path) }
    }
}

private struct VncEditor: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme
    @State private var profile: VncProfile
    @State private var port: String
    @State private var error = ""
    @State private var password = ""
    @State private var removePassword = false
    let creating: Bool
    let hasPassword: Bool
    let sshAliases: [String]
    let save: (VncProfile, String, Bool) throws -> Void

    init(state: VncEditorState, hasPassword: Bool, sshAliases: [String],
         save: @escaping (VncProfile, String, Bool) throws -> Void) {
        creating = state.original == nil
        self.hasPassword = hasPassword
        self.sshAliases = sshAliases
        self.save = save
        var start = state.original ?? VncProfile(group: state.group)
        start.via = sshAliases.first { $0.caseInsensitiveCompare(start.via) == .orderedSame } ?? start.via
        start.filesVia = sshAliases.first { $0.caseInsensitiveCompare(start.filesVia) == .orderedSame } ?? start.filesVia
        _profile = State(initialValue: start)
        _port = State(initialValue: String(start.port))
    }

    var body: some View {
        let palette = Palette.of(scheme)
        VStack(alignment: .leading, spacing: 10) {
            Text(creating ? "Новое VNC-подключение" : "Изменить VNC-подключение").font(.title2.weight(.semibold))
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                GridRow { Text("Название"); TextField("", text: $profile.name) }
                GridRow { Text("Адрес"); TextField("", text: $profile.host) }
                GridRow { Text("Порт"); TextField("5900", text: $port) }
                GridRow { Text("Пользователь"); TextField("", text: $profile.user) }
                GridRow { Text("Пароль"); SecureField("", text: $password).disabled(removePassword) }
                GridRow {
                    Color.clear.frame(width: 1, height: 1)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(hasPassword ? "Сохранён. Пустое поле — не менять." : "Необязательно. Хранится в Связке ключей.")
                        Text("VNC-пароль: учитываются первые 8 символов.")
                        if hasPassword { Toggle("Удалить пароль", isOn: $removePassword) }
                    }.foregroundStyle(palette.muted).font(.callout)
                }
                GridRow {
                    Text("Через SSH-сервер")
                    Picker("Через SSH-сервер", selection: $profile.via) {
                        Text("Напрямую").tag("")
                        if !profile.via.isEmpty && !sshAliases.contains(profile.via) { Text(profile.via).tag(profile.via) }
                        ForEach(sshAliases, id: \.self) { Text($0).tag($0) }
                    }.labelsHidden()
                }
                GridRow {
                    Text("SSH-профиль для файлов")
                    Picker("SSH-профиль для файлов", selection: $profile.filesVia) {
                        Text("Не выбран").tag("")
                        if !profile.filesVia.isEmpty && !sshAliases.contains(profile.filesVia) {
                            Text(profile.filesVia).tag(profile.filesVia)
                        }
                        ForEach(sshAliases, id: \.self) { Text($0).tag($0) }
                    }.labelsHidden()
                }
                GridRow {
                    Text("Масштаб")
                    Picker("Масштаб", selection: $profile.scale) {
                        ForEach(VncProfile.Scale.allCases, id: \.self) { Text($0.title).tag($0) }
                    }.labelsHidden()
                }
                GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Только просмотр", isOn: $profile.viewOnly) }
                GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Общий буфер обмена", isOn: $profile.clipboard) }
                GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Открывать на весь экран", isOn: $profile.fullScreen) }
            }.textFieldStyle(.roundedBorder)
            if !error.isEmpty { Text(error).foregroundStyle(palette.danger).font(.callout) }
            DialogButtons(commit: "Сохранить", cancel: { dismiss() }, submit: submit)
        }.padding(22).frame(width: 565)
    }

    private func submit() {
        let trim: (String) -> String = { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        var value = profile
        value.name = trim(value.name)
        value.host = trim(value.host).trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        value.user = trim(value.user)
        guard let parsedPort = trim(port).isEmpty ? 5900 : Int(trim(port)) else {
            error = "Укажите порт от 1 до 65535."
            return
        }
        value.port = parsedPort
        do { try save(value, removePassword ? "" : password, removePassword); dismiss() }
        catch { self.error = error.localizedDescription }
    }
}

/// «Вход: имя» before an RDP connection without a saved password (docs/ui-spec.md §4.3).
private struct LogonSheet: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme
    let request: DesktopLogonRequest
    @State private var account: String
    @State private var password = ""
    @State private var remember = false

    init(request: DesktopLogonRequest) {
        self.request = request
        _account = State(initialValue: request.profile.account)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Вход: " + request.profile.name).font(.title3.weight(.semibold))
            Text(request.profile.address).foregroundStyle(Palette.of(scheme).muted)
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                GridRow { Text("Пользователь"); TextField("", text: $account) }
                GridRow { Text("Пароль"); SecureField("", text: $password) }
                GridRow { Color.clear.frame(width: 1, height: 1); Toggle("Сохранить пароль в Связке ключей", isOn: $remember) }
            }.textFieldStyle(.roundedBorder)
            DialogButtons(commit: "Подключиться", cancel: { dismiss() }) {
                var user = account.trimmingCharacters(in: .whitespaces)
                var domain = request.profile.domain
                if let slash = user.firstIndex(of: "\\") {
                    domain = String(user[..<slash])
                    user = String(user[user.index(after: slash)...])
                }
                let completion = request.completion
                let entered = password
                let keep = remember
                dismiss()
                DispatchQueue.main.async { completion(user, domain, entered, keep) }
            }
        }.padding(20).frame(width: 460)
    }
}

private struct RenameSheet: View {
    @Environment(\.dismiss) private var dismiss
    let entry: SessionEntry
    let save: (String) throws -> Void
    @State private var label: String
    @State private var failure: String?

    init(entry: SessionEntry, save: @escaping (String) throws -> Void) {
        self.entry = entry
        self.save = save
        _label = State(initialValue: entry.name)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Переименовать сеанс").font(.title3.weight(.semibold))
            Text("Метка сеанса").font(.headline)
            TextField("", text: $label).textFieldStyle(.roundedBorder)
            if let failure { Text(failure).foregroundStyle(.red).font(.callout) }
            DialogButtons(commit: "Сохранить", cancel: { dismiss() }) {
                do { try save(label); dismiss() } catch { failure = error.localizedDescription }
            }
        }.padding(20).frame(width: 410)
    }
}

/// «Новый сеанс»: protocol switch, search, list with group dots and a status line (docs/ui-spec.md §4.5).
private struct ServerPicker: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme
    @ObservedObject var manager: Manager
    var onSelected: (() -> Void)? = nil
    private var embedded: Bool { onSelected != nil }
    @State private var query = ""
    @State private var selection: String?
    @State private var kind: ConnectionProtocol = .ssh
    @FocusState private var focused: Bool

    private struct Choice: Identifiable {
        let id, name, address, group: String
        var groupID: String? = nil
        let color: Color?
        let ssh: Profile?
        let rdp: DesktopProfile?
        let vnc: VncProfile?
    }

    private var everything: [Choice] {
        if kind == .ssh {
            return manager.profiles.map { profile in
                let group = manager.group(of: profile)
                return Choice(id: "ssh:" + profile.id, name: profile.alias, address: profile.address, group: group?.name ?? "",
                              groupID: group?.id, color: group?.marker?.color, ssh: profile, rdp: nil, vnc: nil)
            }
        }
        if kind == .vnc {
            return manager.settings.vnc.map { profile in
                let group = manager.group(of: profile)
                return Choice(id: Manager.key(profile), name: profile.name, address: profile.address, group: group?.name ?? "",
                              groupID: group?.id, color: group?.marker?.color, ssh: nil, rdp: nil, vnc: profile)
            }
        }
        return manager.settings.desktops.map { desktop in
            let group = manager.group(of: desktop)
            return Choice(id: "rdp:" + desktop.id, name: desktop.name, address: desktop.address, group: group?.name ?? "",
                          groupID: group?.id, color: group?.marker?.color, ssh: nil, rdp: desktop, vnc: nil)
        }
    }

    private var visible: [Choice] {
        let search = query.trimmingCharacters(in: .whitespaces)
        let filtered = everything.filter { search.isEmpty || [$0.name, $0.address, $0.group].contains { $0.localizedCaseInsensitiveContains(search) } }
        return (embedded ? manager.columnSort : ServerSort.name).apply(filtered, name: \.name, host: \.address)
    }

    /// Up to five recent servers of this protocol while the search is empty (docs/ui-spec.md §7).
    private var recent: [Choice] {
        guard query.trimmingCharacters(in: .whitespaces).isEmpty else { return [] }
        let all = everything
        return Array(manager.settings.recent.compactMap { entry in all.first { $0.id == entry.key } }.prefix(5))
    }

    private var chosen: Choice? {
        if let selection, let bar = selection.firstIndex(of: "|") {
            let id = String(selection[selection.index(after: bar)...])
            return everything.first { $0.id == id }
        }
        if let selection, selection.hasPrefix("recent:") { return recent.first { "recent:" + $0.id == selection } }
        return visible.first { $0.id == selection } ?? recent.first ?? visible.first
    }

    /// A group of the «Серверы» column: «Недавние», a catalog tab or «Без вкладки» (docs/ui-spec.md §2.2).
    private struct ColumnSection: Identifiable {
        let id, title: String
        let color: Color?
        let choices: [Choice]
    }

    private var sections: [ColumnSection] {
        let all = visible
        var result: [ColumnSection] = []
        let latest = recent
        if !latest.isEmpty { result.append(ColumnSection(id: "recent", title: "Недавние", color: nil, choices: latest)) }
        let favorites = all.filter { manager.isFavorite($0.id) }
        if !favorites.isEmpty { result.append(ColumnSection(id: "favorites", title: "Избранное", color: nil, choices: favorites)) }
        let fixed = result.count
        for group in manager.settings.groups {
            let members = all.filter { $0.groupID == group.id }
            if !members.isEmpty { result.append(ColumnSection(id: group.id, title: group.name, color: group.marker?.color, choices: members)) }
        }
        let loose = all.filter { $0.groupID == nil }
        if !loose.isEmpty {
            let grouped = result.count > fixed
            result.append(ColumnSection(id: "ungrouped", title: grouped ? "Без вкладки" : "Все серверы", color: nil, choices: loose))
        }
        return result
    }

    private func sectionHeader(_ section: ColumnSection, palette: Palette) -> some View {
        let folded = manager.settings.collapsed.contains(section.id)
        return HStack(spacing: 6) {
            Image(systemName: folded ? "chevron.right" : "chevron.down")
                .font(.system(size: 10, weight: .semibold)).foregroundStyle(palette.muted).frame(width: 12)
            if let color = section.color { Circle().fill(color).frame(width: 8, height: 8) }
            Text(section.title).font(.system(size: 13, weight: .semibold)).lineLimit(1)
            Spacer(minLength: 4)
            Text("\(section.choices.count)").font(.system(size: 12)).foregroundStyle(palette.muted)
        }
        .contentShape(Rectangle())
        .onTapGesture { manager.toggleCollapsed(section.id) }
        .contextMenu {
            Button(folded ? "Развернуть" : "Свернуть") { manager.toggleCollapsed(section.id) }
            if let group = manager.settings.groups.first(where: { $0.id == section.id }) { groupMenu(group) }
            Divider()
            Button { manager.groupEditor = GroupEditorState() } label: { Label("Новая вкладка…", systemImage: "plus") }
            if let group = manager.settings.groups.first(where: { $0.id == section.id }) {
                Button(role: .destructive) { manager.removeGroup(group) } label: { Label("Удалить вкладку", systemImage: "trash") }
            }
        }
        .help(folded ? "Развернуть" : "Свернуть")
        .accessibilityAddTraits(.isButton)
        .accessibilityLabel(section.title + ", \(section.choices.count)" + (folded ? ", свёрнута" : ""))
    }

    @ViewBuilder
    private func groupMenu(_ group: ServerGroup) -> some View {
        let groups = manager.settings.groups
        let index = groups.firstIndex { $0.id == group.id } ?? 0
        Button { manager.groupEditor = GroupEditorState(original: group) } label: { Label("Переименовать вкладку", systemImage: "pencil") }
        Menu {
            Button { manager.mark(group, nil) } label: {
                if group.marker == nil { Label("Без метки", systemImage: "checkmark") } else { Text("Без метки") }
            }
            ForEach(GroupMarker.allCases) { marker in
                Button { manager.mark(group, marker) } label: {
                    if group.marker == marker { Label(marker.title, systemImage: "checkmark") } else { Text(marker.title) }
                }
            }
        } label: { Label("Цветовая метка", systemImage: "circle.fill") }
        Button { manager.shiftGroup(group, by: -1) } label: { Label("Переместить выше", systemImage: "arrow.up") }.disabled(index == 0)
        Button { manager.shiftGroup(group, by: 1) } label: { Label("Переместить ниже", systemImage: "arrow.down") }
            .disabled(index == groups.count - 1)
    }

    /// The tab of the chosen server or group header: «Добавить сервер» puts a new server there.
    private func target(for tag: String?) {
        guard embedded else { return }
        var group: String?
        if let tag, let bar = tag.firstIndex(of: "|") {
            let id = String(tag[tag.index(after: bar)...])
            group = everything.first { $0.id == id }?.groupID
        }
        manager.destination = group.map { .group($0) } ?? .all
    }

    private func editChosen() {
        guard embedded, let chosen else { return }
        if let profile = chosen.ssh { manager.editor = ProfileEditorState(original: profile) }
        if let desktop = chosen.rdp { manager.desktopEditor = DesktopEditorState(original: desktop, group: desktop.group) }
        if let profile = chosen.vnc { manager.editVnc(profile) }
    }

    private func deleteChosen() {
        guard embedded, let chosen else { return }
        if let profile = chosen.ssh { manager.remove(profile) }
        if let desktop = chosen.rdp { manager.removeDesktop(desktop) }
        if let profile = chosen.vnc { manager.removeVnc(profile) }
    }

    private func choiceRow(_ choice: Choice, tag: String, palette: Palette, dot: Bool = true) -> some View {
        HStack(alignment: .top, spacing: 9) {
            Circle().fill(choice.color ?? palette.teal).frame(width: 7, height: 7).padding(.top, 5).opacity(dot ? 1 : 0)
            VStack(alignment: .leading, spacing: 1) {
                Text(choice.name).font(.system(size: 13, weight: .semibold)).lineLimit(1)
                Text(choice.address + (choice.group.isEmpty ? "" : "  ·  " + choice.group))
                    .font(.system(size: 12)).foregroundStyle(palette.muted).lineLimit(1)
            }
        }
        .tag(tag)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { choose(choice) }
        .contextMenu {
            if embedded { CatalogRowMenu(manager: manager, ssh: choice.ssh, rdp: choice.rdp, vnc: choice.vnc) }
        }
    }

    var body: some View {
        let palette = Palette.of(scheme)
        VStack(alignment: .leading, spacing: 10) {
            if !embedded { Text("Новый сеанс").font(.title3.weight(.semibold)) }
            HStack(spacing: 4) {
                ForEach(ConnectionProtocol.allCases) { value in
                    Button { kind = value; selection = nil } label: {
                        GlyphLabel(title: value.rawValue, glyph: embedded ? nil : value == .ssh ? .terminal : .server)
                    }
                        .winButton(kind == value ? .selectedTab : .tab)
                }
            }
            SearchField(placeholder: "Найти сервер", text: $query, focus: $focused) { if let chosen { choose(chosen) } }
            List(selection: $selection) {
                if embedded && query.trimmingCharacters(in: .whitespaces).isEmpty {
                    ForEach(sections) { section in
                        sectionHeader(section, palette: palette)
                        if !manager.settings.collapsed.contains(section.id) {
                            ForEach(section.choices) { choice in
                                choiceRow(choice, tag: section.id + "|" + choice.id, palette: palette,
                                          dot: section.id == "recent" || section.id == "favorites")
                            }
                        }
                    }
                } else if recent.isEmpty {
                    ForEach(visible) { choice in choiceRow(choice, tag: choice.id, palette: palette) }
                } else {
                    Section("Недавние") {
                        ForEach(recent) { choice in choiceRow(choice, tag: "recent:" + choice.id, palette: palette) }
                    }
                    Section("Все серверы") {
                        ForEach(visible) { choice in choiceRow(choice, tag: choice.id, palette: palette) }
                    }
                }
            }.frame(height: embedded ? nil : 230).listStyle(.bordered)
            Text(!visible.isEmpty ? "Серверов: \(visible.count)" : everything.isEmpty ? "Сохранённых серверов пока нет." : "Серверы не найдены.")
                .font(.system(size: 12)).foregroundStyle(palette.muted)
            if embedded {
                Button("Подключиться") { if let chosen { choose(chosen) } }.winButton(.primary).disabled(chosen == nil)
            } else {
                DialogButtons(commit: "Подключиться", enabled: chosen != nil, cancel: { dismiss() }) { if let chosen { choose(chosen) } }
            }
        }
        .padding(embedded ? 0 : 20).frame(width: embedded ? nil : 495)
        .onAppear { kind = manager.catalogProtocol; focused = !embedded }
        .onChange(of: kind) { _, value in if embedded { manager.catalogProtocol = value } }
        .onChange(of: manager.catalogProtocol) { _, value in if embedded && kind != value { kind = value } }
        .onChange(of: selection) { _, value in target(for: value) }
        .onReceive(NotificationCenter.default.publisher(for: .editCatalogRow)) { _ in editChosen() }
        .onReceive(NotificationCenter.default.publisher(for: .deleteCatalogRow)) { _ in deleteChosen() }
        .onDeleteCommand { deleteChosen() }
        .onReceive(NotificationCenter.default.publisher(for: .focusServerSearch)) { _ in
            if embedded { focused = true }
        }
    }

    private func choose(_ choice: Choice) {
        if let onSelected { onSelected() } else { dismiss() }
        if let profile = choice.ssh { manager.connect(profile, new: !embedded) }
        if let desktop = choice.rdp { manager.connectDesktop(desktop, new: !embedded) }
        if let profile = choice.vnc { manager.connectVnc(profile, new: !embedded) }
    }
}
