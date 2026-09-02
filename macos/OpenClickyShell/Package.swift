// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "OpenClickyShell",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "OpenClickyShell",
            path: "Sources/OpenClickyShell",
            linkerSettings: [.linkedFramework("Carbon")]
        )
    ]
)
