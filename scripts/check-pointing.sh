#!/usr/bin/env bash
#
# Walks through the three checks that matter after a change to how `point_at` resolves an element,
# and reads the verdict out of the app log so you do not have to. Say the line it prints, then let
# it tell you which pass answered and what it picked.
#
# Usage: scripts/check-pointing.sh
#
set -uo pipefail

LOG="$HOME/Library/Logs/OpenClicky/app.log"
BOLD=$'\033[1m'; DIM=$'\033[2m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; OFF=$'\033[0m'

if ! pgrep -x OpenClicky >/dev/null 2>&1; then
    echo "${RED}OpenClicky is not running.${OFF} Build and run it from Xcode first, then re-run this."
    exit 1
fi
[ -f "$LOG" ] || { echo "${RED}No log at $LOG${OFF} (the app writes it on the first Realtime turn)."; exit 1; }

# Turn one raw log line into a plain-English verdict.
explain() {
    local line="$1"
    case "$line" in
        *"accessibility "*)
            picked="${line#*accessibility }"
            echo "  ${GREEN}Accessibility resolved it${OFF} (instant, no Claude call)"
            echo "  ${DIM}picked:${OFF} ${picked%% (*}" ;;
        *"snapped to "*)
            picked="${line#*snapped to }"
            echo "  ${GREEN}OCR snapped it${OFF} (instant)"
            echo "  ${DIM}picked:${OFF} ${picked%% (*}" ;;
        *"browser tab "*)
            echo "  ${GREEN}Browser tab locator resolved it${OFF} (instant)" ;;
        *"located by Claude"*)
            echo "  ${YELLOW}Fell through to Claude${OFF} $(sed -n 's/.*in \([0-9]*\) ms.*/(\1 ms)/p' <<<"$line")"
            echo "  ${DIM}This is correct when nothing on screen could tell the candidates apart.${OFF}" ;;
        *"using the guess"*)
            echo "  ${RED}Nothing resolved it${OFF}, the model's raw guess was used (expect it to be 30-100 px off)" ;;
        *) echo "  ${DIM}unrecognised resolution, raw line below${OFF}" ;;
    esac
    echo "  ${DIM}${line}${OFF}"
}

# Block until a new point_at line lands, or time out.
await_point() {
    local startingLineCount="$1" secondsWaited=0
    while [ "$secondsWaited" -lt 60 ]; do
        local currentLineCount
        currentLineCount=$(grep -c "point_at" "$LOG" 2>/dev/null || echo 0)
        if [ "$currentLineCount" -gt "$startingLineCount" ]; then
            grep "point_at" "$LOG" | tail -n $((currentLineCount - startingLineCount))
            return 0
        fi
        sleep 1
        secondsWaited=$((secondsWaited + 1))
    done
    return 1
}

run_check() {
    local title="$1" say="$2" expectation="$3" setup="${4:-}"
    echo
    echo "${BOLD}$title${OFF}"
    [ -n "$setup" ] && eval "$setup" && sleep 2
    echo "  Hold ${BOLD}control + option${OFF} and say:"
    echo "     ${BOLD}\"$say\"${OFF}"
    echo "  ${DIM}expected: $expectation${OFF}"
    echo "  waiting..."
    local before newLines
    before=$(grep -c "point_at" "$LOG" 2>/dev/null || echo 0)
    if newLines=$(await_point "$before"); then
        while IFS= read -r line; do explain "$line"; done <<<"$newLines"
    else
        echo "  ${RED}nothing logged in 60s${OFF} (was the app listening? did the buddy move?)"
    fi
}

echo "${BOLD}point_at check${OFF}  ${DIM}log: $LOG${OFF}"

run_check "1 of 3: a repeated caption the request identifies" \
    "where do I open the details for Wi-Fi" \
    "Accessibility resolves it and names the Wi-Fi row" \
    "open 'x-apple.systempreferences:com.apple.Network-Settings.extension'"

run_check "2 of 3: the same repeated caption with nothing to go on" \
    "point at the details button" \
    "nothing can separate the rows, so it falls through to Claude"

run_check "3 of 3: a caption that appears once (regression)" \
    "where do I change the Wi-Fi network" \
    "OCR snaps it instantly, no Claude call"

echo
echo "${BOLD}Done.${OFF} Check 1 should say Accessibility, check 2 should say Claude, check 3 should say OCR."
