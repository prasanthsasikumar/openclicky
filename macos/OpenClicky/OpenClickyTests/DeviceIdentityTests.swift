import Testing
@testable import OpenClicky

struct DeviceIdentityTests {
    @Test func theHashIsSaltedSHA256InLowercaseHex() {
        // printf 'openclicky-device-v1:ABC' | shasum -a 256
        #expect(DeviceIdentity.hash(platformUUID: "ABC") == "09dfa12193ca778d98a1c1d3b219ba1f2e98a5d1fe755d048e0b1b4f8faff226")
    }
    @Test func thisMacHasAStable64CharacterHash() {
        let first = DeviceIdentity.current
        #expect(first?.count == 64)
        #expect(first?.allSatisfy { "0123456789abcdef".contains($0) } == true)
        #expect(DeviceIdentity.current == first)
    }
}
