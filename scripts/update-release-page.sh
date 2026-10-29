#!/usr/bin/env bash
# Rebuilds a GitHub release's page from CHANGELOG.md for the platforms whose files are attached to it:
# what's new by platform, then a downloads table. The windows-release and linux-release workflows run
# it after they upload, so a platform's notes and download rows appear when its files do.
#
#   scripts/update-release-page.sh v0.10.0 [--windows-signed]
#
# Run from a full clone of the default branch (its CHANGELOG.md is the newest); the build number and
# commit on the page are the tag's.
set -euo pipefail

TAG="${1:?usage: update-release-page.sh <tag> [--windows-signed]}"
shift
REPO="${GITHUB_REPOSITORY:-prasanthsasikumar/openclicky}"
VERSION="${TAG#v}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

ASSETS="$(gh release view "$TAG" --repo "$REPO" --json assets --jq '.assets[].name')"
PLATFORMS=()
grep -qx "OpenClicky-$VERSION.dmg" <<<"$ASSETS" && PLATFORMS+=(mac)
grep -q '^OpenClicky\.Windows-.*Setup\.exe$' <<<"$ASSETS" && PLATFORMS+=(windows)
grep -q '^openclicky_linux-.*\.whl$' <<<"$ASSETS" && PLATFORMS+=(linux)
if [[ ${#PLATFORMS[@]} -eq 0 ]]; then
  echo "no platform files attached to $TAG yet; leaving its page alone" >&2
  exit 0
fi

# How the Mac build was signed is only known to release.sh, which wrote it on the page.
BODY="$(gh release view "$TAG" --repo "$REPO" --json body --jq .body)"
MAC_SIGNING="notarized"
if grep -q "Signed for development only" <<<"$BODY"; then
  MAC_SIGNING="development"
elif grep -q "Signed with Developer ID, not notarized" <<<"$BODY"; then
  MAC_SIGNING="developer-id"
fi

NOTES_FILE="$(mktemp)"
trap 'rm -f "$NOTES_FILE"' EXIT
node "$SCRIPT_DIR/release-notes.mjs" github "$VERSION" --repo "$REPO" --ref "$TAG" \
  --platforms "$(IFS=,; echo "${PLATFORMS[*]}")" --mac-signing "$MAC_SIGNING" "$@" > "$NOTES_FILE"
gh release edit "$TAG" --repo "$REPO" --notes-file "$NOTES_FILE"
echo "updated the $TAG page for: ${PLATFORMS[*]}"
