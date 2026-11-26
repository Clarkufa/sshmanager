import AppKit
import CryptoKit
import Foundation
import SwiftUI

struct UpdateVersion: Comparable, CustomStringConvertible {
    private let parts: [Int]

    init?(_ value: String) {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        let numbers = parts.compactMap { Int($0) }
        guard parts.count == 3, numbers.count == 3,
              zip(parts, numbers).allSatisfy({ $1 >= 0 && $0 == String($1) }) else { return nil }
        self.parts = numbers
    }

    var description: String { parts.map(String.init).joined(separator: ".") }
    static func < (lhs: Self, rhs: Self) -> Bool { lhs.parts.lexicographicallyPrecedes(rhs.parts) }
}

struct MacUpdate {
    static let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    static let releasePage = URL(string: "https://github.com/Clarkufa/sshmanager/releases/latest")!
    private static let latest = URL(string: "https://api.github.com/repos/Clarkufa/sshmanager/releases/latest")!
    private static let filename = "SSH-Manager-macOS.dmg"
    private static let client = URLSession(configuration: .ephemeral, delegate: UpdateRedirects(), delegateQueue: nil)

    let version: UpdateVersion
    let size: Int
    let digest: String

    private struct Release: Decodable {
        let tag_name: String
        let draft: Bool
        let prerelease: Bool
        let assets: [Asset]
    }

    private struct Asset: Decodable {
        let name: String
        let state: String
        let size: Int
        let digest: String?
    }

    static func check() async throws -> MacUpdate? {
        let data = try await read(latest, limit: 512 * 1024, json: true)
        return try update(from: data, current: current)
    }

    static func update(from data: Data, current: String) throws -> MacUpdate? {
        let release = try JSONDecoder().decode(Release.self, from: data)
        guard !release.draft, !release.prerelease else { return nil }
        guard release.tag_name.hasPrefix("v"),
              let version = UpdateVersion(String(release.tag_name.dropFirst())),
              let current = UpdateVersion(current) else {
            throw ManagerError("Не удалось определить версию выпуска.")
        }
        guard version > current else { return nil }
        let assets = release.assets.filter { $0.name == filename }
        guard assets.count == 1, let asset = assets.first, asset.state == "uploaded",
              (1...(64 * 1024 * 1024)).contains(asset.size), let checksum = asset.digest,
              checksum.count == 71, checksum.range(of: #"^sha256:[0-9a-fA-F]{64}$"#, options: .regularExpression) != nil else {
            throw ManagerError("В выпуске нет проверяемого файла для macOS.")
        }
        return MacUpdate(version: version, size: asset.size, digest: String(checksum.dropFirst(7)).lowercased())
    }

    func download() async throws -> URL {
        let address = URL(string: "https://github.com/Clarkufa/sshmanager/releases/download/v\(version)/\(Self.filename)")!
        let data = try await Self.read(address, limit: size, json: false)
        let hash = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard data.count == size, hash == digest else {
            throw ManagerError("Контрольная сумма скачанного файла не совпала с опубликованной.")
        }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("SSH-Manager-Update-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        do {
            let image = folder.appendingPathComponent(Self.filename)
            try writePrivate(data, to: image)
            return image
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }

    private static func read(_ url: URL, limit: Int, json: Bool) async throws -> Data {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.setValue("SSHManager/\(current)", forHTTPHeaderField: "User-Agent")
        if json { request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept") }
        let (bytes, response) = try await client.bytes(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200,
              UpdateRedirects.trusted(http.url), http.expectedContentLength <= Int64(limit) else {
            throw ManagerError("GitHub не вернул ожидаемый файл обновления.")
        }
        var result = Data()
        for try await byte in bytes {
            guard result.count < limit else { throw ManagerError("Файл обновления превышает ожидаемый размер.") }
            result.append(byte)
        }
        return result
    }
}

private final class UpdateRedirects: NSObject, URLSessionTaskDelegate {
    static func trusted(_ url: URL?) -> Bool {
        guard let url, url.scheme == "https", url.user == nil, url.password == nil,
              url.port == nil || url.port == 443 else { return false }
        return ["github.com", "api.github.com", "objects.githubusercontent.com",
                "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"].contains(url.host)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(Self.trusted(request.url) ? request : nil)
    }
}

struct MacBundleReplacement {
    let live: URL
    private let stage: URL
    private let previous: URL

    static func install(_ image: URL, version: UpdateVersion, at live: URL) throws -> Self {
        let fm = FileManager.default
        guard live.pathExtension.lowercased() == "app", live.resolvingSymlinksInPath().path == live.path,
              let installed = try LocalFiles.item(live),
              installed.kind == .directory, fm.isWritableFile(atPath: live.deletingLastPathComponent().path) else {
            throw ManagerError("Для замены приложения нужна доступная для записи папка.")
        }
        let original = try info(live)
        guard let identity = original["CFBundleIdentifier"] as? String,
              let installedVersion = original["CFBundleShortVersionString"] as? String,
              let current = UpdateVersion(installedVersion), current < version else {
            throw ManagerError("Эта версия уже установлена или устарела.")
        }
        try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", "--all-architectures", live.path])
        let mount = image.deletingLastPathComponent().appendingPathComponent("volume")
        try fm.createDirectory(at: mount, withIntermediateDirectories: false)
        try run("/usr/bin/hdiutil", ["attach", "-readonly", "-nobrowse", "-noautoopen", "-mountpoint", mount.path, image.path])
        defer { try? run("/usr/bin/hdiutil", ["detach", mount.path]) }

        let source = mount.appendingPathComponent("SSH Manager.app")
        guard try LocalFiles.item(source)?.kind == .directory,
              let incoming = try? info(source), incoming["CFBundleIdentifier"] as? String == identity,
              incoming["CFBundleShortVersionString"] as? String == version.description else {
            throw ManagerError("В образе нет приложения нужной версии.")
        }

        // Stage beside the live bundle so the swap is atomic and can also be undone.
        let stage = live.deletingLastPathComponent().appendingPathComponent(".ssh-manager-update-" + UUID().uuidString)
        try fm.createDirectory(at: stage, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        let previous = stage.appendingPathComponent("SSH Manager.app")
        do {
            try run("/usr/bin/ditto", [source.path, previous.path])
            try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", "--all-architectures", previous.path])
            guard try info(live)["CFBundleShortVersionString"] as? String == installedVersion else {
                throw ManagerError("Версия приложения изменилась во время установки.")
            }
            try swap(live, previous)
            return Self(live: live, stage: stage, previous: previous)
        } catch {
            try? fm.removeItem(at: stage)
            throw error
        }
    }

    func rollback() throws {
        try Self.swap(live, previous)
        clean()
    }

    func clean() { try? FileManager.default.removeItem(at: stage) }

    private static func info(_ bundle: URL) throws -> [String: Any] {
        let url = bundle.appendingPathComponent("Contents/Info.plist")
        let data = try readRegularFile(url, limit: 512 * 1024)
        guard let fields = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else {
            throw ManagerError("Не удалось прочитать сведения о приложении.")
        }
        return fields
    }

    private static func swap(_ a: URL, _ b: URL) throws {
        guard renamex_np(a.path, b.path, UInt32(RENAME_SWAP)) == 0 else {
            throw ManagerError("Не удалось заменить приложение: \(String(cString: strerror(errno))).")
        }
    }

    private static func run(_ command: String, _ arguments: [String]) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: command)
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            throw ManagerError("Не удалось проверить или установить приложение (\(process.executableURL!.lastPathComponent)).")
        }
    }
}

@MainActor
final class MacUpdates: ObservableObject {
    @Published var presented = false
    @Published private(set) var available: MacUpdate?
    @Published private(set) var checking = false
    @Published private(set) var installing = false
    @Published private(set) var message: String?
    @Published private(set) var manualImage: URL?
    private var automaticCheckStarted = false

    func check(manual: Bool) {
        if manual { presented = true }
        else {
            guard !automaticCheckStarted else { return }
            automaticCheckStarted = true
        }
        guard !checking, !installing else { return }
        checking = true
        message = nil
        Task {
            defer { checking = false }
            do {
                available = try await MacUpdate.check()
                if available == nil { message = "Установлена актуальная версия \(MacUpdate.current)." }
            } catch {
                message = "Не удалось проверить обновления. \(error.localizedDescription)"
            }
        }
    }

    func install() {
        guard let available, !installing, !checking else { return }
        installing = true
        message = nil
        manualImage = nil
        Task {
            defer { installing = false }
            do {
                let image = try await available.download()
                let live = Bundle.main.bundleURL.absoluteURL.standardizedFileURL
                do {
                    let replacement = try await Task.detached(priority: .utility) {
                        try MacBundleReplacement.install(image, version: available.version, at: live)
                    }.value
                    let configuration = NSWorkspace.OpenConfiguration()
                    configuration.createsNewApplicationInstance = true
                    do {
                        _ = try await NSWorkspace.shared.openApplication(at: live, configuration: configuration)
                    } catch {
                        try replacement.rollback()
                        throw error
                    }
                    replacement.clean()
                    try? FileManager.default.removeItem(at: image.deletingLastPathComponent())
                    NSApp.terminate(nil)
                } catch {
                    manualImage = image
                    message = "Файл скачан и проверен. Для ручной установки откройте DMG. \(error.localizedDescription)"
                }
            } catch {
                message = "Не удалось скачать обновление. \(error.localizedDescription)"
            }
        }
    }
}

/// «Обновление SSH Manager» (docs/ui-spec.md §4.7).
struct MacUpdateView: View {
    @ObservedObject var updates: MacUpdates
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme
    @State private var confirming = false

    var body: some View {
        let palette = Palette.of(scheme)
        VStack(alignment: .leading, spacing: 8) {
            if updates.checking {
                ProgressView("Проверка…").padding(.vertical, 8)
            } else if updates.installing {
                ProgressView("Загрузка и установка…").padding(.vertical, 8)
            } else if let available = updates.available {
                Text("Доступна версия \(available.version.description)").font(.system(size: 21, weight: .semibold))
                Text("У вас версия \(MacUpdate.current)").foregroundStyle(palette.muted)
                Text("Пакет обновления DMG").foregroundStyle(palette.muted)
                Link("Что нового на GitHub", destination: MacUpdate.releasePage).padding(.top, 6)
            }
            if let message = updates.message {
                Text(message).textSelection(.enabled).fixedSize(horizontal: false, vertical: true).padding(.top, 4)
            }
            HStack {
                Spacer()
                if let image = updates.manualImage {
                    Button("Закрыть") { dismiss() }.keyboardShortcut(.cancelAction).winButton(.outline)
                    Button("Открыть DMG") { NSWorkspace.shared.open(image) }.winButton(.primary)
                } else if updates.available != nil && !updates.installing {
                    Button("Позже") { dismiss() }.keyboardShortcut(.cancelAction).winButton(.outline)
                    Button { confirming = true } label: { GlyphLabel(title: "Скачать обновление", glyph: .refresh, kind: .primary) }
                        .keyboardShortcut(.defaultAction).winButton(.primary)
                } else if !updates.installing {
                    Button("ОК") { dismiss() }.keyboardShortcut(.defaultAction).winButton(.primary)
                }
            }.padding(.top, 10)
        }
        .padding(22).frame(width: 460)
        .interactiveDismissDisabled(updates.installing)
        .alert("Установить обновление", isPresented: $confirming) {
            Button("Нет", role: .cancel) {}
            Button("Да") { updates.install() }
        } message: {
            Text("Установить версию \(updates.available?.version.description ?? "") и перезапустить SSH Manager?\n\nОткрытые сеансы SSH и окна SFTP будут закрыты.")
        }
    }
}
