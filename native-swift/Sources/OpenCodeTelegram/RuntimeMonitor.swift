import Foundation

enum RuntimeMonitor {
    static func serviceFile(home: URL) -> URL {
        home.appendingPathComponent("run/bot-service.json")
    }
}

struct SessionLogReader {
    private var offsets: [URL: UInt64] = [:]

    init(folder: URL) {
        for file in Self.files(folder) {
            if let handle = try? FileHandle(forReadingFrom: file) {
                offsets[file] = (try? handle.seekToEnd()) ?? 0
                try? handle.close()
            }
        }
    }

    mutating func readNew(folder: URL) -> String {
        var output = ""
        for file in Self.files(folder) {
            guard let handle = try? FileHandle(forReadingFrom: file) else { continue }
            defer { try? handle.close() }
            let size = (try? handle.seekToEnd()) ?? 0
            let previous = offsets[file] ?? 0
            let start = previous <= size ? previous : 0
            // Bound the foreground buffer without modifying the persistent log.
            let offset = max(start, size > 65536 ? size - 65536 : 0)
            do {
                try handle.seek(toOffset: offset)
                let data = try handle.readToEnd() ?? Data()
                offsets[file] = offset + UInt64(data.count)
                output += String(decoding: data, as: UTF8.self)
            } catch { continue }
        }
        return output
    }

    private static func files(_ folder: URL) -> [URL] {
        ((try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? [])
            .filter { $0.pathExtension == "log" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }
}
