//
//  KyttoMCPApp.swift
//  KyttoMCP
//

import AppKit
import SwiftUI

@main
struct KyttoMCPApp: App {
    @State private var router: CommandRouter
    @State private var model: AppModel
    @State private var menuBar: MenuBarController
    @State private var mainWindow: MainWindowLink

    @Environment(\.openWindow) private var openWindow

    init() {
        // Registered before the web view is built, because the page starts
        // calling commands as soon as it loads. Doing this in a `.task` would be
        // a race the UI would lose intermittently.
        let router = CommandRouter()
        let model = AppModel()
        // Earlier versions stored backups world-readable. Tightened before the
        // page can send its first command, so it never runs beside a write.
        model.tightenStoragePermissions()
        CommandRegistry.registerAll(on: router, model: model)
        // A config changing on disk — whoever changed it — pushes fresh state
        // into the UI rather than letting it show something stale (§6.4).
        let pushState: @MainActor () -> Void = { [weak router] in
            guard let router else { return }
            Task { await router.emit("configs.changed", payload: StateDTO(model)) }
        }
        model.onExternalChange = pushState

        let mainWindow = MainWindowLink {
            model.reload()
            pushState()
        }
        let menuBar = MenuBarController(model: model) { mainWindow.open() }
        // The page's own commands reply with fresh state; changes made from the
        // menu bar item have nobody to reply to, so they are pushed instead.
        menuBar.onModelChanged = pushState
        model.onStateChanged = { [weak menuBar] in menuBar?.refresh() }
        model.onSettingsChanged = { [weak menuBar, weak router] settings, stateChanged in
            menuBar?.apply(settings: settings)
            guard let router else { return }
            Task {
                await router.emit("settings.changed", payload: SettingsDTO(settings))
                if stateChanged {
                    await router.emit("configs.changed", payload: StateDTO(model))
                }
            }
        }

        _router = State(initialValue: router)
        _model = State(initialValue: model)
        _menuBar = State(initialValue: menuBar)
        _mainWindow = State(initialValue: mainWindow)
    }

    var body: some Scene {
        // Handed to the link so the menu bar item can reopen a window that was
        // closed. Reading the environment needs a body; calling it later does not.
        let _ = (mainWindow.openWindow = openWindow)
        Window("Kytto", id: MainWindowLink.id) {
            WebView(router: router)
                // The matrix needs width before it needs height; below this the
                // client columns stop being readable.
                .frame(minWidth: 860, minHeight: 480)
                // Otherwise SwiftUI parks the whole page below the title bar and
                // the transparent strip shows the window's own background —
                // a second, emptier toolbar above the real one.
                .ignoresSafeArea()
                .task { menuBar.apply(settings: model.settings) }
        }
        // No native toolbar: the title bar is a transparent strip the page draws
        // its own toolbar band under, and ChromeView tells it how tall that strip
        // is. A toolbar style here would size a bar that has nothing in it.
        .defaultSize(width: 1080, height: 700)
        .commands { MenuCommands(router: router) }

        Settings {
            SettingsView(model: model)
        }
    }
}

/// The shell's hold on the main window from outside SwiftUI.
///
/// The menu bar item has to bring the window forward even after it was closed,
/// when SwiftUI may already have released its `NSWindow` — only `openWindow`
/// can rebuild it. And the window coming forward is the moment the user looks
/// at it again, so it re-reads the configs then: a change the file watcher
/// missed, or a health result written into Kytto's own (unwatched) directory,
/// should not wait for a click to show up (§6.4).
@MainActor
final class MainWindowLink {
    static let id = "main"

    var openWindow: OpenWindowAction?
    private let onBecameKey: @MainActor () -> Void
    private var lastRefresh = Date.distantPast
    private var observer: (any NSObjectProtocol)?

    init(onBecameKey: @escaping @MainActor () -> Void) {
        self.onBecameKey = onBecameKey
        observer = NotificationCenter.default.addObserver(
            forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main
        ) { [weak self] note in
            MainActor.assumeIsolated {
                guard let window = note.object as? NSWindow, Self.isMain(window) else { return }
                self?.becameKey()
            }
        }
    }

    func open() {
        // Fast path: the window is still there and only needs bringing forward.
        if let window = NSApp.windows.first(where: Self.isMain),
           window.isVisible || window.isMiniaturized {
            if window.isMiniaturized { window.deminiaturize(nil) }
            window.makeKeyAndOrderFront(nil)
            return
        }
        if let openWindow {
            openWindow(id: Self.id)
        } else {
            NSApp.windows.first(where: Self.isMain)?.makeKeyAndOrderFront(nil)
        }
    }

    /// Sheets and panels hand key status back to the window as they close, so
    /// a burst of these is one refresh, not several discovery passes.
    private func becameKey() {
        let now = Date()
        guard now.timeIntervalSince(lastRefresh) > 2 else { return }
        lastRefresh = now
        onBecameKey()
    }

    private static func isMain(_ window: NSWindow) -> Bool {
        window.identifier?.rawValue.contains(id) == true
    }
}
