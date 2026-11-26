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
                Button("Каталог") { manager.showServers() }
                    .keyboardShortcut(.escape, modifiers: .shift)
                    .disabled(!manager.showingSessions)
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
                // The catalog table's search when it is shown, otherwise the search of the «Серверы» column.
                Button("Найти сервер") {
                    let catalog = manager.workspace == .catalog
                    if !catalog { manager.revealServerColumn() }
                    DispatchQueue.main.async {
                        NotificationCenter.default.post(name: catalog ? .focusCatalogSearch : .focusServerSearch, object: nil)
                    }
                }.keyboardShortcut("f", modifiers: .command)
                Button("Изменить сервер") { NotificationCenter.default.post(name: .editCatalogRow, object: nil) }
                    .keyboardShortcut("e", modifiers: .command)
                    .disabled(manager.showingSessions)
                Button("Удалить сервер") { NotificationCenter.default.post(name: .deleteCatalogRow, object: nil) }
                    .keyboardShortcut(.delete, modifiers: .command)
                    .disabled(manager.showingSessions)
                Button("Открыть OpenSSH config") { manager.openConfig() }
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
        .sheet(isPresented: $manager.choosingServer) { ServerPicker(manager: manager) }
        .sheet(item: $manager.backupPassword) { request in BackupPasswordSheet(request: request) }
        .sheet(isPresented: $updates.presented) { MacUpdateView(updates: updates) }
        .sheet(isPresented: $manager.editingSettings) {
            SettingsSheet(preferences: manager.settings.preferences) { try manager.savePreferences($0) }
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
        let need: CGFloat = manager.workspace == .catalog ? 620 : 460
        var servers = manager.serverColumn
        var sessions = manager.sessionColumn
        func room() -> CGFloat {
            free - (servers ? Self.serverWidth + Self.gap : 0) - (sessions ? Self.sessionWidth + Self.gap : 0)
        }
        if servers && !manager.serverColumnForced && room() < need { servers = false }
        if sessions && !manager.sessionColumnForced && room() < need { sessions = false }
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
                Button { manager.showServers() } label: { GlyphLabel(title: "Каталог", glyph: .server, kind: .quiet) }
                    .winButton(.quiet).help("Таблица подключений")
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
            CatalogPage(manager: manager)
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
            if !manager.showingSessions {
                Text(manager.catalogProtocol == .ssh ? manager.configPath : "~/Library/Application Support/SSH Manager/tabs.xml")
                    .foregroundStyle(palette.muted).lineLimit(1).truncationMode(.middle)
                Spacer()
                Text(CatalogModel(manager: manager, query: manager.catalogQuery).statusText).foregroundStyle(palette.muted)
            } else {
                Spacer()
            }
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

// MARK: - Catalog

/// Rows of the current tab after search and sorting.
private struct CatalogModel {
    let manager: Manager
    var query = ""

    var favorites: Bool { manager.destination == .favorites }

    var groupProfiles: [Profile] {
        if favorites { return manager.profiles.filter { manager.isFavorite(Manager.key($0)) } }
        guard let group = manager.currentGroup else { return manager.profiles }
        return manager.profiles.filter { group.aliases.contains($0.id) }
    }

    var groupDesktops: [DesktopProfile] {
        if favorites { return manager.settings.desktops.filter { manager.isFavorite(Manager.key($0)) } }
        guard let group = manager.currentGroup else { return manager.settings.desktops }
        return manager.settings.desktops.filter { $0.group == group.id }
    }

    var groupVnc: [VncProfile] {
        if favorites { return manager.settings.vnc.filter { manager.isFavorite(Manager.key($0)) } }
        guard let group = manager.currentGroup else { return manager.settings.vnc }
        return manager.settings.vnc.filter { $0.group == group.id }
    }

    var profiles: [Profile] {
        let search = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let filtered = groupProfiles.filter { profile in
            search.isEmpty || [profile.alias, profile.host, profile.user, manager.group(of: profile)?.name ?? ""]
                .contains { $0.localizedCaseInsensitiveContains(search) }
        }
        return manager.currentSort.apply(filtered, name: \.alias, host: { $0.host.isEmpty ? $0.alias : $0.host })
    }

    var desktops: [DesktopProfile] {
        let search = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let filtered = groupDesktops.filter { profile in
            search.isEmpty || [profile.name, profile.address, profile.account, manager.group(of: profile)?.name ?? ""]
                .contains { $0.localizedCaseInsensitiveContains(search) }
        }
        return manager.currentSort.apply(filtered, name: \.name, host: \.host)
    }

    var vnc: [VncProfile] {
        let search = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let filtered = groupVnc.filter { profile in
            search.isEmpty || [profile.name, profile.address, profile.user, manager.group(of: profile)?.name ?? ""]
                .contains { $0.localizedCaseInsensitiveContains(search) }
        }
        return manager.currentSort.apply(filtered, name: \.name, host: \.host)
    }

    var statusText: String {
        let (total, shown) = switch manager.catalogProtocol {
        case .ssh: (groupProfiles.count, profiles.count)
        case .rdp: (groupDesktops.count, desktops.count)
        case .vnc: (groupVnc.count, vnc.count)
        }
        return shown == total ? "Подключений: \(total)" : "Найдено: \(shown) из \(total)"
    }
}

private struct CatalogPage: View {
    @ObservedObject var manager: Manager
    @State private var selection: String?
    @FocusState private var searchFocused: Bool
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let query = manager.catalogQuery
        let model = CatalogModel(manager: manager, query: query)
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 4) {
                ForEach(ConnectionProtocol.allCases) { kind in
                    Button {
                        manager.catalogProtocol = kind
                        manager.catalogQuery = ""
                        selection = nil
                    } label: { GlyphLabel(title: kind.rawValue, glyph: kind == .ssh ? .terminal : .server) }
                        .winButton(manager.catalogProtocol == kind ? .selectedTab : .tab)
                        .frame(minWidth: 86)
                }
                Spacer(minLength: 12)
            }.padding(.bottom, 6)
            TabStrip(manager: manager)
            tools(model: model)
            ServerTable(manager: manager, model: model, selection: $selection, query: query)
        }
        .padding(.bottom, 8)
        // ⌘F, ⌘E and ⌘⌫ / Delete, like Ctrl+F, F2 and Delete in the Windows app.
        .onReceive(NotificationCenter.default.publisher(for: .focusCatalogSearch)) { _ in searchFocused = true }
        .onReceive(NotificationCenter.default.publisher(for: .editCatalogRow)) { _ in
            editSelected(CatalogModel(manager: manager, query: manager.catalogQuery))
        }
        .onReceive(NotificationCenter.default.publisher(for: .deleteCatalogRow)) { _ in
            deleteSelected(CatalogModel(manager: manager, query: manager.catalogQuery))
        }
    }

    private func tools(model: CatalogModel) -> some View {
        HStack(spacing: 4) {
            SearchField(placeholder: "Найти сервер", text: $manager.catalogQuery, focus: $searchFocused).frame(width: 265)
            Spacer()
            Menu {
                Button("Экспорт архива…") { manager.exportBackup() }
                Button("Импорт архива…") { manager.importBackup() }
                Divider()
                Button("Импорт из других программ…") { manager.importing = true }
            } label: { GlyphLabel(title: "Резервные копии", glyph: .folder) }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                .padding(.horizontal, 10)
            Button { manager.reloadAll() } label: { GlyphLabel(title: "Обновить", glyph: .refresh) }.winButton(.quiet)
                .help("Перечитать SSH config и настройки (⌘R)")
            Button { editSelected(model) } label: { GlyphLabel(title: "Изменить", glyph: .edit) }
                .winButton(.quiet).disabled(selection == nil).help("Изменить выбранное подключение (⌘E)")
            Button { deleteSelected(model) } label: { GlyphLabel(title: "Удалить", glyph: .delete, danger: true) }
                .winButton(.quiet, danger: true).disabled(selection == nil).help("Удалить выбранное подключение (⌘⌫)")
        }
        .padding(.bottom, 8)
    }

    private func editSelected(_ model: CatalogModel) {
        switch manager.catalogProtocol {
        case .ssh:
            if let profile = model.profiles.first(where: { $0.id == selection }) { manager.editor = ProfileEditorState(original: profile) }
        case .rdp:
            if let profile = model.desktops.first(where: { $0.id == selection }) {
                manager.desktopEditor = DesktopEditorState(original: profile, group: profile.group)
            }
        case .vnc:
            if let profile = model.vnc.first(where: { $0.id == selection }) { manager.editVnc(profile) }
        }
    }

    private func deleteSelected(_ model: CatalogModel) {
        switch manager.catalogProtocol {
        case .ssh:
            if let profile = model.profiles.first(where: { $0.id == selection }) { manager.remove(profile) }
        case .rdp:
            if let profile = model.desktops.first(where: { $0.id == selection }) { manager.removeDesktop(profile) }
        case .vnc:
            if let profile = model.vnc.first(where: { $0.id == selection }) { manager.removeVnc(profile) }
        }
    }
}

private struct TabStrip: View {
    @ObservedObject var manager: Manager
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        HStack(spacing: 6) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 5) {
                    if !manager.settings.favorites.isEmpty {
                        Button { manager.destination = .favorites } label: {
                            HStack(spacing: 8) {
                                GlyphView(glyph: .star, color: manager.destination == .favorites ? Palette.of(scheme).teal : Palette.of(scheme).muted)
                                Text("Избранное").lineLimit(1)
                            }
                        }.winButton(manager.destination == .favorites ? .selectedTab : .tab)
                    }
                    tab(name: "Все серверы", marker: nil, selected: manager.destination == .all) { manager.destination = .all }
                        .contextMenu { startupItem(nil) }
                    ForEach(manager.settings.groups) { group in
                        tab(name: group.name, marker: group.marker, selected: manager.destination == .group(group.id)) {
                            manager.destination = .group(group.id)
                        }
                        .contextMenu { groupMenu(group) }
                        .draggable(group.id)
                        .dropDestination(for: String.self) { items, _ in
                            guard let moved = items.first else { return false }
                            manager.placeGroup(moved, before: group.id)
                            return true
                        }
                    }
                }
            }
            Button { manager.groupEditor = GroupEditorState() } label: { GlyphLabel(title: "Новая вкладка", glyph: .plus) }
                .winButton(.quiet).frame(minWidth: 140)
        }
        .frame(height: 41)
        .padding(.bottom, 8)
    }

    private func tab(name: String, marker: GroupMarker?, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 8) {
                if let marker { Circle().fill(marker.color).frame(width: 11, height: 11) }
                Text(name).lineLimit(1).frame(maxWidth: 230)
            }
        }
        .winButton(selected ? .selectedTab : .tab)
    }

    @ViewBuilder
    private func startupItem(_ id: String?) -> some View {
        Button {
            manager.updateSettings { $0.startup = id }
        } label: {
            if manager.settings.startup == id { Label("Открывать при запуске", systemImage: "checkmark") }
            else { Text("Открывать при запуске") }
        }
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
        Button { manager.shiftGroup(group, by: -1) } label: { Label("Переместить влево", systemImage: "arrow.left") }
            .disabled(index == 0)
        Button { manager.shiftGroup(group, by: 1) } label: { Label("Переместить вправо", systemImage: "arrow.right") }
            .disabled(index == groups.count - 1)
        startupItem(group.id)
        Divider()
        Button(role: .destructive) { manager.removeGroup(group) } label: { Label("Удалить вкладку", systemImage: "trash") }
    }
}

/// The Windows server grid: header with three-state sorting, 45 pt rows, Files/Connect pills.
private struct ServerTable: View {
    @ObservedObject var manager: Manager
    let model: CatalogModel
    @Binding var selection: String?
    let query: String
    @FocusState private var focused: Bool
    @Environment(\.colorScheme) private var scheme

    private struct Row: Identifiable {
        let id: String
        let name, address, user, port, tab: String
        let sessions: Int
        let ssh: Profile?
        let rdp: DesktopProfile?
        let vnc: VncProfile?
    }

    private var rows: [Row] {
        if manager.catalogProtocol == .rdp {
            return model.desktops.map { desktop in
                Row(id: desktop.id, name: desktop.name, address: desktop.host, user: desktop.account.isEmpty ? "—" : desktop.account,
                    port: String(desktop.port), tab: manager.group(of: desktop)?.name ?? "—",
                    sessions: manager.liveCount(for: desktop), ssh: nil, rdp: desktop, vnc: nil)
            }
        }
        if manager.catalogProtocol == .vnc {
            return model.vnc.map { profile in
                Row(id: profile.id, name: profile.name, address: profile.host, user: profile.user.isEmpty ? "—" : profile.user,
                    port: String(profile.port), tab: manager.group(of: profile)?.name ?? "—",
                    sessions: manager.liveCount(for: profile), ssh: nil, rdp: nil, vnc: profile)
            }
        }
        return model.profiles.map { profile in
            Row(id: profile.id, name: profile.alias, address: profile.host.isEmpty ? "—" : profile.host,
                user: profile.user.isEmpty ? "—" : profile.user, port: profile.port.isEmpty ? "—" : profile.port,
                tab: manager.group(of: profile)?.name ?? "—", sessions: manager.liveCount(for: profile), ssh: profile, rdp: nil, vnc: nil)
        }
    }

    var body: some View {
        let palette = Palette.of(scheme)
        let ssh = manager.catalogProtocol == .ssh
        GeometryReader { geometry in
            let widths = columnWidths(total: geometry.size.width, ssh: ssh)
            VStack(spacing: 0) {
                header(widths: widths, ssh: ssh, palette: palette)
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(rows) { row in rowView(row, widths: widths, ssh: ssh, palette: palette) }
                    }
                }
                .overlay {
                    if rows.isEmpty {
                        Text(emptyText).foregroundStyle(palette.muted).multilineTextAlignment(.center).padding(16)
                    }
                }
            }
            .background(palette.surface)
            .overlay(Rectangle().strokeBorder(palette.line, lineWidth: 1).allowsHitTesting(false))
        }
        // Delete removes the selected server only while the table has focus, never from a text field.
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onDeleteCommand { if selection != nil { NotificationCenter.default.post(name: .deleteCatalogRow, object: nil) } }
    }

    private var emptyText: String {
        if !query.trimmingCharacters(in: .whitespaces).isEmpty { return "По вашему запросу ничего не найдено." }
        if manager.destination == .favorites { return "Отметьте серверы звёздочкой: «В избранное» в меню строки." }
        if manager.catalogProtocol == .rdp { return "Добавьте RDP-подключение или импортируйте профиль .rdp." }
        if manager.catalogProtocol == .vnc { return "Добавьте VNC-подключение." }
        if manager.currentGroup != nil { return "Добавьте сервер или перенесите сюда подключение из «Все серверы»." }
        return "Добавьте сервер, чтобы создать первое подключение."
    }

    private func columnWidths(total: CGFloat, ssh: Bool) -> [CGFloat] {
        let fixed: CGFloat = 64 + (ssh ? 100 : 0) + 159
        let flexible = max(0, total - fixed)
        let weights: [CGFloat] = [30, 34, 23, 22]
        let sum = weights.reduce(0, +)
        let flex = weights.map { flexible * $0 / sum }
        return [flex[0], flex[1], flex[2], 64, flex[3], ssh ? 100 : 0, 159]
    }

    private func header(widths: [CGFloat], ssh: Bool, palette: Palette) -> some View {
        let order = manager.currentSort
        return HStack(spacing: 0) {
            sortableHeader(ssh ? "Псевдоним" : "Название", width: widths[0], address: false, order: order)
            sortableHeader("Адрес", width: widths[1], address: true, order: order)
            headerCell("Пользователь", width: widths[2])
            headerCell("Порт", width: widths[3])
            headerCell("Вкладка", width: widths[4])
            if ssh { headerCell("SFTP", width: widths[5]) }
            headerCell(ssh ? "Терминал" : "Рабочий стол", width: widths[6])
        }
        .frame(height: 37)
        .background(palette.canvas)
        .overlay(alignment: .bottom) { Rectangle().fill(palette.line).frame(height: 1) }
        .foregroundStyle(palette.muted)
        .font(.system(size: 12, weight: .semibold))
    }

    private func headerCell(_ title: String, width: CGFloat) -> some View {
        Text(title).lineLimit(1).padding(.leading, 8).frame(width: width, alignment: .leading)
    }

    private func sortableHeader(_ title: String, width: CGFloat, address: Bool, order: ServerSort) -> some View {
        let ascending = order == (address ? .address : .name)
        let descending = order == (address ? .addressDescending : .nameDescending)
        return Button { manager.sortBy(address: address) } label: {
            HStack(spacing: 4) {
                Text(title).lineLimit(1)
                if ascending || descending { Image(systemName: ascending ? "chevron.up" : "chevron.down").font(.system(size: 9, weight: .bold)) }
                Spacer(minLength: 0)
            }
            .padding(.leading, 8)
            .frame(width: width, height: 37, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Нажмите для сортировки")
    }

    private func rowView(_ row: Row, widths: [CGFloat], ssh: Bool, palette: Palette) -> some View {
        let selected = selection == row.id
        return HStack(spacing: 0) {
            HStack(spacing: 8) {
                if manager.isFavorite(row.ssh.map { Manager.key($0) } ?? row.rdp.map { Manager.key($0) }
                    ?? row.vnc.map { Manager.key($0) } ?? "") {
                    GlyphView(glyph: .star, color: GroupMarker.amber.color)
                } else {
                    GlyphView(glyph: .server, color: palette.teal)
                }
                Text(row.name).font(.system(size: 13, weight: .semibold)).lineLimit(1)
            }
            .padding(.leading, 9).frame(width: widths[0], alignment: .leading)
            cell(row.address, width: widths[1])
            cell(row.user, width: widths[2])
            cell(row.port, width: widths[3])
            cell(row.tab, width: widths[4])
            if ssh, let profile = row.ssh {
                pill("Файлы", glyph: .folder, palette: palette) { manager.openFiles(profile) }.frame(width: widths[5])
            }
            pill(row.sessions > 0 ? "К сеансу · \(row.sessions)" : "Подключиться", glyph: .terminal, palette: palette) {
                if let profile = row.ssh { manager.connect(profile) }
                if let desktop = row.rdp { manager.connectDesktop(desktop) }
                if let profile = row.vnc { manager.connectVnc(profile) }
            }.frame(width: widths[6])
        }
        .frame(height: 45)
        .background(selected ? palette.selection : palette.surface)
        .overlay(alignment: .bottom) { Rectangle().fill(palette.line).frame(height: 1) }
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { edit(row) }
        .onTapGesture { selection = row.id; focused = true }
        .contextMenu {
            if let profile = row.ssh { sshMenu(profile) }
            if let desktop = row.rdp { rdpMenu(desktop) }
            if let profile = row.vnc { vncMenu(profile) }
        }
    }

    private func cell(_ text: String, width: CGFloat) -> some View {
        Text(text).font(.system(size: 13)).lineLimit(1).padding(.leading, 8).frame(width: width, alignment: .leading)
    }

    private func pill(_ title: String, glyph: Glyph, palette: Palette, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 6) {
                GlyphView(glyph: glyph, color: palette.teal)
                Text(title).font(.system(size: 13, weight: .semibold)).foregroundStyle(palette.teal).lineLimit(1)
                Spacer(minLength: 0)
            }
            .padding(.leading, 7)
            .frame(maxWidth: .infinity, minHeight: 30, maxHeight: 30)
            .background(RoundedRectangle(cornerRadius: 7).fill(palette.mint))
            .contentShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 5)
    }

    private func edit(_ row: Row) {
        selection = row.id
        if let profile = row.ssh { manager.editor = ProfileEditorState(original: profile) }
        if let desktop = row.rdp { manager.desktopEditor = DesktopEditorState(original: desktop, group: desktop.group) }
        if let profile = row.vnc { manager.editVnc(profile) }
    }

    @ViewBuilder
    private func sshMenu(_ profile: Profile) -> some View {
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
    private func rdpMenu(_ desktop: DesktopProfile) -> some View {
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
    private func vncMenu(_ profile: VncProfile) -> some View {
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
    }
}

// MARK: - Sessions

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
        .frame(minWidth: 150, maxWidth: 460, minHeight: 32, maxHeight: 32)
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
                              color: group?.marker?.color, ssh: profile, rdp: nil, vnc: nil)
            }
        }
        if kind == .vnc {
            return manager.settings.vnc.map { profile in
                let group = manager.group(of: profile)
                return Choice(id: Manager.key(profile), name: profile.name, address: profile.address, group: group?.name ?? "",
                              color: group?.marker?.color, ssh: nil, rdp: nil, vnc: profile)
            }
        }
        return manager.settings.desktops.map { desktop in
            let group = manager.group(of: desktop)
            return Choice(id: "rdp:" + desktop.id, name: desktop.name, address: desktop.address, group: group?.name ?? "",
                          color: group?.marker?.color, ssh: nil, rdp: desktop, vnc: nil)
        }
    }

    private var visible: [Choice] {
        let search = query.trimmingCharacters(in: .whitespaces)
        let filtered = everything.filter { search.isEmpty || [$0.name, $0.address, $0.group].contains { $0.localizedCaseInsensitiveContains(search) } }
        return ServerSort.name.apply(filtered, name: \.name, host: \.address)
    }

    /// Up to five recent servers of this protocol while the search is empty (docs/ui-spec.md §7).
    private var recent: [Choice] {
        guard query.trimmingCharacters(in: .whitespaces).isEmpty else { return [] }
        let all = everything
        return Array(manager.settings.recent.compactMap { entry in all.first { $0.id == entry.key } }.prefix(5))
    }

    private var chosen: Choice? {
        if let selection, selection.hasPrefix("recent:") { return recent.first { "recent:" + $0.id == selection } }
        return visible.first { $0.id == selection } ?? recent.first ?? visible.first
    }

    private func choiceRow(_ choice: Choice, tag: String, palette: Palette) -> some View {
        HStack(alignment: .top, spacing: 9) {
            Circle().fill(choice.color ?? palette.teal).frame(width: 7, height: 7).padding(.top, 5)
            VStack(alignment: .leading, spacing: 1) {
                Text(choice.name).font(.system(size: 13, weight: .semibold)).lineLimit(1)
                Text(choice.address + (choice.group.isEmpty ? "" : "  ·  " + choice.group))
                    .font(.system(size: 12)).foregroundStyle(palette.muted).lineLimit(1)
            }
        }
        .tag(tag)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { choose(choice) }
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
                if recent.isEmpty {
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
