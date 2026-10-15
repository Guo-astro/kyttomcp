import Foundation

/// Client-specific registration locations belong beside the client registry (§4).
public enum PluginRegistrySource: Sendable {
    case claudeCode(installed: PlatformPath, settings: PlatformPath, projectPath: String?)
    case codex(config: PlatformPath, cache: PlatformPath)

    public var registrationFile: PlatformPath {
        switch self {
        case .claudeCode(let installed, _, _): return installed
        case .codex(let config, _): return config
        }
    }

    static func claudeCode(projectPath: String? = nil) -> Self {
        .claudeCode(
            installed: PlatformPath(darwin: "~/.claude/plugins/installed_plugins.json", windows: #"%USERPROFILE%\.claude\plugins\installed_plugins.json"#),
            settings: PlatformPath(darwin: "~/.claude/settings.json", windows: #"%USERPROFILE%\.claude\settings.json"#),
            projectPath: projectPath
        )
    }
}
