// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "OpenCodeTelegram",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "OpenCodeTelegram", targets: ["OpenCodeTelegram"])],
    targets: [
        .executableTarget(name: "OpenCodeTelegram"),
        .testTarget(name: "OpenCodeTelegramTests", dependencies: ["OpenCodeTelegram"])
    ]
)
