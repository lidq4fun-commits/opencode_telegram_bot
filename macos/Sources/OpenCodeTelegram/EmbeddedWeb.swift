import SwiftUI
import WebKit

struct EmbeddedWeb: NSViewRepresentable {
    @ObservedObject var model: AppModel
    func makeCoordinator() -> Coordinator { Coordinator(model) }
    func makeNSView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        let scriptURL = Bundle.main.resourceURL?.appendingPathComponent("EmbeddedWebSync.js")
        if let scriptURL, let script = try? String(contentsOf: scriptURL) {
            // Preserve the Windows script's metadata-only contract.
            let shim = "window.chrome=window.chrome||{};window.chrome.webview={postMessage:function(v){window.webkit.messageHandlers.desktopSync.postMessage(v)}};"
            config.userContentController.addUserScript(WKUserScript(source: shim + script, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        }
        config.userContentController.add(context.coordinator, name: "desktopSync")
        let view = WKWebView(frame: .zero, configuration: config)
        view.navigationDelegate = context.coordinator
        context.coordinator.view = view
        context.coordinator.timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak coordinator = context.coordinator] _ in coordinator?.refresh() }
        return view
    }
    func updateNSView(_ view: WKWebView, context: Context) {
        if let url = model.webURL, context.coordinator.loadedURL != url {
            context.coordinator.loadedURL = url
            view.load(URLRequest(url: url))
        }
        if context.coordinator.reloadRevision != model.webReloadRevision {
            context.coordinator.reloadRevision = model.webReloadRevision
            if let current = view.url, WebRouting.sameOrigin(current, context.coordinator.loadedURL) {
                view.load(URLRequest(url: model.authenticatedURL(current)))
            }
        }
    }
    static func dismantleNSView(_ view: WKWebView, coordinator: Coordinator) {
        coordinator.timer?.invalidate()
        view.configuration.userContentController.removeScriptMessageHandler(forName: "desktopSync")
        view.navigationDelegate = nil
    }
    final class Coordinator: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
        let model: AppModel
        weak var view: WKWebView?
        var timer: Timer?
        var loadedURL: URL?
        var reloadRevision = 0
        init(_ model: AppModel) { self.model = model }
        func sameOrigin(_ a: URL?, _ b: URL?) -> Bool {
            WebRouting.sameOrigin(a, b)
        }
        func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
            guard message.frameInfo.isMainFrame, sameOrigin(message.frameInfo.request.url, loadedURL),
                  let body = message.body as? [String: Any], let id = body["id"] as? String,
                  id.range(of: "^[a-zA-Z0-9-]{1,80}$", options: .regularExpression) != nil else { return }
            do {
                let folder = SettingsStore.home.appendingPathComponent("desktop-sync", isDirectory: true)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                let data = try JSONSerialization.data(withJSONObject: body)
                try data.write(to: folder.appendingPathComponent("command-\(id).json"), options: .atomic)
            } catch { model.error = error.localizedDescription }
        }
        func refresh() {
            guard let view, sameOrigin(view.url, loadedURL), let server = URL(string: model.settings.serverURL) else { return }
            let file = SettingsStore.home.appendingPathComponent("desktop-sync/state.json")
            guard let data = try? Data(contentsOf: file), let state = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let updated = state["updatedAt"] as? Double, Date().timeIntervalSince1970 * 1000 - updated < 6000 else { return }
            let target = WebRouting.target(state, server: server, version: model.settings.apiVersion)
            let arguments: [Any] = [state, model.authenticatedURL(target).absoluteString, model.settings.apiVersion, model.settings.locale]
            guard let encoded = try? JSONSerialization.data(withJSONObject: arguments), let json = String(data: encoded, encoding: .utf8) else { return }
            view.evaluateJavaScript("window.__opencodeTelegramSync && window.__opencodeTelegramSync.accept.apply(null,\(json))", completionHandler: nil)
        }
        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { refresh() }
        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { model.error = error.localizedDescription }
        func webView(_ webView: WKWebView, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
            let method = challenge.protectionSpace.authenticationMethod
            guard method == NSURLAuthenticationMethodHTTPBasic || method == NSURLAuthenticationMethodHTTPDigest else { completionHandler(.performDefaultHandling, nil); return }
            guard let url = loadedURL, url.host == challenge.protectionSpace.host,
                  (url.port ?? (url.scheme == "https" ? 443 : 80)) == challenge.protectionSpace.port,
                  challenge.previousFailureCount == 0 else { completionHandler(.cancelAuthenticationChallenge, nil); return }
            completionHandler(.useCredential, URLCredential(user: model.settings.serverUsername, password: model.settings.serverPassword, persistence: .forSession))
        }
    }
}
