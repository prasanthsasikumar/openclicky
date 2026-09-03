#!/usr/bin/env bash
# Removes every OpenClicky.app bundle that is NOT the installed copy, so Spotlight only ever
# offers one OpenClicky. Build products in DerivedData (Xcode's default folder, the release
# script's build/ folder, headless builds) are indexed like real apps and show up as
# "OpenClicky — Debug" / "OpenClicky — Release". They are rebuilt on demand, so deleting is safe.
#
#   scripts/clean-stray-builds.sh            # delete
#   scripts/clean-stray-builds.sh --dry-run  # just list
set -euo pipefail

APP_NAME="OpenClicky"
BUNDLE_ID="org.openclicky.app"
INSTALL_DIR="${OPENCLICKY_INSTALL_DIR:-/Applications}"
DRY_RUN=0
[[ "${1:-}" == "--dry-run" ]] && DRY_RUN=1

candidates() {
  # Spotlight's own view (what the user sees in ⌘-space) …
  mdfind "kMDItemCFBundleIdentifier == '$BUNDLE_ID'" 2>/dev/null || true
  mdfind "kMDItemKind == 'Application' && kMDItemDisplayName == '$APP_NAME*'" 2>/dev/null || true
  # … plus the usual build folders in case the index is stale.
  find "$HOME/Library/Developer/Xcode/DerivedData" "$(cd "$(dirname "$0")/.." && pwd)/build" \
    -maxdepth 6 -type d -name "$APP_NAME.app" 2>/dev/null || true
}

found=0
while IFS= read -r app; do
  [[ -z "$app" ]] && continue
  [[ "$app" == "$INSTALL_DIR/$APP_NAME.app" ]] && continue
  [[ "$app" == "$INSTALL_DIR/$APP_NAME.app/"* ]] && continue
  [[ -d "$app" ]] || continue
  found=1
  if [[ $DRY_RUN -eq 1 ]]; then
    echo "  would remove $app"
  else
    echo "  removing $app"
    rm -rf "$app"
  fi
done < <(candidates | sort -u)

[[ $found -eq 0 ]] && echo "  no stray $APP_NAME.app bundles"
exit 0
