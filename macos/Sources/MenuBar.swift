import AppKit

/// The menu bar icon with «Открыть SSH Manager» / «Выход» (docs/ui-spec.md §5). Closing the window keeps the
/// app running with its sessions; the icon, the Dock icon or launching again shows the window.
final class AppDelegate: NSObject, NSApplicationDelegate {
    static var reopen: (() -> Void)?
    /// «Оставлять работать в строке меню при закрытии окна» from the settings.
    static var closeToTray = true
    private var statusItem: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = item.button {
            let image = NSApp.applicationIconImage.copy() as? NSImage
            image?.size = NSSize(width: 18, height: 18)
            button.image = image
            button.toolTip = "SSH Manager"
        }
        let menu = NSMenu()
        let open = NSMenuItem(title: "Открыть SSH Manager", action: #selector(showWindow), keyEquivalent: "")
        open.target = self
        menu.addItem(open)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Выход", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(quit)
        item.menu = menu
        statusItem = item
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { !AppDelegate.closeToTray }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { showWindow() }
        return true
    }

    @objc func showWindow() {
        NSApp.activate(ignoringOtherApps: true)
        if let window = NSApp.windows.first(where: { $0.identifier?.rawValue.hasPrefix("main") == true && $0.canBecomeMain }) {
            window.makeKeyAndOrderFront(nil)
        } else {
            AppDelegate.reopen?()
        }
    }
}
