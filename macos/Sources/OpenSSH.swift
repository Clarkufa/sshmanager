import AppKit
import Foundation

struct SSHRequest {
    let profile: Profile
    let configURL: URL?
    private let passwordHost: String?

    init(profile: Profile, configURL: URL? = nil) {
        self.profile = profile
        self.configURL = configURL
        passwordHost = (try? Passwords.read(profile.account)) != nil ? profile.passwordHost : nil
    }

    func arguments(sftp: Bool = false, noForwarding: Bool = false) -> [String] {
        var result = ["-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-o", "ConnectTimeout=15"]
        if let configURL { result += ["-F", configURL.path] }
        if let passwordHost {
            // Bind an automatic response to the same user, host and port as the Keychain entry.
            result += ["-o", "HostName=" + passwordHost, "-l", profile.user, "-p", String(Int(profile.port) ?? 22),
                       "-o", "BatchMode=no", "-o", "PasswordAuthentication=yes"]
        }
        if sftp || noForwarding { result += ["-o", "ClearAllForwardings=yes"] }
        if sftp {
            result += ["-T", "-o", "RemoteCommand=none", "-o", "SessionType=subsystem",
                       "-o", "ControlMaster=no", "-o", "ControlPath=none", "-o", "BatchMode=no", "-s", "--", profile.alias, "sftp"]
        } else {
            result += ["--", profile.alias]
        }
        return result
    }

    func environment(prompt: Bool) -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        env["TERM"] = "xterm-256color"
        // macOS accepts this bare locale name, but OpenSSH may forward it to a server that doesn't.
        if env["LC_CTYPE"]?.caseInsensitiveCompare("UTF-8") == .orderedSame {
            env.removeValue(forKey: "LC_CTYPE")
        }
        // Enable a native prompt for headless SFTP, or to use a saved password from the app.
        if prompt || passwordHost != nil, let executable = Bundle.main.executablePath {
            env["SSH_ASKPASS"] = executable
            env["SSH_ASKPASS_REQUIRE"] = "force"
            env["DISPLAY"] = env["DISPLAY"] ?? "SSH Manager"
            env["SSH_MANAGER_ASKPASS"] = "1"
            env["SSH_MANAGER_ASKPASS_ACCOUNT"] = profile.account
            env["SSH_MANAGER_ASKPASS_TARGET"] = passwordHost.map { profile.user + "@" + $0 }
            env.removeValue(forKey: "SSH_ASKPASS_PROMPT")
        }
        return env
    }
}

enum AskPass {
    static var requested: Bool {
        ProcessInfo.processInfo.environment["SSH_MANAGER_ASKPASS"] == "1" && CommandLine.arguments.count == 2
    }

    static func run() -> Never {
        let environment = ProcessInfo.processInfo.environment
        let prompt = CommandLine.arguments[1]
        let mode = environment["SSH_ASKPASS_PROMPT"]
        if mode == "none" { exit(EXIT_FAILURE) }
        if mode != "confirm", let target = environment["SSH_MANAGER_ASKPASS_TARGET"],
           prompt.trimmingCharacters(in: .whitespacesAndNewlines) == "\(target)'s password:",
           let account = environment["SSH_MANAGER_ASKPASS_ACCOUNT"],
           let password = try? Passwords.read(account) {
            answer(password)
        }

        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        app.activate(ignoringOtherApps: true)

        let alert = NSAlert()
        alert.messageText = mode == "confirm" ? "Подтвердите ключ сервера" : "Запрос OpenSSH"
        alert.informativeText = prompt
        alert.addButton(withTitle: mode == "confirm" ? "Подтвердить" : "Продолжить")
        let cancel = alert.addButton(withTitle: "Отмена")
        if mode == "confirm" {
            alert.buttons[0].keyEquivalent = ""
            cancel.keyEquivalent = "\r"
            if alert.runModal() == .alertFirstButtonReturn { answer("yes") }
        } else {
            let input = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 26))
            input.placeholderString = "Пароль или код"
            alert.accessoryView = input
            alert.window.initialFirstResponder = input
            if alert.runModal() == .alertFirstButtonReturn { answer(input.stringValue) }
        }
        exit(EXIT_FAILURE)
    }

    private static func answer(_ response: String) -> Never {
        guard response.utf8.count <= 1022, !response.contains(where: \.isNewline), !response.contains("\0") else { exit(EXIT_FAILURE) }
        FileHandle.standardOutput.write(Data((response + "\n").utf8))
        exit(EXIT_SUCCESS)
    }
}
