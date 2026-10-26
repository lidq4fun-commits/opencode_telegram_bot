import AppKit
import CoreGraphics

// Exercise LaunchServices on a disposable copy, never the installed application.
let source = CommandLine.arguments[1]
let root = FileManager.default.temporaryDirectory.appendingPathComponent("OpenCodeTelegram-launch-\(UUID().uuidString)")
try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
let target = root.appendingPathComponent("OpenCodeTelegram.app")
let copy = Process(); copy.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
copy.arguments = [source, target.path]; try copy.run(); copy.waitUntilExit()
guard copy.terminationStatus == 0 else { fatalError("Copy failed") }
let configuration = NSWorkspace.OpenConfiguration()
configuration.arguments = CommandLine.arguments.contains("--main") ? [] : ["--install"]
configuration.createsNewApplicationInstance = true
var finished = false
var passed = false
NSWorkspace.shared.openApplication(at: target, configuration: configuration) { application, error in
    guard let application, error == nil else {
        print("FAIL: LaunchServices: \(String(describing: error))"); finished = true; return
    }
    DispatchQueue.main.asyncAfter(deadline: .now() + 15) {
        let windows = CGWindowListCopyWindowInfo(.optionAll, kCGNullWindowID) as? [[String: Any]] ?? []
        let visible = windows.filter { ($0[kCGWindowOwnerPID as String] as? Int32) == application.processIdentifier && ($0[kCGWindowLayer as String] as? Int) == 0 }
        passed = !application.isTerminated && !visible.isEmpty
        print("LaunchServices disposable app: \(passed ? "PASS" : "FAIL"), normal windows: \(visible.count), terminated: \(application.isTerminated)")
        if !passed && !application.isTerminated {
            let sample = Process(); sample.executableURL = URL(fileURLWithPath: "/usr/bin/sample")
            let report = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("OpenCodeTelegram-build/native-swift/launch-hang.txt")
            sample.arguments = [String(application.processIdentifier), "2", "-file", report.path]
            try? sample.run(); sample.waitUntilExit()
            print("Hang sample:", report.path)
        }
        application.forceTerminate()
        finished = true
    }
}
let deadline = Date().addingTimeInterval(45)
while !finished && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.1)) }
if finished { try? FileManager.default.removeItem(at: root) }
exit(passed ? 0 : 1)
