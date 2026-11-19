import AppKit
import SwiftUI

struct SSHManagerApp: App {
    @StateObject private var manager = Manager()
    var body: some Scene {
        Window("SSH Manager", id: "main") {
            ManagerView(manager: manager)
                .frame(minWidth: 760, minHeight: 500)
                .preferredColorScheme(manager.settings.appearance.scheme)
        }
        .defaultSize(width: 1040, height: 680)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("Новый сеанс…") { manager.choosingServer = true }
                    .keyboardShortcut("n", modifiers: .command)
                    .disabled(manager.profiles.isEmpty)
                Button("Добавить сервер…") { manager.editor = ProfileEditorState() }
                    .keyboardShortcut("n", modifiers: [.command, .shift])
                Button("Новая вкладка…") { manager.groupEditor = GroupEditorState() }
            }
            CommandMenu("Подключения") {
                Button("Два последних сеанса") { manager.switchSession() }
                    .keyboardShortcut(.tab, modifiers: .control)
                    .disabled(manager.sessions.isEmpty)
                Button("Обновить OpenSSH config") { manager.reload() }
                    .keyboardShortcut("r", modifiers: .command)
                Button("Открыть OpenSSH config") { manager.openConfig() }
                Button("Резервные копии…") { manager.managingBackups = true }
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

private struct ManagerView: View {
    @ObservedObject var manager: Manager
    @State private var query = ""
    @FocusState private var focusSearch: Bool

    var body: some View {
        NavigationSplitView {
            sidebar
                .navigationSplitViewColumnWidth(min: 195, ideal: 222, max: 310)
        } detail: {
            ZStack {
                if case .session(let id) = manager.destination, let session = manager.sessions.first(where: { $0.id == id }) {
                    sessionDetail(session)
                } else {
                    catalog
                }
            }.frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .navigationSplitViewStyle(.balanced)
        .sheet(item: $manager.editor) { state in
            ProfileEditor(state: state) { try manager.save($0, original: state.original, password: $1, removePassword: $2) }
        }
        .sheet(item: $manager.groupEditor) { state in
            GroupEditor(state: state) { try manager.saveGroup($0, original: state.original) }
        }
        .sheet(isPresented: $manager.choosingServer) {
            ServerPicker(profiles: manager.profiles) { manager.connect($0, new: true) }
        }
        .sheet(isPresented: $manager.managingBackups) { BackupView(manager: manager) }
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
        .confirmationDialog("Удалить сервер?",
                            isPresented: Binding(get: { manager.deleteProfile != nil }, set: { if !$0 { manager.deleteProfile = nil } }),
                            titleVisibility: .visible, presenting: manager.deleteProfile) { profile in
            Button("Удалить «\(profile.alias)»", role: .destructive) { manager.remove(profile); manager.deleteProfile = nil }
        } message: { _ in Text("Секция подключения будет удалена из OpenSSH config.") }
        .confirmationDialog("Удалить вкладку?",
                            isPresented: Binding(get: { manager.deleteGroup != nil }, set: { if !$0 { manager.deleteGroup = nil } }),
                            titleVisibility: .visible, presenting: manager.deleteGroup) { group in
            Button("Удалить «\(group.name)»", role: .destructive) { manager.removeGroup(group); manager.deleteGroup = nil }
        } message: { _ in Text("Серверы останутся в разделе «Все серверы».") }
        .onChange(of: manager.destination) { _, destination in
            if case .session(let id) = destination { manager.recordSession(id) }
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            List(selection: Binding<Destination?>(get: { manager.destination }, set: { value in if let value { manager.destination = value } })) {
                Section("Каталог") {
                    Label("Все серверы", systemImage: "server.rack")
                        .tag(Destination.all)
                        .contextMenu { Button("Открывать при запуске") { manager.updateSettings { $0.startup = nil } } }
                    ForEach(manager.settings.groups) { group in
                        Label {
                            Text(group.name).lineLimit(1)
                        } icon: {
                            Image(systemName: "folder")
                                .foregroundStyle(group.marker?.color ?? .secondary)
                        }
                        .tag(Destination.group(group.id))
                        .contextMenu { groupMenu(group) }
                    }
                }
                Section("Сеансы") {
                    if manager.sessions.isEmpty {
                        Text("Нет открытых сеансов").foregroundStyle(.secondary).font(.callout)
                    }
                    ForEach(manager.sessions, id: \.id) { session in
                        SessionRow(session: session)
                            .tag(Destination.session(session.id))
                            .contextMenu {
                                Button("Новый сеанс") { manager.connect(session.profile, new: true) }
                                Button("Новый сеанс без проброса портов") { manager.connect(session.profile, new: true, noForwarding: true) }
                                Button("Файлы") { manager.openFiles(session.profile) }
                                Divider()
                                Button("Закрыть сеанс", role: .destructive) { manager.closeSession(session) }
                            }
                    }
                }
            }
            .listStyle(.sidebar)

            Divider().padding(.horizontal, 12)
            HStack {
                Button {
                    manager.groupEditor = GroupEditorState()
                } label: { Label("Новая вкладка", systemImage: "plus") }
                .buttonStyle(.borderless)
                Spacer()
                Menu {
                    Button("Открыть OpenSSH config") { manager.openConfig() }
                    Button("Обновить список серверов") { manager.reload() }
                    Button("Резервные копии…") { manager.managingBackups = true }
                    Divider()
                    ForEach(Appearance.allCases) { style in
                        Button(style.title) { manager.updateSettings { $0.appearance = style } }
                    }
                } label: { Image(systemName: "gearshape") }
                    .menuStyle(.borderlessButton).fixedSize()
                    .help("Настройки").accessibilityLabel("Настройки")
            }.padding(.horizontal, 16).frame(height: 42)
        }
    }

    private var currentGroup: ServerGroup? {
        guard case .group(let id) = manager.destination else { return nil }
        return manager.settings.groups.first { $0.id == id }
    }

    private var groupProfiles: [Profile] {
        guard let group = currentGroup else { return manager.profiles }
        return manager.profiles.filter { group.aliases.contains($0.id) }
    }

    private var visibleProfiles: [Profile] {
        let search = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !search.isEmpty else { return groupProfiles }
        return groupProfiles.filter { profile in
            profile.alias.localizedCaseInsensitiveContains(search) || profile.address.localizedCaseInsensitiveContains(search)
        }
    }

    private var catalog: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text(currentGroup?.name ?? "Все серверы").font(.largeTitle.weight(.semibold)).lineLimit(1)
                    Text("Серверов: \(groupProfiles.count)").font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                Button {
                    manager.choosingServer = true
                } label: { Label("Новый сеанс", systemImage: "terminal") }
                    .disabled(manager.profiles.isEmpty)
                Button {
                    manager.editor = ProfileEditorState()
                } label: { Label("Добавить сервер", systemImage: "plus") }
                    .buttonStyle(.borderedProminent)
            }
            .padding(.horizontal, 24).padding(.top, 24).padding(.bottom, 20)
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary).accessibilityHidden(true)
                TextField("Поиск по имени или адресу", text: $query)
                    .textFieldStyle(.plain).focused($focusSearch)
                    .accessibilityLabel("Поиск по имени или адресу")
                if !query.isEmpty {
                    Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("Очистить поиск")
                }
            }
            .padding(.horizontal, 10).frame(height: 32)
            .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 7))
            .padding(.horizontal, 24).padding(.bottom, 16)

            if visibleProfiles.isEmpty {
                Spacer()
                VStack(spacing: 9) {
                    Image(systemName: query.isEmpty ? "server.rack" : "magnifyingglass")
                        .font(.system(size: 31, weight: .light))
                        .foregroundStyle(.secondary).accessibilityHidden(true)
                    Text(query.isEmpty ? "Здесь пока нет серверов" : "Серверы не найдены")
                        .font(.title3.weight(.semibold))
                    Text(query.isEmpty ? "Добавьте сервер для подключения по SSH или SFTP." : "Попробуйте другой псевдоним или адрес.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity).padding(.bottom, 48)
                Spacer()
            } else {
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(visibleProfiles) { profile in
                            profileRow(profile)
                            if profile.id != visibleProfiles.last?.id { Divider().padding(.leading, 50) }
                        }
                    }
                    .padding(.horizontal, 12)
                    .background(.background, in: RoundedRectangle(cornerRadius: 10))
                    .overlay { RoundedRectangle(cornerRadius: 10).strokeBorder(.quaternary, lineWidth: 1).allowsHitTesting(false) }
                    .padding(.horizontal, 24).padding(.bottom, 20)
                }
            }
            Divider()
            HStack(spacing: 14) {
                Label("~/.ssh/config", systemImage: "doc.text").font(.system(.caption, design: .monospaced))
                Spacer()
                Button("Обновить") { manager.reload() }.buttonStyle(.borderless)
                Button("Поиск") { focusSearch = true }.keyboardShortcut("f").buttonStyle(.borderless)
            }.foregroundStyle(.secondary).padding(.horizontal, 24).frame(height: 36)
        }
        .background(Color(nsColor: .controlBackgroundColor))
    }

    private func profileRow(_ profile: Profile) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "terminal")
                .font(.system(size: 16, weight: .medium))
                .foregroundStyle(Color.accentColor)
                .frame(width: 36, height: 36)
                .background(Color.accentColor.opacity(0.10), in: RoundedRectangle(cornerRadius: 8))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                Text(profile.alias).font(.headline).lineLimit(1)
                Text(profile.address).font(.system(.callout, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            let open = manager.sessions.filter { $0.profile.id == profile.id }
            Button { manager.connect(profile) } label: { SessionButton(sessions: open) }
                .help("Открыть терминал")
            Button { manager.openFiles(profile) } label: { Label("Файлы", systemImage: "folder") }
            Menu { profileMenu(profile) } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton).fixedSize()
                .help("Действия с сервером").accessibilityLabel("Действия с сервером \(profile.alias)")
        }
        .frame(minHeight: 64)
        .padding(.horizontal, 6)
        .contextMenu { profileMenu(profile) }
    }

    @ViewBuilder
    private func profileMenu(_ profile: Profile) -> some View {
        Button("Подключиться") { manager.connect(profile) }
        Button("Новый сеанс") { manager.connect(profile, new: true) }
        Button("Новый сеанс без проброса портов") { manager.connect(profile, new: true, noForwarding: true) }
        Button("Файлы") { manager.openFiles(profile) }
        Divider()
        Button("Изменить…") { manager.editor = ProfileEditorState(original: profile) }
        Menu("Переместить во вкладку") {
            Button("Без вкладки") { manager.updateSettings { $0.move(profile.id, to: nil) } }
            ForEach(manager.settings.groups) { group in
                Button(group.name) { manager.updateSettings { $0.move(profile.id, to: group.id) } }
            }
        }
        Button("Удалить…", role: .destructive) { manager.deleteProfile = profile }
    }

    @ViewBuilder
    private func groupMenu(_ group: ServerGroup) -> some View {
        Button("Переименовать…") { manager.groupEditor = GroupEditorState(original: group) }
        Menu("Цветовая метка") {
            Button("Без метки") { setMarker(nil, group: group) }
            ForEach(GroupMarker.allCases) { marker in
                Button(marker.title) { setMarker(marker, group: group) }
            }
        }
        Button("Открывать при запуске") { manager.updateSettings { $0.startup = group.id } }
        Button("Переместить выше") { manager.shiftGroup(group, by: -1) }
        Button("Переместить ниже") { manager.shiftGroup(group, by: 1) }
        Divider()
        Button("Удалить…", role: .destructive) { manager.deleteGroup = group }
    }

    private func setMarker(_ marker: GroupMarker?, group: ServerGroup) {
        manager.updateSettings {
            if let index = $0.groups.firstIndex(where: { $0.id == group.id }) { $0.groups[index].marker = marker }
        }
    }

    private func sessionDetail(_ session: TerminalSession) -> some View {
        SessionDetail(session: session, files: { manager.openFiles(session.profile) },
                      newSession: { manager.connect(session.profile, new: true, noForwarding: session.noForwarding) },
                      close: { manager.closeSession(session) })
            .id(session.id)
    }
}

private struct SessionButton: View {
    let sessions: [TerminalSession]

    var body: some View {
        if let first = sessions.last { SessionButtonStatus(session: first) }
        else { Label("Подключиться", systemImage: "arrow.up.right") }
    }
}

private struct SessionButtonStatus: View {
    @ObservedObject var session: TerminalSession
    var body: some View {
        session.exitCode == nil ? Label("К сеансу", systemImage: "terminal") : Label("Подключиться", systemImage: "arrow.up.right")
    }
}

private struct SessionRow: View {
    @ObservedObject var session: TerminalSession
    var body: some View {
        Label(session.label, systemImage: session.exitCode == nil ? "terminal" : "xmark.rectangle")
            .lineLimit(1)
            .help(session.profile.address)
    }
}

private struct SessionDetail: View {
    @ObservedObject var session: TerminalSession
    let files: () -> Void
    let newSession: () -> Void
    let close: () -> Void
    @State private var rename = false
    @State private var label = ""

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: "terminal").font(.title2).foregroundStyle(Color.accentColor).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text(session.label).font(.headline).lineLimit(1)
                    Text(session.profile.address).font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                Button(action: files) { Label("Файлы", systemImage: "folder") }
                Button(action: newSession) { Label("Новый сеанс", systemImage: "plus") }
                Menu {
                    Button("Переименовать сеанс…") { label = session.label; rename = true }
                    Button("Закрыть сеанс", role: .destructive, action: close)
                } label: { Image(systemName: "ellipsis.circle") }
                    .menuStyle(.borderlessButton).fixedSize().accessibilityLabel("Действия с сеансом")
            }.padding(.horizontal, 16).frame(height: 58)
            TerminalView(session: session)
            HStack(spacing: 8) {
                if let exit = session.exitCode {
                    Label("Сеанс завершён (\(exit))", systemImage: "xmark.circle")
                } else {
                    Label("Сеанс SSH", systemImage: "terminal")
                }
                if session.noForwarding { Text("Без проброса портов") }
                Spacer()
                Button("Закрыть сеанс", action: close).buttonStyle(.borderless)
            }.font(.callout).foregroundStyle(.secondary).padding(.horizontal, 16).frame(height: 36)
        }
        .alert("Название сеанса", isPresented: $rename) {
            TextField("Название", text: $label)
            Button("Отмена", role: .cancel) {}
            Button("Сохранить") {
                let trimmed = label.trimmingCharacters(in: .whitespacesAndNewlines)
                session.label = trimmed.isEmpty ? session.profile.alias : String(trimmed.prefix(80))
            }
        }
    }
}

private struct ProfileEditor: View {
    @Environment(\.dismiss) private var dismiss
    @FocusState private var aliasFocused: Bool
    @State private var profile: Profile
    @State private var password = ""
    @State private var removePassword = false
    @State private var failure: String?
    private let state: ProfileEditorState
    private let save: (Profile, String, Bool) throws -> Void

    init(state: ProfileEditorState, save: @escaping (Profile, String, Bool) throws -> Void) {
        self.state = state
        self.save = save
        _profile = State(initialValue: state.original ?? Profile())
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(state.original == nil ? "Добавить сервер" : "Изменить сервер").font(.title2.weight(.semibold))
            Form {
                TextField("Псевдоним", text: $profile.alias, prompt: Text("например, staging")).focused($aliasFocused)
                TextField("Адрес", text: $profile.host, prompt: Text("имя хоста или IP"))
                TextField("Пользователь", text: $profile.user, prompt: Text("из OpenSSH config"))
                TextField("Порт", text: $profile.port, prompt: Text("22"))
                SecureField("Пароль", text: $password, prompt: Text("Связка ключей")).disabled(removePassword)
                if state.original != nil { Toggle("Удалить сохранённый пароль", isOn: $removePassword) }
            }.textFieldStyle(.roundedBorder)
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text("Файлы ключей").font(.headline)
                    Spacer()
                    Button("Выбрать…", action: chooseKeys)
                }
                TextEditor(text: $profile.identities)
                    .font(.system(.callout, design: .monospaced)).frame(height: 58)
                    .overlay { RoundedRectangle(cornerRadius: 4).strokeBorder(.quaternary).allowsHitTesting(false) }
                    .accessibilityLabel("Файлы ключей, по одному на строку")
                Text("Один путь на строку. Пустые поля используют настройки OpenSSH.").foregroundStyle(.secondary).font(.caption)
            }
            if let failure { Text(failure).foregroundStyle(.red).font(.callout) }
            HStack {
                Spacer()
                Button("Отмена") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Сохранить", action: submit).keyboardShortcut(.defaultAction)
                    .disabled(profile.alias.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(22).frame(width: 490)
        .onAppear { aliasFocused = true }
    }

    private func chooseKeys() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.showsHiddenFiles = true
        panel.directoryURL = ConfigStore.standard.url.deletingLastPathComponent()
        if panel.runModal() == .OK {
            var paths = profile.identities.components(separatedBy: .newlines).filter { !$0.isEmpty }
            for url in panel.urls where !paths.contains(url.path) { paths.append(url.path) }
            profile.identities = paths.joined(separator: "\n")
        }
    }

    private func submit() {
        profile.alias = profile.alias.trimmingCharacters(in: .whitespacesAndNewlines)
        profile.host = profile.host.trimmingCharacters(in: .whitespacesAndNewlines)
        profile.user = profile.user.trimmingCharacters(in: .whitespacesAndNewlines)
        profile.port = profile.port.trimmingCharacters(in: .whitespacesAndNewlines)
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
        VStack(alignment: .leading, spacing: 16) {
            Text(state.original == nil ? "Новая вкладка" : "Переименовать вкладку").font(.title2.weight(.semibold))
            TextField("Название", text: $name).textFieldStyle(.roundedBorder)
            if let failure { Text(failure).foregroundStyle(.red) }
            HStack {
                Spacer()
                Button("Отмена") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Сохранить") {
                    do { try save(name); dismiss() } catch { failure = error.localizedDescription }
                }.keyboardShortcut(.defaultAction).disabled(name.isEmpty)
            }
        }.padding(22).frame(width: 360)
    }
}

private struct ServerPicker: View {
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var selection: String?
    @FocusState private var focused: Bool
    let profiles: [Profile]
    let connect: (Profile) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Новый сеанс").font(.title2.weight(.semibold))
            TextField("Поиск по имени или адресу", text: $query).focused($focused)
            List(visible, selection: $selection) { profile in
                VStack(alignment: .leading, spacing: 2) {
                    Text(profile.alias).font(.headline)
                    Text(profile.address).foregroundStyle(.secondary).font(.system(.caption, design: .monospaced))
                }
                .tag(profile.id)
                .contentShape(Rectangle())
                .onTapGesture(count: 2) { choose(profile) }
            }.frame(height: 210).listStyle(.bordered)
            HStack {
                Spacer()
                Button("Отмена") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Подключиться") { if let profile = chosen { choose(profile) } }
                    .keyboardShortcut(.defaultAction).disabled(chosen == nil)
            }
        }.padding(22).frame(width: 440)
            .onAppear { focused = true }
    }

    private var visible: [Profile] {
        query.isEmpty ? profiles : profiles.filter { $0.alias.localizedCaseInsensitiveContains(query) || $0.address.localizedCaseInsensitiveContains(query) }
    }
    private var chosen: Profile? { visible.first(where: { $0.id == selection }) ?? visible.first }
    private func choose(_ profile: Profile) { dismiss(); connect(profile) }
}
