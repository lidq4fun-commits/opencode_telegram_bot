import AppKit

extension AppModel {
    func nativeCommand(_ args: [String]) async throws -> String? {
        guard let name = args.first else { return "" }
        switch name {
        case "help", "--help", "-h":
            return """
            start | stop | status | logs | environment
            config [set <key> <value> | unset <key>]
            opencode scan | install v2 | uninstall v2
            web start | stop | status | open
            autostart on | off | status
            app-autostart on | off | status
            language zh | en
            Secrets are not printed. Running commands accept hidden input.
            """
        case "start":
            try save(); try await connect()
            return try await Runtime.bot(["start", "--daemon", "--mode", "installed"], settings: settings)
        case "stop": return try await Runtime.bot(["stop"], settings: settings)
        case "status": return try await Runtime.bot(["status"], settings: settings)
        case "environment": return "App: \(Bundle.main.bundleURL.path)\nHome: \(SettingsStore.home.path)\nEngine: \(Runtime.engine(settings).path)"
        case "logs": refreshLogs(); return logs
        case "language":
            guard args.count == 2, ["zh", "en"].contains(args[1]) else { throw AppFailure.message("Use: language zh|en") }
            settings.locale = args[1]; try save(); return text("语言已保存。", "Language saved.")
        case "autostart", "app-autostart":
            let kind = name == "autostart" ? "bot" : "app"
            let action = args.count > 1 ? args[1] : "status"
            if action == "status" { return StartupManager.enabled(kind) ? "on" : "off" }
            guard ["on", "off"].contains(action) else { throw AppFailure.message("Use: \(name) on|off|status") }
            try save(); try StartupManager.set(kind, enabled: action == "on")
            appStartup = StartupManager.enabled("app"); botStartup = StartupManager.enabled("bot")
            return text("下次登录生效。", "Applies at next login.")
        case "web":
            guard args.count == 2 else { throw AppFailure.message("Use: web start|stop|status|open") }
            switch args[1] {
            case "start": try await connect(); return settings.serverURL
            case "stop": try await stopWeb(); return "stopped"
            case "open":
                try await connect()
                if let url = webURL { NSWorkspace.shared.open(url) }
                return "opened"
            case "status":
                return try await Runtime.engineCommand(["service", "status"], settings: settings)
            default: throw AppFailure.message("Use: web start|stop|status|open")
            }
        case "opencode":
            guard args.count >= 2 else { throw AppFailure.message("Use: opencode scan|version|install|uninstall") }
            if args[1] == "scan" {
                engines = await EngineManager.scan(settings: settings)
                return engines.map { "\($0.version)  \($0.path)" }.joined(separator: "\n")
            }
            guard args.count == 3, args[2] == "v2" else { throw AppFailure.message("Only OpenCode V2 is supported.") }
            let version = args[2]
            switch args[1] {
            case "install": settings.executable = try await EngineManager.install(version, settings: settings)
            case "uninstall":
                if settings.apiVersion == version { _ = try await Runtime.bot(["stop"], settings: settings); try await stopWeb() }
                try await EngineManager.uninstall(version)
                if settings.executable == (try EngineManager.managed(version)).path { settings.executable = "" }
                try save(); return "Managed engine removed."
            default: throw AppFailure.message("Unknown engine command.")
            }
            settings.apiVersion = version; settings.serverURL = "http://127.0.0.1:49374"
            try save(); try EngineShellPath.sync(Runtime.engine(settings)); return "Selected \(version)."
        case "config":
            if args.count == 1 {
                return "Bot token: \(settings.botToken.isEmpty ? "not set" : "saved (hidden)")\nAllowed user: \(settings.allowedUserID)\nEOT10: \(settings.encryptionEnabled)\nOpenCode: \(settings.apiVersion) \(settings.serverURL)\nModel: \(settings.modelProvider)/\(settings.modelID)"
            }
            guard args.count >= 3 else { throw AppFailure.message("Use: config set <key> <value> | unset <key>") }
            let key = args[2], value: String
            if args[1] == "unset", args.count == 3 { value = "" }
            else if args[1] == "set", args.count == 4 { value = args[3] }
            else if args[1] == "set", args.count == 5, args[3] == "--env", let supplied = ProcessInfo.processInfo.environment[args[4]] { value = supplied }
            else { throw AppFailure.message("Use quoted values or --env NAME for secrets.") }
            switch key {
            case "bot-token": settings.botToken = value
            case "user-id": settings.allowedUserID = value
            case "eot10.password": settings.encryptionPassword = value; if value.isEmpty { settings.encryptionEnabled = false }
            case "eot10.enabled": guard ["true", "false", "1", "0"].contains(value) else { throw AppFailure.message("Expected true or false.") }; settings.encryptionEnabled = ["true", "1"].contains(value)
            case "opencode.url": guard let url = URL(string: value), ["http", "https"].contains(url.scheme ?? ""), url.host != nil else { throw AppFailure.message("Invalid server URL.") }; settings.serverURL = value
            case "opencode.username": settings.serverUsername = value
            case "opencode.password": settings.serverPassword = value
            case "opencode.executable": settings.executable = value
            case "model.provider": settings.modelProvider = value
            case "model.id": settings.modelID = value
            default: throw AppFailure.message("Unknown configuration key.")
            }
            try save()
            if key == "opencode.executable" { try EngineShellPath.sync(Runtime.engine(settings)) }
            return text("设置已保存（秘密值不显示）。", "Settings saved (secret values are hidden).")
        default: return nil
        }
    }
}
