import Foundation
import Network

struct VncProfile: Identifiable, Equatable {
    enum Scale: String, CaseIterable {
        case fit, native

        var title: String { self == .fit ? "По размеру окна" : "Исходный размер" }
    }

    var id = UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: "")
    var name = ""
    var host = ""
    var port = 5900
    var user = ""
    var group: String?
    var scale: Scale = .fit
    var viewOnly = false
    var clipboard = true
    var fullScreen = false
    var via = ""
    var filesVia = ""

    var address: String { "\(Self.formatHost(host)):\(port)" }
    var keychainAccount: String { "vnc\n" + id }

    static func formatHost(_ host: String) -> String { host.contains(":") ? "[\(host)]" : host }

    func validate() throws {
        guard id.range(of: #"^[0-9a-f]{32}$"#, options: .regularExpression) != nil,
              !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              [(name, 80), (host, 253), (user, 128)].allSatisfy({ value, limit in
                  value.utf16.count <= limit && !value.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains)
              }) else { throw ManagerError("Проверьте название и параметры VNC-подключения.") }
        let dns = #"^[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?(?:\.[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?)*\.?$"#
        guard IPv4Address(host) != nil || IPv6Address(host) != nil || host.range(of: dns, options: .regularExpression) != nil
        else { throw ManagerError("Укажите DNS-имя, IPv4 или IPv6 без порта.") }
        guard (1...65535).contains(port) else { throw ManagerError("Укажите порт от 1 до 65535.") }
        guard via.isEmpty || Profile.validAlias(via) else {
            throw ManagerError("Выберите SSH-сервер из списка или «Напрямую».")
        }
        guard filesVia.isEmpty || Profile.validAlias(filesVia) else {
            throw ManagerError("Выберите SSH-профиль для файлов в настройках VNC-подключения.")
        }
    }

    init(group: String? = nil) { self.group = group }

    init(element: XMLElement, groups: [String]) throws {
        func field(_ key: String) -> String? { element.attribute(forName: key)?.stringValue }
        guard let number = Int(field("port") ?? "5900"), let scale = Scale(rawValue: field("scale") ?? "fit") else {
            throw ManagerError("Некорректные параметры VNC-профиля.")
        }
        id = (field("id") ?? "").lowercased()
        name = field("name") ?? ""
        host = field("host") ?? ""
        port = number
        user = field("user") ?? ""
        group = field("group").flatMap { groups.contains($0) ? $0 : nil }
        self.scale = scale
        viewOnly = field("viewOnly") == "1"
        clipboard = field("clipboard") != "0"
        fullScreen = field("fullScreen") == "1"
        via = field("via") ?? ""
        filesVia = field("filesVia") ?? ""
        try validate()
    }

    var fields: [String: String] {
        var fields = ["id": id, "name": name, "host": host, "user": user, "port": String(port), "scale": scale.rawValue,
                      "clipboard": clipboard ? "1" : "0"]
        if let group { fields["group"] = group }
        if viewOnly { fields["viewOnly"] = "1" }
        if fullScreen { fields["fullScreen"] = "1" }
        if !via.isEmpty { fields["via"] = via }
        if !filesVia.isEmpty { fields["filesVia"] = filesVia }
        return fields
    }

    static func tigerVNC() -> URL? {
        let files = FileManager.default
        let roots = [URL(fileURLWithPath: "/Applications"), files.homeDirectoryForCurrentUser.appendingPathComponent("Applications")]
        for root in roots {
            let applications = (try? files.contentsOfDirectory(atPath: root.path)) ?? []
            for name in applications.sorted() where name.hasPrefix("TigerVNC Viewer") && name.hasSuffix(".app") {
                let viewer = root.appendingPathComponent(name).appendingPathComponent("Contents/MacOS/TigerVNC Viewer")
                if files.isExecutableFile(atPath: viewer.path) { return viewer }
            }
        }
        let paths = ["/opt/homebrew/bin/vncviewer", "/usr/local/bin/vncviewer"]
        return paths.first(where: { files.isExecutableFile(atPath: $0) }).map { URL(fileURLWithPath: $0) }
    }
}
