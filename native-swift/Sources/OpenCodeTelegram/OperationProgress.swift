import SwiftUI
import Foundation

enum EngineUpdatePhase: Sendable {
    case checking, scanning, downloading, verifying, unpacking, validating, installing, uninstalling, connecting

    func title(english: Bool) -> String {
        switch self {
        case .checking: return english ? "Checking the latest version…" : "正在检查最新版本…"
        case .scanning: return english ? "Scanning local installations…" : "正在扫描本机安装…"
        case .downloading: return english ? "Downloading OpenCode…" : "正在下载 OpenCode…"
        case .verifying: return english ? "Verifying download integrity…" : "正在校验下载文件…"
        case .unpacking: return english ? "Extracting OpenCode…" : "正在解压 OpenCode…"
        case .validating: return english ? "Validating the executable…" : "正在验证可执行文件…"
        case .installing: return english ? "Installing OpenCode…" : "正在安装 OpenCode…"
        case .uninstalling: return english ? "Removing the selected installation…" : "正在移除所选安装…"
        case .connecting: return english ? "Synchronizing configuration and connecting…" : "正在同步配置并连接服务…"
        }
    }
}

struct EngineUpdateProgress: Sendable {
    let phase: EngineUpdatePhase
    var received: Int64 = 0
    var expected: Int64 = 0

    var fraction: Double? {
        guard phase == .downloading, expected > 0 else { return nil }
        return min(1, max(0, Double(received) / Double(expected)))
    }
    var detail: String {
        guard phase == .downloading else { return "" }
        let downloaded = ByteCountFormatter.string(fromByteCount: received, countStyle: .file)
        guard expected > 0 else { return downloaded }
        return downloaded + " / " + ByteCountFormatter.string(fromByteCount: expected, countStyle: .file)
    }
}

final class EngineDownloadProgress: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let report: @MainActor @Sendable (EngineUpdateProgress) -> Void
    private var continuation: CheckedContinuation<(URL, URLResponse), Error>?
    private var download: URL?
    private var fileError: Error?
    private var lastPercent = -1
    private var lastBytes: Int64 = 0
    init(report: @escaping @MainActor @Sendable (EngineUpdateProgress) -> Void) { self.report = report }

    func start(_ request: URLRequest) async throws -> (URL, URLResponse) {
        try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            let session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)
            session.downloadTask(with: request).resume()
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        if totalBytesExpectedToWrite > 0 {
            let percent = Int(Double(totalBytesWritten) * 100 / Double(totalBytesExpectedToWrite))
            guard percent != lastPercent else { return }
            lastPercent = percent
        } else {
            guard lastBytes == 0 || totalBytesWritten - lastBytes >= 262144 else { return }
            lastBytes = totalBytesWritten
        }
        let update = EngineUpdateProgress(phase: .downloading, received: totalBytesWritten, expected: totalBytesExpectedToWrite)
        Task { @MainActor in report(update) }
    }
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        do {
            try FileManager.default.createDirectory(at: SettingsStore.home, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let saved = SettingsStore.home.appendingPathComponent("download-\(UUID().uuidString).tmp")
            try FileManager.default.moveItem(at: location, to: saved)
            download = saved
        } catch { fileError = error }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        defer { continuation = nil; session.finishTasksAndInvalidate() }
        if let error = error ?? fileError {
            if let download { try? FileManager.default.removeItem(at: download) }
            continuation?.resume(throwing: error)
        } else if let download, let response = task.response {
            continuation?.resume(returning: (download, response))
        } else {
            if let download { try? FileManager.default.removeItem(at: download) }
            continuation?.resume(throwing: AppFailure.message("OpenCode download did not complete."))
        }
    }
}

struct OperationProgressDialog: View {
    let title: String
    let message: String
    let fraction: Double?
    var detail = ""
    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "arrow.down.circle.fill").font(.system(size: 44)).foregroundStyle(Color.blue)
            Text(title).font(.system(size: 21, weight: .semibold))
            Text(message).multilineTextAlignment(.center)
            if let fraction {
                ProgressView(value: fraction).progressViewStyle(.linear)
                Text("\(Int(fraction * 100))%").monospacedDigit()
            } else {
                ProgressView().progressViewStyle(.linear)
            }
            if !detail.isEmpty { Text(detail).monospacedDigit().foregroundStyle(.secondary) }
        }.padding(32).frame(width: 400).interactiveDismissDisabled()
    }
}
