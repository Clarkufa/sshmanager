import Foundation
import SwiftUI

struct ServerGroup: Identifiable {
    let id: String
    var name: String
    var marker: GroupMarker?
    var aliases: Set<String> = []
    var sshSort: ServerSort = .name
    var rdpSort: ServerSort = .original

    init(id: String = UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: ""), name: String, marker: GroupMarker? = nil) {
        self.id = id
        self.name = name
        self.marker = marker
    }
}

enum GroupMarker: String, CaseIterable, Identifiable {
    case blue, teal, green, amber, red, purple, gray
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
        case .blue: .blue
        case .teal: .teal
        case .green: .green
        case .amber: .orange
        case .red: .red
        case .purple: .purple
        case .gray: .gray
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

struct ManagerSettings {
    var groups: [ServerGroup] = []
    var startup: String?
    var appearance: Appearance = .system
    var sshSort: ServerSort = .name
    var rdpSort: ServerSort = .original
    var desktops: [DesktopProfile] = []

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
        if let theme = root.attribute(forName: "theme")?.stringValue { settings.appearance = Appearance(rawValue: theme) ?? .system }
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
        let groups = settings.groups.map(\.id)
        var desktops: Set<String> = []
        for element in root.elements(forName: "rdp") {
            let profile = try DesktopProfile(element: element, groups: groups)
            guard desktops.insert(profile.id).inserted else { throw ManagerError("RDP-профиль повторяется в настройках.") }
            settings.desktops.append(profile)
        }
        return settings
    }

    var data: Data {
        let root = XMLElement(name: "tabs")
        if let startup { Self.attribute("startupTab", value: startup, on: root) }
        if appearance != .system { Self.attribute("theme", value: appearance.rawValue, on: root) }
        Self.attribute("sshSort", value: sshSort.rawValue, on: root)
        Self.attribute("rdpSort", value: rdpSort.rawValue, on: root)
        for group in groups {
            let element = XMLElement(name: "tab")
            Self.attribute("id", value: group.id, on: element)
            Self.attribute("name", value: group.name, on: element)
            if let marker = group.marker { Self.attribute("color", value: marker.rawValue, on: element) }
            Self.attribute("sshSort", value: group.sshSort.rawValue, on: element)
            Self.attribute("rdpSort", value: group.rdpSort.rawValue, on: element)
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
            throw ManagerError("Это название уже используется.")
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
