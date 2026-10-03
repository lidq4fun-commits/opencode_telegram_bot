import Foundation

enum WebRouting {
    static func shouldReportNavigationError(_ error: Error) -> Bool {
        let error = error as NSError
        return !(error.domain == NSURLErrorDomain && error.code == NSURLErrorCancelled)
    }
    static func navigationTarget(configured: URL?, loaded: URL?, current: URL?, reload: Bool) -> URL? {
        guard let configured else { return nil }
        if configured != loaded { return configured }
        guard reload else { return nil }
        return sameOrigin(current, configured) ? current : configured
    }
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
            let source = server.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            return server.appendingPathComponent("server/" + key(source) + "/session/" + id)
        }
        return server
    }
}
