#!/usr/bin/env bash
#
# Build, sign, install, and publish OpenClicky for macOS.
#
#   scripts/release.sh                 # build + sign + install to /Applications (no GitHub release)
#   scripts/release.sh --publish       # ...and tag + create a GitHub release with the zip and dmg
#   scripts/release.sh --version 0.3.0 # override the version (default: VERSION file)
#   scripts/release.sh --dev           # Apple Development signing, no notarization (ignores release.env)
#   scripts/release.sh --no-notarize   # Developer ID signing, no notarization — a fast local install
#                                      # that keeps the installed app's signature (and its TCC grants)
#
# Signing (in priority order):
#   OPENCLICKY_SIGN_IDENTITY  e.g. "Developer ID Application: Your Org (TEAMID)" — distribution builds
#   default                   "Apple Development" with OPENCLICKY_TEAM_ID (runs on this Mac; other Macs
#                             must right-click → Open, since it is not notarized)
# Notarization (Developer ID only): set OPENCLICKY_NOTARY_PROFILE to a `xcrun notarytool store-credentials`
# profile name and the dmg/zip are notarized and stapled.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Signing identity + notarization profile for distribution builds live in scripts/release.env
# (git-ignored; see release.env.example). `--dev` ignores it: Apple Development signing, no
# notarization, for a quick local install.
if [[ -f "$SCRIPT_DIR/release.env" && " $* " != *" --dev "* ]]; then
  set -a
  # shellcheck disable=SC1091
  source "$SCRIPT_DIR/release.env"
  set +a
fi
APP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_DIR="$(cd "$APP_DIR/../.." && pwd)"
SCHEME="OpenClicky"
APP_NAME="OpenClicky"
BUILD_DIR="$APP_DIR/build"
DERIVED_DATA="$BUILD_DIR/DerivedData"
EXPORT_DIR="$BUILD_DIR/export"
INSTALL_DIR="${OPENCLICKY_INSTALL_DIR:-/Applications}"
TEAM_ID="${OPENCLICKY_TEAM_ID:-3U4384584Z}"
SIGN_IDENTITY="${OPENCLICKY_SIGN_IDENTITY:-Apple Development}"
NOTARY_PROFILE="${OPENCLICKY_NOTARY_PROFILE:-}"
GITHUB_REPO="${OPENCLICKY_GITHUB_REPO:-prasanthsasikumar/openclicky}"

PUBLISH=0
VERSION="$(tr -d '[:space:]' < "$APP_DIR/VERSION")"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --publish) PUBLISH=1 ;;
    --version) VERSION="$2"; shift ;;
    --no-install) INSTALL_DIR="" ;;
    --dev) SIGN_IDENTITY="Apple Development"; NOTARY_PROFILE="" ;;
    # Notarization only matters for other Macs (Gatekeeper). Skipping it keeps the Developer ID
    # signature, so the installed app's Accessibility/Screen Recording grants stay valid — the whole
    # reason to prefer this over --dev when reinstalling over an existing copy.
    --no-notarize) NOTARY_PROFILE="" ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
BUILD_NUMBER="$(git -C "$REPO_DIR" rev-list --count HEAD)"
COMMIT="$(git -C "$REPO_DIR" rev-parse --short HEAD)"
TAG="v$VERSION"

# Release notes come from CHANGELOG.md (the "## $VERSION" section): they are what the Sparkle update
# dialog shows and what the GitHub release says. A publish without them is refused here, before the
# build, so nobody waits on notarization to learn that; a local build only warns and falls back to
# the build line. (v0.9.3 shipped with "Build 477" as its whole update prompt, which read as an
# empty update to the people who got it.)
NOTES_SCRIPT="$REPO_DIR/scripts/release-notes.mjs"
if node "$NOTES_SCRIPT" check "$VERSION"; then
  HAVE_NOTES=1
else
  HAVE_NOTES=0
  if [[ $PUBLISH -eq 1 ]]; then
    echo "refusing to publish $TAG without release notes" >&2
    exit 1
  fi
  echo "▸ no CHANGELOG.md section for $VERSION; the update notes will only name the build (fine for a local install)"
fi

# Notarization needs a secure timestamp on every binary. Apple's timestamp server is flaky enough
# that asking Xcode to timestamp each of its many signing steps (resource bundles included) fails
# builds at random, so Xcode signs without one and every piece of code is re-signed below with a
# timestamp, retrying on a timestamp-server hiccup.
CODE_SIGN_FLAGS=""
# Developer ID needs manual signing: Xcode rejects an explicit Developer ID identity under automatic
# signing ("conflicting provisioning settings"). Development builds stay automatic.
CODE_SIGN_STYLE="Automatic"
# Xcode injects com.apple.security.get-task-allow (debugging) into non-archive builds; notarization rejects it.
INJECT_BASE_ENTITLEMENTS="YES"
if [[ "$SIGN_IDENTITY" == Developer\ ID* ]]; then
  CODE_SIGN_STYLE="Manual"
  INJECT_BASE_ENTITLEMENTS="NO"
  # Xcode adds a secure timestamp to every Developer ID signature on its own, and a timestamp-server
  # hiccup then fails the build on a resource bundle (seen on PostHog's, twice in four runs). Told
  # not to, Xcode signs without one; the re-signing below adds timestamps with retries.
  CODE_SIGN_FLAGS="--timestamp=none"
fi

# A secure timestamp is what notarization checks; a local install does not need one, and Apple's
# timestamp server fails often enough that an unattended `--no-notarize` build should not depend on it.
sign_with_timestamp() {
  local attempt
  if [[ -z "$NOTARY_PROFILE" ]]; then
    codesign --force --options runtime --timestamp=none "$@"
    return $?
  fi
  for attempt in 1 2 3 4; do
    if codesign --force --options runtime --timestamp "$@"; then return 0; fi
    echo "  codesign (timestamp) failed, attempt $attempt; retrying in 5 s" >&2
    sleep 5
  done
  return 1
}
echo "▸ OpenClicky $VERSION (build $BUILD_NUMBER, $COMMIT) — signing as '$SIGN_IDENTITY' team $TEAM_ID"
rm -rf "$EXPORT_DIR"
mkdir -p "$EXPORT_DIR"

# 1. Build a Release app, signed. Signing here (not in an archive/export step) keeps the script
#    dependency-free; hardened runtime is on so a Developer ID build can be notarized as-is.
xcodebuild \
  -project "$APP_DIR/OpenClicky.xcodeproj" \
  -scheme "$SCHEME" \
  -configuration Release \
  -destination 'generic/platform=macOS' \
  -derivedDataPath "$DERIVED_DATA" \
  -allowProvisioningUpdates \
  build \
  ARCHS="arm64 x86_64" \
  ONLY_ACTIVE_ARCH=NO \
  MARKETING_VERSION="$VERSION" \
  CURRENT_PROJECT_VERSION="$BUILD_NUMBER" \
  DEVELOPMENT_TEAM="$TEAM_ID" \
  CODE_SIGN_STYLE="$CODE_SIGN_STYLE" \
  CODE_SIGN_IDENTITY="$SIGN_IDENTITY" \
  PROVISIONING_PROFILE_SPECIFIER="" \
  ENABLE_HARDENED_RUNTIME=YES \
  OTHER_CODE_SIGN_FLAGS="$CODE_SIGN_FLAGS" \
  CODE_SIGN_INJECT_BASE_ENTITLEMENTS="$INJECT_BASE_ENTITLEMENTS" \
  -quiet
APP_PATH="$DERIVED_DATA/Build/Products/Release/$APP_NAME.app"
[[ -d "$APP_PATH" ]] || { echo "build product missing: $APP_PATH" >&2; exit 1; }

# Release builds are universal so Intel Macs can run the app. Fail before signing if the app is not;
# a nested helper without an Intel slice is only a warning (the app still launches, Sparkle updates break).
APP_ARCHS="$(lipo -archs "$APP_PATH/Contents/MacOS/$APP_NAME")"
if [[ " $APP_ARCHS " != *" arm64 "* || " $APP_ARCHS " != *" x86_64 "* ]]; then
  echo "build is not universal: $APP_ARCHS" >&2
  exit 1
fi
while IFS= read -r macho; do
  if [[ " $(lipo -archs "$macho" 2>/dev/null) " != *" x86_64 "* ]]; then
    echo "warning: no x86_64 slice in ${macho#"$APP_PATH"/}" >&2
  fi
done < <(find "$APP_PATH/Contents/Frameworks" -type f -perm -u+x -exec sh -c 'file "$1" | grep -q Mach-O && echo "$1"' _ {} \;)

# Notarization checks every nested binary. Xcode leaves Sparkle's helpers (Updater.app, Autoupdate,
# the XPC services) with Sparkle's own signature, which Apple rejects as "not signed with a valid
# Developer ID certificate", so they are re-signed inside-out with our identity, then the framework
# and Kokoro's embedded engine (signed by the build phase without a timestamp),
# then the app itself (re-signing nested code invalidates the outer signature).
if [[ "$SIGN_IDENTITY" == Developer\ ID* ]]; then
  echo "▸ re-signing nested components with the Developer ID identity"
  SPARKLE="$APP_PATH/Contents/Frameworks/Sparkle.framework"
  for nested in \
    "$SPARKLE/Versions/B/XPCServices/Installer.xpc" \
    "$SPARKLE/Versions/B/XPCServices/Downloader.xpc" \
    "$SPARKLE/Versions/B/Autoupdate" \
    "$SPARKLE/Versions/B/Updater.app" \
    "$SPARKLE" \
    "$APP_PATH/Contents/Frameworks/libsherpa-onnx-c-api.dylib" \
    "$APP_PATH/Contents/Frameworks/libonnxruntime.dylib"; do
    [[ -e "$nested" ]] && sign_with_timestamp --sign "$SIGN_IDENTITY" "$nested"
  done
  sign_with_timestamp --entitlements "$APP_DIR/OpenClicky/OpenClicky.entitlements" --sign "$SIGN_IDENTITY" "$APP_PATH"
fi

echo "▸ verifying signature"
codesign --verify --deep --strict --verbose=2 "$APP_PATH"
codesign -dvv "$APP_PATH" 2>&1 | grep -E '^(Authority|TeamIdentifier|Identifier)=' | sed 's/^/    /'

# 2. Package: zip (Sparkle/GitHub friendly) and a dmg.
ZIP_PATH="$EXPORT_DIR/$APP_NAME-$VERSION.zip"
DMG_PATH="$EXPORT_DIR/$APP_NAME-$VERSION.dmg"
ditto -c -k --sequesterRsrc --keepParent "$APP_PATH" "$ZIP_PATH"
DMG_STAGING="$EXPORT_DIR/dmg"
mkdir -p "$DMG_STAGING"
cp -R "$APP_PATH" "$DMG_STAGING/"
ln -s /Applications "$DMG_STAGING/Applications"
# LZMA (ULMO, macOS 10.15+): with Kokoro's engine inside, 24.6 MB against 35.6 MB as UDZO.
hdiutil create -quiet -volname "$APP_NAME $VERSION" -srcfolder "$DMG_STAGING" -ov -format ULMO "$DMG_PATH"
rm -rf "$DMG_STAGING"

# 3. Notarize (Developer ID builds only).
if [[ -n "$NOTARY_PROFILE" ]]; then
  echo "▸ notarizing"
  xcrun notarytool submit "$ZIP_PATH" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP_PATH"
  ditto -c -k --sequesterRsrc --keepParent "$APP_PATH" "$ZIP_PATH"
  xcrun notarytool submit "$DMG_PATH" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$DMG_PATH"
fi
# 3b. Sparkle: sign the zip with the EdDSA key in the login keychain (`generate_keys`, whose public
#     half is SUPublicEDKey in Info.plist) and write the appcast the app checks daily
#     (SUFeedURL points at the latest release's appcast.xml). Skipped when the tools or key are
#     missing, so a build without them is still a build.
SPARKLE_BIN="$DERIVED_DATA/SourcePackages/artifacts/sparkle/Sparkle/bin"
APPCAST_PATH="$EXPORT_DIR/appcast.xml"
# The private key as a file (OPENCLICKY_SPARKLE_KEY_FILE, default scripts/sparkle-ed25519.key, made
# with `generate_keys -x`): generate_appcast reading it from the keychain asks for permission in a
# dialog, which hangs an unattended build.
SPARKLE_KEY_FILE="${OPENCLICKY_SPARKLE_KEY_FILE:-$SCRIPT_DIR/sparkle-ed25519.key}"
SPARKLE_KEY_ARGS=()
[[ -f "$SPARKLE_KEY_FILE" ]] && SPARKLE_KEY_ARGS=(--ed-key-file "$SPARKLE_KEY_FILE")
if [[ -x "$SPARKLE_BIN/generate_appcast" ]] && { [[ -f "$SPARKLE_KEY_FILE" ]] || "$SPARKLE_BIN/generate_keys" -p >/dev/null 2>&1; }; then
  echo "▸ writing the Sparkle appcast"
  APPCAST_DIR="$EXPORT_DIR/appcast"
  rm -rf "$APPCAST_DIR" && mkdir -p "$APPCAST_DIR"
  cp "$ZIP_PATH" "$APPCAST_DIR/"
  # generate_appcast embeds <name>.html beside the zip as the item's description, which the app
  # renders in the update dialog.
  RELEASE_NOTES="$APPCAST_DIR/$APP_NAME-$VERSION.html"
  if [[ $HAVE_NOTES -eq 1 ]]; then
    node "$NOTES_SCRIPT" html "$VERSION" --platform mac > "$RELEASE_NOTES"
  else
    printf '<html><body><h2>OpenClicky %s</h2><p>Build %s (%s).</p></body></html>\n' "$VERSION" "$BUILD_NUMBER" "$COMMIT" > "$RELEASE_NOTES"
  fi
  "$SPARKLE_BIN/generate_appcast" ${SPARKLE_KEY_ARGS[@]+"${SPARKLE_KEY_ARGS[@]}"} \
    --download-url-prefix "https://github.com/$GITHUB_REPO/releases/download/$TAG/" \
    --embed-release-notes \
    "$APPCAST_DIR" >/dev/null
  mv "$APPCAST_DIR/appcast.xml" "$APPCAST_PATH"
  rm -rf "$APPCAST_DIR"
else
  echo "▸ Sparkle appcast skipped (no generate_appcast or no signing key in the keychain)"
  APPCAST_PATH=""
fi
echo "▸ artifacts:"
ls -la "$ZIP_PATH" "$DMG_PATH" ${APPCAST_PATH:+"$APPCAST_PATH"} | sed 's/^/    /'

# 4. Install locally so Spotlight can launch it.
if [[ -n "$INSTALL_DIR" ]]; then
  echo "▸ installing to $INSTALL_DIR/$APP_NAME.app"
  pkill -x "$APP_NAME" 2>/dev/null || true
  sleep 0.5
  rm -rf "$INSTALL_DIR/$APP_NAME.app"
  ditto "$APP_PATH" "$INSTALL_DIR/$APP_NAME.app"
  xattr -dr com.apple.quarantine "$INSTALL_DIR/$APP_NAME.app" 2>/dev/null || true
  # Build products elsewhere show up in Spotlight as extra "OpenClicky" entries; keep only this one.
  echo "▸ removing stray build copies"
  bash "$SCRIPT_DIR/clean-stray-builds.sh"
fi

# 5. GitHub release.
if [[ $PUBLISH -eq 1 ]]; then
  echo "▸ publishing $TAG to $GITHUB_REPO"
  if ! git -C "$REPO_DIR" rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
    git -C "$REPO_DIR" tag -a "$TAG" -m "OpenClicky $VERSION"
    git -C "$REPO_DIR" push -q origin "$TAG"
  fi
  # The page lists what's new by platform and a downloads table. Only the Mac is attached here; the
  # windows-release and linux-release workflows rebuild the page when they attach theirs.
  NOTES_FILE="$EXPORT_DIR/notes.md"
  if [[ "$SIGN_IDENTITY" == Apple\ Development* ]]; then
    MAC_SIGNING="development"
  elif [[ -n "$NOTARY_PROFILE" ]]; then
    MAC_SIGNING="notarized"
  else
    MAC_SIGNING="developer-id"
  fi
  node "$NOTES_SCRIPT" github "$VERSION" --platforms mac --mac-signing "$MAC_SIGNING" --repo "$GITHUB_REPO" > "$NOTES_FILE"
  RELEASE_ASSETS=("$ZIP_PATH" "$DMG_PATH")
  [[ -n "$APPCAST_PATH" ]] && RELEASE_ASSETS+=("$APPCAST_PATH")
  if gh release view "$TAG" --repo "$GITHUB_REPO" >/dev/null 2>&1; then
    gh release upload "$TAG" "${RELEASE_ASSETS[@]}" --repo "$GITHUB_REPO" --clobber
  else
    gh release create "$TAG" "${RELEASE_ASSETS[@]}" --repo "$GITHUB_REPO" --title "OpenClicky $VERSION" --notes-file "$NOTES_FILE"
  fi
  gh release view "$TAG" --repo "$GITHUB_REPO" --json url --jq .url
fi
echo "▸ done"
