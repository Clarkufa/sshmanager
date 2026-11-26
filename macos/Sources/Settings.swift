import Foundation
import SwiftUI

struct ServerGroup: Identifiable {
    let id: String
    var name: String
    var marker: GroupMarker?
    var aliases: Set<String> = []
    var sshSort: ServerSort = .name
    var rdpSort: ServerSort = .original
    var vncSort: ServerSort = .original

    init(id: String = UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: ""), name: String, marker: GroupMarker? = nil) {
        self.id = id
        self.name = name
        self.marker = marker
    }
}

enum GroupMarker: String, CaseIterable, Identifiable {
    // The Windows order and colours (src/TabColor.cs).
    case blue, green, teal, amber, red, purple, gray
    var id: Self { self }
    var title: String {
        switch self {
        case .blue: "Синяя"
        case .teal: "Бирюзовая"
        case .green: "Зелёная"
        case .amber: "Янтарная"
        case .red: "Красная"
        case .purple: "Фиолетовая"
        case .gray: "Серая"
        }
    }
    var color: Color {
        switch self {
        case .blue: Color(hex: 0x2563EB)
        case .green: Color(hex: 0x20804E)
        case .teal: Color(hex: 0x0E7B82)
        case .amber: Color(hex: 0xB36306)
        case .red: Color(hex: 0xC52E49)
        case .purple: Color(hex: 0x7E4CC8)
        case .gray: Color(hex: 0x64748B)
        }
    }
}

enum Appearance: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: Self { self }
    var title: String {
        switch self {
        case .system: "Как в macOS"
        case .light: "Светлая"
        case .dark: "Тёмная"
        }
    }
    var scheme: ColorScheme? {
        switch self {
        case .system: nil
        case .light: .light
        case .dark: .dark
        }
    }
}

struct Snippet: Identifiable, Equatable {
    var id = UUID()
    var name: String
    var command: String
}

struct RecentEntry: Equatable {
    var key: String
    var at: Int
}

/// «Настройки» (docs/ui-spec.md §7), stored as attributes of the tabs.xml root like the Windows app.
struct Preferences: Equatable {
    var terminalFont = ""
    var terminalSize = 13
    var scrollback = 3000
    var defaultUser = ""
    var defaultPort = ""
    var autoReconnect = false
    var closeToTray = true
    var checkUpdates = true
}

struct ManagerSettings {
    var favorites: [String] = []
    var recent: [RecentEntry] = []
    var snippets: [Snippet] = []
    var preferences = Preferences()
    var groups: [ServerGroup] = []
    var startup: String?
    var appearance: Appearance = .system
    var sshSort: ServerSort = .name
    var rdpSort: ServerSort = .original
    var vncSort: ServerSort = .original
    var desktops: [DesktopProfile] = []
    var vnc: [VncProfile] = []
    /// Folded groups of the «Серверы» column: tab ids, «ungrouped» and «recent» (tabs.xml «collapsed»).
    var collapsed: Set<String> = []

    static let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("SSH Manager/tabs.xml")

    static func load() throws -> ManagerSettings {
        do { return try decode(readRegularFile(url, limit: BackupArchive.maxFile)) }
        catch let error as NSError where error.domain == NSCocoaErrorDomain && error.code == NSFileReadNoSuchFileError { return ManagerSettings() }
    }

    static func decode(_ data: Data) throws -> ManagerSettings {
        guard data.count <= 8 * 1024 * 1024, let xml = String(data: data, encoding: .utf8),
              !xml.contains("<!"), !xml.contains("\0"),
              let root = try XMLDocument(data: data, options: [.nodeLoadExternalEntitiesNever]).rootElement(),
              root.name == "tabs"
        else { throw ManagerError("Не удалось прочитать настройки вкладок.") }
        var settings = ManagerSettings()
        settings.sshSort = order("sshSort", on: root, fallback: .name)
        settings.rdpSort = order("rdpSort", on: root, fallback: .original)
        settings.vncSort = order("vncSort", on: root, fallback: .original)
        if let theme = root.attribute(forName: "theme")?.stringValue { settings.appearance = Appearance(rawValue: theme) ?? .system }
        func text(_ key: String) -> String? { root.attribute(forName: key)?.stringValue }
        var preferences = Preferences()
        preferences.terminalFont = String((text("terminalFont") ?? "").prefix(80))
        preferences.terminalSize = min(max(Int(text("terminalSize") ?? "") ?? 13, 6), 48)
        preferences.scrollback = min(max(Int(text("scrollback") ?? "") ?? 3000, 100), 100_000)
        preferences.defaultUser = String((text("defaultUser") ?? "").prefix(128))
        preferences.defaultPort = text("defaultPort").flatMap { Int($0).map(String.init) } ?? ""
        preferences.autoReconnect = text("autoReconnect") == "1"
        preferences.closeToTray = text("closeToTray") != "0"
        preferences.checkUpdates = text("checkUpdates") != "0"
        settings.preferences = preferences
        settings.favorites = root.elements(forName: "favorite").compactMap { $0.attribute(forName: "key")?.stringValue }
            .filter { $0.hasPrefix("ssh:") || $0.hasPrefix("rdp:") || $0.hasPrefix("vnc:") }
        settings.recent = Array(root.elements(forName: "recent").compactMap { element -> RecentEntry? in
            guard let key = element.attribute(forName: "key")?.stringValue else { return nil }
            return RecentEntry(key: key, at: Int(element.attribute(forName: "at")?.stringValue ?? "") ?? 0)
        }.prefix(10))
        settings.snippets = root.elements(forName: "snippet").compactMap { element -> Snippet? in
            guard let name = element.attribute(forName: "name")?.stringValue, !name.isEmpty,
                  let command = element.attribute(forName: "command")?.stringValue else { return nil }
            return Snippet(name: String(name.prefix(80)), command: String(command.prefix(4000)))
        }
        var aliases: Set<String> = []
        for element in root.elements(forName: "tab") {
            guard let id = element.attribute(forName: "id")?.stringValue, id.count == 32,
                  id.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }),
                  !settings.groups.contains(where: { $0.id == id }),
                  let name = element.attribute(forName: "name")?.stringValue else {
                throw ManagerError("Настройки вкладок содержат повреждённую запись.")
            }
            try settings.validate(name, except: nil)
            let marker = element.attribute(forName: "color")?.stringValue.flatMap(GroupMarker.init(rawValue:))
            var group = ServerGroup(id: id, name: name, marker: marker)
            group.sshSort = order("sshSort", on: element, fallback: .name)
            group.rdpSort = order("rdpSort", on: element, fallback: .original)
            group.vncSort = order("vncSort", on: element, fallback: .original)
            for host in element.elements(forName: "host") {
                guard let alias = host.attribute(forName: "alias")?.stringValue, Profile.validAlias(alias),
                      aliases.insert(alias.lowercased()).inserted
                else { throw ManagerError("Настройки вкладок содержат повторяющийся или недопустимый псевдоним.") }
                group.aliases.insert(alias.lowercased())
            }
            settings.groups.append(group)
        }
        if let startup = root.attribute(forName: "startupTab")?.stringValue,
           settings.groups.contains(where: { $0.id == startup }) { settings.startup = startup }
        settings.collapsed = Set((root.attribute(forName: "collapsed")?.stringValue ?? "").split(separator: " ").map(String.init)
            .filter { key in key == "recent" || key == "ungrouped" || settings.groups.contains { $0.id == key } })
        let groups = settings.groups.map(\.id)
        var desktops: Set<String> = []
        for element in root.elements(forName: "rdp") {
            let profile = try DesktopProfile(element: element, groups: groups)
            guard desktops.insert(profile.id).inserted else { throw ManagerError("RDP-профиль повторяется в настройках.") }
            settings.desktops.append(profile)
        }
        var vncIDs: Set<String> = []
        for element in root.elements(forName: "vnc") {
            let profile = try VncProfile(element: element, groups: groups)
            guard vncIDs.insert(profile.id).inserted else { throw ManagerError("VNC-профиль повторяется в настройках.") }
            settings.vnc.append(profile)
        }
        return settings
    }

    var data: Data {
        let root = XMLElement(name: "tabs")
        if let startup { Self.attribute("startupTab", value: startup, on: root) }
        if appearance != .system { Self.attribute("theme", value: appearance.rawValue, on: root) }
        Self.attribute("sshSort", value: sshSort.rawValue, on: root)
        Self.attribute("rdpSort", value: rdpSort.rawValue, on: root)
        Self.attribute("vncSort", value: vncSort.rawValue, on: root)
        let folded = collapsed.filter { key in key == "recent" || key == "ungrouped" || groups.contains { $0.id == key } }.sorted()
        if !folded.isEmpty { Self.attribute("collapsed", value: folded.joined(separator: " "), on: root) }
        if !preferences.terminalFont.isEmpty { Self.attribute("terminalFont", value: preferences.terminalFont, on: root) }
        Self.attribute("terminalSize", value: String(preferences.terminalSize), on: root)
        Self.attribute("scrollback", value: String(preferences.scrollback), on: root)
        if !preferences.defaultUser.isEmpty { Self.attribute("defaultUser", value: preferences.defaultUser, on: root) }
        if !preferences.defaultPort.isEmpty { Self.attribute("defaultPort", value: preferences.defaultPort, on: root) }
        Self.attribute("autoReconnect", value: preferences.autoReconnect ? "1" : "0", on: root)
        Self.attribute("closeToTray", value: preferences.closeToTray ? "1" : "0", on: root)
        Self.attribute("checkUpdates", value: preferences.checkUpdates ? "1" : "0", on: root)
        for key in favorites {
            let element = XMLElement(name: "favorite")
            Self.attribute("key", value: key, on: element)
            root.addChild(element)
        }
        for entry in recent.prefix(10) {
            let element = XMLElement(name: "recent")
            Self.attribute("key", value: entry.key, on: element)
            Self.attribute("at", value: String(entry.at), on: element)
            root.addChild(element)
        }
        for snippet in snippets {
            let element = XMLElement(name: "snippet")
            Self.attribute("name", value: snippet.name, on: element)
            Self.attribute("command", value: snippet.command, on: element)
            root.addChild(element)
        }
        for group in groups {
            let element = XMLElement(name: "tab")
            Self.attribute("id", value: group.id, on: element)
            Self.attribute("name", value: group.name, on: element)
            if let marker = group.marker { Self.attribute("color", value: marker.rawValue, on: element) }
            Self.attribute("sshSort", value: group.sshSort.rawValue, on: element)
            Self.attribute("rdpSort", value: group.rdpSort.rawValue, on: element)
            Self.attribute("vncSort", value: group.vncSort.rawValue, on: element)
            for alias in group.aliases.sorted() {
                let host = XMLElement(name: "host")
                Self.attribute("alias", value: alias, on: host)
                element.addChild(host)
            }
            root.addChild(element)
        }
        for profile in desktops {
            let element = XMLElement(name: "rdp")
            for (key, value) in profile.fields.sorted(by: { $0.key < $1.key }) {
                Self.attribute(key, value: value, on: element)
            }
            root.addChild(element)
        }
        for profile in vnc {
            let element = XMLElement(name: "vnc")
            for (key, value) in profile.fields.sorted(by: { $0.key < $1.key }) {
                Self.attribute(key, value: value, on: element)
            }
            root.addChild(element)
        }
        let document = XMLDocument(rootElement: root)
        document.characterEncoding = "UTF-8"
        return document.xmlData(options: [.nodePrettyPrint])
    }

    func save() throws {
        try FileManager.default.createDirectory(at: Self.url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try writePrivate(data, to: Self.url)
    }

    func validate(_ name: String, except id: String?) throws {
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, name.utf16.count <= 32,
              !name.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) else {
            throw ManagerError("Введите название вкладки длиной от 1 до 32 символов.")
        }
        let inUse = groups.contains { $0.id != id && $0.name.caseInsensitiveCompare(name) == .orderedSame }
        guard !inUse, !["Все серверы", "Без вкладки"].contains(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) else {
            throw ManagerError("Это название уже используется. Выберите другое.")
        }
    }

    mutating func move(_ alias: String, to id: String?) {
        for index in groups.indices {
            if groups[index].id == id { groups[index].aliases.insert(alias) }
            else { groups[index].aliases.remove(alias) }
        }
    }

    private static func order(_ name: String, on node: XMLElement, fallback: ServerSort) -> ServerSort {
        node.attribute(forName: name)?.stringValue.flatMap(ServerSort.init(rawValue:)) ?? fallback
    }

    private static func attribute(_ name: String, value: String, on node: XMLElement) {
        let attribute = XMLNode(kind: .attribute)
        attribute.name = name
        attribute.stringValue = value
        node.addAttribute(attribute)
    }
}
