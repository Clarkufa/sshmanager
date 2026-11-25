import Foundation
import Network

enum ServerSort: String, CaseIterable, Identifiable {
    case original = "Original", name = "Name", nameDescending = "NameDescending"
    case address = "Address", addressDescending = "AddressDescending"

    var id: Self { self }
    var title: String {
        switch self {
        case .original: "Исходный порядок"
        case .name: "По названию ↑"
        case .nameDescending: "По названию ↓"
        case .address: "По адресу ↑"
        case .addressDescending: "По адресу ↓"
        }
    }

    func apply<T>(_ values: [T], name: (T) -> String, host: (T) -> String) -> [T] {
        guard self != .original else { return values }
        let comparingAddresses = self == .address || self == .addressDescending
        let descending = self == .addressDescending || self == .nameDescending
        let keyed = values.map { value in
            (value: value, address: comparingAddresses ? AddressKey(host(value)) : nil)
        }
        return keyed.sorted { left, right in
            if let a = left.address, let b = right.address, a != b { return descending ? b < a : a < b }
            let comparison = name(left.value).localizedStandardCompare(name(right.value))
            return comparison == (descending && !comparingAddresses ? .orderedDescending : .orderedAscending)
        }.map(\.value)
    }

    private struct AddressKey: Comparable {
        let address: Data?
        let text: String

        init(_ value: String) {
            let host = value.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
            address = IPv4Address(host)?.rawValue ?? IPv6Address(host)?.rawValue
            text = host.lowercased()
        }

        static func < (left: Self, right: Self) -> Bool {
            switch (left.address, right.address) {
            case let (a?, b?):
                if a.count != b.count { return a.count < b.count }
                if a != b { return a.lexicographicallyPrecedes(b) }
                return left.text < right.text
            case (_?, nil): return true
            case (nil, _?): return false
            case (nil, nil): return left.text < right.text
            }
        }
    }
}

struct DesktopProfile: Identifiable {
    var id = UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: "")
    var name = ""
    var host = ""
    var port = 3389
    var user = ""
    var domain = ""
    var group: String? = nil
    var width = 0
    var height = 0
    var clipboard = false
    var fullScreen = false

    var address: String { "\(host.contains(":") ? "[\(host)]" : host):\(port)" }
    var account: String { domain.isEmpty || user.isEmpty ? user : "\(domain)\\\(user)" }

    func validate() throws {
        guard id.range(of: #"^[0-9a-f]{32}$"#, options: .regularExpression) != nil,
              !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              [(name, 80), (host, 253), (user, 128), (domain, 128)].allSatisfy({ value, limit in
                  value.utf16.count <= limit && !value.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains)
              }) else { throw ManagerError("Проверьте название и параметры RDP-подключения.") }
        let dns = #"^[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?(?:\.[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?)*\.?$"#
        guard IPv4Address(host) != nil || IPv6Address(host) != nil || host.range(of: dns, options: .regularExpression) != nil
        else { throw ManagerError("Укажите DNS-имя, IPv4 или IPv6 без порта.") }
        guard (1...65535).contains(port) else { throw ManagerError("Укажите порт от 1 до 65535.") }
        guard (width == 0 && height == 0) || ((640...8192).contains(width) && (480...8192).contains(height))
        else { throw ManagerError("Укажите разрешение от 640 × 480 до 8192 × 8192 или оставьте оба поля пустыми.") }
    }

    init(group: String? = nil) { self.group = group }

    init(element: XMLElement, groups: [String]) throws {
        func field(_ key: String) -> String? { element.attribute(forName: key)?.stringValue }
        func number(_ key: String, _ fallback: Int) throws -> Int {
            guard let text = field(key) else { return fallback }
            guard let parsed = Int(text) else { throw ManagerError("Некорректные параметры RDP-профиля.") }
            return parsed
        }
        id = field("id") ?? ""
        name = field("name") ?? ""
        host = field("host") ?? ""
        port = try number("port", 3389)
        user = field("user") ?? ""
        domain = field("domain") ?? ""
        group = field("group").flatMap { groups.contains($0) ? $0 : nil }
        width = try number("width", 0)
        height = try number("height", 0)
        clipboard = field("clipboard") == "1"
        fullScreen = field("fullScreen") == "1"
        try validate()
    }

    var fields: [String: String] {
        var fields = ["id": id, "name": name, "host": host, "port": String(port), "user": user, "domain": domain]
        if let group { fields["group"] = group }
        if width != 0 { fields["width"] = String(width); fields["height"] = String(height) }
        if clipboard { fields["clipboard"] = "1" }
        if fullScreen { fields["fullScreen"] = "1" }
        return fields
    }

    init(file: URL, group: String?) throws {
        self.init(group: group)
        let input = try readRegularFile(file, limit: 256 * 1024)
        let encoding: String.Encoding = input.starts(with: [0xff, 0xfe]) || input.starts(with: [0xfe, 0xff]) ? .utf16 : .utf8
        guard let text = String(data: input, encoding: encoding) else { throw ManagerError("Не удалось прочитать файл .rdp.") }
        var fields: [String: String] = [:]
        for line in text.trimmingCharacters(in: CharacterSet(charactersIn: "\u{feff}")).components(separatedBy: .newlines) {
            let parts = line.split(separator: ":", maxSplits: 2, omittingEmptySubsequences: false)
            if parts.count == 3 {
                fields[parts[0].trimmingCharacters(in: .whitespaces).lowercased()] = parts[2].trimmingCharacters(in: .whitespaces)
            }
        }
        name = file.deletingPathExtension().lastPathComponent
        host = fields["full address"] ?? ""
        let portSeparator = host.hasPrefix("[") ? host.range(of: "]:").map { host.index(after: $0.lowerBound) } :
            host.filter { $0 == ":" }.count == 1 ? host.firstIndex(of: ":") : nil
        var portText = fields["server port"] ?? "3389"
        if let portSeparator {
            portText = String(host[host.index(after: portSeparator)...])
            host = String(host[..<portSeparator])
        }
        host = host.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        port = Int(portText) ?? 0
        user = fields["username"] ?? ""
        domain = fields["domain"] ?? ""
        if domain.isEmpty, let slash = user.firstIndex(of: "\\") {
            domain = String(user[..<slash])
            user = String(user[user.index(after: slash)...])
        }
        width = Int(fields["desktopwidth"] ?? "") ?? 0
        height = Int(fields["desktopheight"] ?? "") ?? 0
        if width == 0 || height == 0 { width = 0; height = 0 }
        clipboard = fields["redirectclipboard"] == "1"
        fullScreen = fields["screen mode id"] == "2"
        try validate()
    }

    var data: Data {
        var lines = ["full address:s:\(address)", "username:s:\(account)", "screen mode id:i:\(fullScreen ? 2 : 1)",
                     "redirectclipboard:i:\(clipboard ? 1 : 0)", "enablecredsspsupport:i:1", "authentication level:i:2",
                     "drivestoredirect:s:", "redirectprinters:i:0"]
        if width != 0 { lines.append(contentsOf: ["desktopwidth:i:\(width)", "desktopheight:i:\(height)"]) }
        let text = lines.joined(separator: "\r\n") + "\r\n"
        return Data([0xff, 0xfe]) + Data(text.utf16.flatMap { [UInt8(truncatingIfNeeded: $0), UInt8($0 >> 8)] })
    }
}
