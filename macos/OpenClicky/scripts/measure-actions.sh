#!/usr/bin/env bash
# Reports how long the fast lane actually takes, from the app's own log.
#
#   scripts/measure-actions.sh          # summarise every mac action in the log
#   scripts/measure-actions.sh open_app # one verb
#
# Populate the log first by using the app: hold the talk shortcut and say "open Spotify",
# "create a folder called Test on my desktop", and so on. The spec's acceptance number is
# p95 under 2 s for open_app and create_folder.
set -euo pipefail

LOG="${LOG:-$HOME/Library/Logs/OpenClicky/app.log}"
VERB="${1:-}"
[[ -f "$LOG" ]] || { echo "no log at $LOG — run the app first"; exit 1; }

# Only timed lines are latency samples: a rejected argument ("mac action: create_folder rejected — …")
# carries no duration and would otherwise be read as a verb named after the timestamp.
# grep/sed run outside the main pipeline (with `|| true`) so an empty result — no timed lines yet,
# or none for the requested verb — doesn't trip `pipefail` and abort the script.
samples="$(grep -E "mac action: [a-z_]+ .* in [0-9.]+ s" "$LOG" || true)"
samples="$(sed -E 's/.*mac action: ([a-z_]+) .* in ([0-9.]+) s.*/\1 \2/' <<< "$samples")"
[[ -n "$VERB" ]] && samples="$(grep "^$VERB " <<< "$samples" || true)"

if [[ -z "$samples" ]]; then
  echo "no timed mac action lines in $LOG yet${VERB:+ for verb \"$VERB\"}"
  exit 0
fi

awk '
    { times[$1] = times[$1] " " $2 }
    END {
      printf "%-18s %6s %8s %8s\n", "verb", "runs", "p50", "p95"
      for (verb in times) {
        n = split(times[verb], values, " ")
        for (i = 1; i <= n; i++) for (j = i + 1; j <= n; j++)
          if (values[i] + 0 > values[j] + 0) { t = values[i]; values[i] = values[j]; values[j] = t }
        p50 = values[int((n + 1) / 2)]
        p95 = values[int(n * 0.95) < 1 ? 1 : int(n * 0.95)]
        printf "%-18s %6d %7.2fs %7.2fs\n", verb, n, p50, p95
      }
    }' <<< "$samples"
