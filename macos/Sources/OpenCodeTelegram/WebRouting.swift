import Foundation

enum WebRouting {
    static func sameOrigin(_ a: URL?, _ b: URL?) -> Bool {
        guard let a, let b else { return false }
        return a.scheme == b.scheme && a.host == b.host && (a.port ?? (a.scheme == "https" ? 443 : 80)) == (b.port ?? (b.scheme == "https" ? 443 : 80))
    }
    static func target(_ state: [String: Any], server: URL, version: String) -> URL {
        func key(_ text: String) -> String {
            Data(text.utf8).base64EncodedString().replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
        }
        if let session = state["currentSession"] as? [String: Any], let id = session["id"] as? String,
           id.range(of: "^ses_[a-zA-Z0-9_-]+$", options: .regularExpression) != nil {
            let source = version == "v2" ? server.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/")) : (session["directory"] as? String ?? "")
            return server.appendingPathComponent((version == "v2" ? "server/" : "") + key(source) + "/session/" + id)
        }
        if version == "v1", let project = state["currentProject"] as? [String: Any], let directory = project["worktree"] as? String, !directory.isEmpty {
            return server.appendingPathComponent(key(directory) + "/session")
        }
        return server
    }
}
