import AppKit
import Foundation
import KyttoCore
import UniformTypeIdentifiers

/// File paths and replacement values never enter the web process. It receives
/// only an opaque, digest-guarded preview after this native form is accepted.
@MainActor
enum PortableSetupController {
    static func export(model: AppModel) throws -> PortableExportDTO {
        let alert = NSAlert()
        alert.messageText = "Export portable setup"
        alert.informativeText = "Exports writable server definitions and profiles. Credentials, URLs, private paths and unknown arguments become required inputs on the destination Mac. Plugin installations are not copied."
        alert.addButton(withTitle: "Choose file…")
        alert.addButton(withTitle: "Cancel")
        let include = NSButton(checkboxWithTitle: "Include reviewed contract fingerprints", target: nil, action: nil)
        include.state = .off
        alert.accessoryView = include
        guard alert.runModal() == .alertFirstButtonReturn else { return .cancelled }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "kytto-setup.json"
        guard panel.runModal() == .OK, let file = panel.url else { return .cancelled }
        let summary = try model.exportPortableSetup(to: file, includeReviewedContracts: include.state == .on)
        return PortableExportDTO(cancelled: false, serverCount: summary.serverNames.count, profileCount: summary.profileNames.count, warnings: summary.warnings)
    }

    static func preview(model: AppModel) throws -> PortableImportPreview? {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let file = panel.url else { return nil }
        let summary = try model.inspectPortableSetup(file: file)
        let clients = model.current().clients.filter { !$0.isReadOnly && $0.id.isBuiltIn && $0.isInstalled }
        let form = NSStackView()
        form.orientation = .vertical
        form.alignment = .leading
        form.spacing = 8
        var choices: [(ClientID, NSButton)] = []
        if !summary.serverNames.isEmpty {
            form.addArrangedSubview(NSTextField(labelWithString: "Import disabled servers into:"))
            for client in clients {
                let choice = NSButton(checkboxWithTitle: client.displayName, target: nil, action: nil)
                if choices.isEmpty { choice.state = .on }
                form.addArrangedSubview(choice)
                choices.append((client.id, choice))
            }
        }
        var inputs: [(String, NSTextField)] = []
        for requirement in summary.requirements {
            let label = NSTextField(wrappingLabelWithString: requirement.label)
            label.font = .systemFont(ofSize: 11)
            label.widthAnchor.constraint(equalToConstant: 410).isActive = true
            form.addArrangedSubview(label)
            let field: NSTextField = requirement.isSecret ? NSSecureTextField() : NSTextField()
            field.placeholderString = requirement.kind == .path ? "Path on this Mac" : "Required value (kept native)"
            field.translatesAutoresizingMaskIntoConstraints = false
            field.widthAnchor.constraint(equalToConstant: 410).isActive = true
            form.addArrangedSubview(field)
            inputs.append((requirement.id, field))
        }
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 440, height: min(430, max(100, form.fittingSize.height + 24))))
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        form.frame = NSRect(x: 8, y: 0, width: 420, height: form.fittingSize.height)
        scroll.documentView = form
        let alert = NSAlert()
        alert.messageText = "Prepare portable import"
        alert.informativeText = "\(summary.serverNames.count) servers · \(summary.profileNames.count) profiles. Fill required values and choose destinations. The next screen previews the import; nothing is written yet. Existing names will not be overwritten."
        alert.accessoryView = scroll
        alert.addButton(withTitle: "Preview")
        alert.addButton(withTitle: "Cancel")
        while alert.runModal() == .alertFirstButtonReturn {
            do {
                return try model.previewPortableSetup(
                    file: file,
                    targetClients: choices.filter { $0.1.state == .on }.map(\.0),
                    values: Dictionary(uniqueKeysWithValues: inputs.map { ($0.0, $0.1.stringValue) })
                )
            } catch {
                let validation = NSAlert()
                validation.messageText = "Import needs attention"
                validation.informativeText = error.localizedDescription
                validation.addButton(withTitle: "Back to inputs")
                validation.runModal()
            }
        }
        return nil
    }
}

struct PortableExportDTO: Encodable {
    let cancelled: Bool
    let serverCount: Int
    let profileCount: Int
    let warnings: [String]
    static let cancelled = Self(cancelled: true, serverCount: 0, profileCount: 0, warnings: [])
}
