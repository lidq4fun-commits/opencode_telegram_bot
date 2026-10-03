import SwiftUI
import WebKit

struct EmbeddedWeb: NSViewRepresentable {
    @ObservedObject var model: AppModel
    func makeCoordinator() -> Coordinator { Coordinator(model) }
    func makeNSView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        let darkScript = """
        (() => {
          const apply = () => {
            document.documentElement.classList.add('dark');
            document.documentElement.style.colorScheme = 'dark';
            if (!document.getElementById('desktop-dark-background')) {
              const style = document.createElement('style');
              style.id = 'desktop-dark-background';
              style.textContent = 'html, body { background: #000 !important; color-scheme: dark !important; }';
              document.documentElement.appendChild(style);
            }
          };
          apply();
          document.addEventListener('DOMContentLoaded', apply);
        })();
        """
        config.userContentController.addUserScript(WKUserScript(source: darkScript, injectionTime: .atDocumentEnd, forMainFrameOnly: true))
        let scriptURL = Bundle.main.resourceURL?.appendingPathComponent("EmbeddedWebSync.js")
        if let scriptURL, let script = try? String(contentsOf: scriptURL) {
            // Preserve the Windows script's metadata-only contract.
            let shim = "window.chrome=window.chrome||{};window.chrome.webview={postMessage:function(v){window.webkit.messageHandlers.desktopSync.postMessage(v)}};"
            config.userContentController.addUserScript(WKUserScript(source: shim + script, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        }
        config.userContentController.add(context.coordinator, name: "desktopSync")
        let view = WKWebView(frame: .zero, configuration: config)
        view.underPageBackgroundColor = .black
        view.appearance = NSAppearance(named: .darkAqua)
        view.navigationDelegate = context.coordinator
        context.coordinator.view = view
        context.coordinator.timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak coordinator = context.coordinator] _ in coordinator?.refresh() }
        return view
    }
    func updateNSView(_ view: WKWebView, context: Context) {
        view.pageZoom = model.fontSize / 13
        let reload = context.coordinator.reloadRevision != model.webReloadRevision
        context.coordinator.reloadRevision = model.webReloadRevision
        if let target = WebRouting.navigationTarget(configured: model.webURL, loaded: context.coordinator.loadedURL, current: view.url, reload: reload) {
            context.coordinator.loadedURL = model.webURL
            view.load(URLRequest(url: model.authenticatedURL(target)))
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
        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { reportNavigationError(error) }
        func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { reportNavigationError(error) }
        private func reportNavigationError(_ error: Error) {
            guard WebRouting.shouldReportNavigationError(error) else { return }
            model.error = error.localizedDescription
        }
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
