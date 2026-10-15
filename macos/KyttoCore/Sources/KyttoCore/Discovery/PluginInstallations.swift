import Foundation

struct PluginInstallation {
    let clientID: ClientID
    let pluginID: String
    let root: URL
    let manifest: JSONDocument?
    let version: String?
    let projectPath: String?

    var name: String {
        manifest?.root["name"]?.stringValue ?? String(pluginID.split(separator: "@").first ?? "plugin")
    }

    var displayName: String {
        manifest?.root["displayName"]?.stringValue
            ?? manifest?.root["interface"]?["displayName"]?.stringValue
            ?? name
    }
}

/// Reads only installation registrations and their declared component files.
/// No network, process execution, directory creation or recursive cache walk.
enum PluginInstallations {
    struct Result {
        var packages: [PluginInstallation] = []
        var warnings: [String] = []
    }

    static func read(
        _ source: PluginRegistrySource,
        home: URL,
        codexConfig: URL?,
        text: (URL) -> String?
    ) -> Result {
        switch source {
        case .claudeCode(let installed, let settings, let project):
            return claude(installed: installed.resolve(home: home), settings: settings.resolve(home: home), project: project, text: text)
        case .codex(let config, let cache):
            return codex(config: codexConfig ?? config.resolve(home: home), cache: cache.resolve(home: home), text: text)
        }
    }

    private static func claude(installed: URL, settings: URL, project: String?, text: (URL) -> String?) -> Result {
        var result = Result()
        guard let source = text(installed) else { return result }
        guard let registry = try? JSONDocument.parse(source),
              registry.root["version"]?.numberValue == 2,
              let plugins = registry.root["plugins"]?.members
        else {
            result.warnings.append("Claude Code plugin registrations are unreadable or use an unsupported format. Cached packages were not scanned.")
            return result
        }

        var enabled: [String: Bool] = [:]
        var settingsURLs = [settings]
        if let project {
            settingsURLs += ["settings.json", "settings.local.json"].map {
                URL(filePath: project).appending(path: ".claude").appending(path: $0)
            }
        }
        for url in settingsURLs {
            guard let source = text(url) else { continue }
            guard let document = try? JSONDocument.parse(source) else {
                result.warnings.append("Claude Code plugin settings could not be read. Plugin discovery was skipped rather than guessing enablement.")
                return result
            }
            for entry in document.root["enabledPlugins"]?.members ?? [] {
                if let value = entry.value.boolValue { enabled[entry.key] = value }
            }
        }

        for plugin in plugins.sorted(by: { $0.key < $1.key }) {
            guard enabled[plugin.key] != false else { continue }
            let matching = (plugin.value.elements ?? []).filter { entry in
                let scope = entry["scope"]?.stringValue
                if let project {
                    return scope == "user" || (["project", "local"].contains(scope ?? "") && entry["projectPath"]?.stringValue == project)
                }
                return scope == "user"
            }
            // The most specific installation wins. More than one installation
            // at that scope is ambiguous: timestamps/version sorting cannot prove
            // which copy the client will load.
            let priority: (JSONNode) -> Int = { ["user": 0, "project": 1, "local": 2][$0["scope"]?.stringValue ?? ""] ?? -1 }
            guard let highest = matching.map(priority).max() else { continue }
            let candidates = matching.filter { priority($0) == highest }
            guard candidates.count == 1, let entry = candidates.first,
                  let path = entry["installPath"]?.stringValue, path.hasPrefix("/"),
                  let root = directory(URL(filePath: path))
            else {
                result.warnings.append("A Claude Code plugin has a missing or ambiguous registered installation; its cached copies were not scanned.")
                continue
            }
            let manifestURL = root.appending(path: ".claude-plugin/plugin.json")
            let manifestSource = text(manifestURL)
            let manifest = manifestSource.flatMap { try? JSONDocument.parse($0) }
            if manifestSource != nil && manifest == nil {
                result.warnings.append("An installed Claude Code plugin has an unreadable manifest and was skipped.")
                continue
            }
            if enabled[plugin.key] == nil, manifest?.root["defaultEnabled"]?.boolValue == false { continue }
            result.packages.append(PluginInstallation(
                clientID: .claudeCode, pluginID: plugin.key, root: root, manifest: manifest,
                version: entry["version"]?.stringValue ?? manifest?.root["version"]?.stringValue,
                projectPath: project
            ))
        }
        return result
    }

    private static func codex(config: URL, cache: URL, text: (URL) -> String?) -> Result {
        var result = Result()
        guard let source = text(config), let document = try? TOMLDocument.parse(source) else { return result }
        for table in document.tables where table.path.count == 2 && table.path.first == "plugins" {
            guard table.value("enabled")?.boolValue == true else { continue }
            let id = table.path[1]
            let parts = id.split(separator: "@", omittingEmptySubsequences: false)
            guard parts.count == 2, parts.allSatisfy({ safeComponent(String($0)) }) else { continue }
            let packageRoot = cache.appending(path: String(parts[1])).appending(path: String(parts[0]))
            guard let base = directory(packageRoot),
                  let entries = try? FileManager.default.contentsOfDirectory(at: base, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            else { continue }
            // Version selection is deliberately conservative. A cache is not an
            // installation registry; only one package version can establish the
            // launch context without asking the client to execute anything.
            let versions = entries.filter {
                guard directory($0) != nil else { return false }
                return FileManager.default.fileExists(atPath: $0.appending(path: ".codex-plugin/plugin.json").path)
            }
            guard versions.count == 1, let root = versions.first,
                  let manifestSource = text(root.appending(path: ".codex-plugin/plugin.json")),
                  let manifest = try? JSONDocument.parse(manifestSource)
            else {
                if !versions.isEmpty {
                    result.warnings.append("An enabled Codex plugin has several cached versions or an unreadable manifest. Kytto skipped it because the active version is uncertain.")
                }
                continue
            }
            result.packages.append(PluginInstallation(
                clientID: .codex, pluginID: id, root: root, manifest: manifest,
                version: manifest.root["version"]?.stringValue ?? root.lastPathComponent, projectPath: nil
            ))
        }
        return result
    }

    private static func safeComponent(_ value: String) -> Bool {
        !value.isEmpty && value != "." && value != ".." && !value.contains("/") && !value.contains("\\") && !value.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
    }

    /// Symlinks are not followed through plugin registry components. Component
    /// manifest paths receive an additional realpath containment check below.
    private static func directory(_ url: URL) -> URL? {
        guard let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]),
              values.isDirectory == true, values.isSymbolicLink != true
        else { return nil }
        return url.standardizedFileURL
    }

    static func containedFile(_ path: String, root: URL) -> URL? {
        guard path.hasPrefix("./"), !path.split(separator: "/").contains(".."), path.hasSuffix(".json") else { return nil }
        let url = root.appending(path: String(path.dropFirst(2))).standardizedFileURL
        let realRoot = root.resolvingSymlinksInPath().path
        guard url.resolvingSymlinksInPath().path.hasPrefix(realRoot + "/"),
              let values = try? url.resourceValues(forKeys: [.isRegularFileKey]), values.isRegularFile == true
        else { return nil }
        return url
    }
}
