#!/usr/bin/env bash
# Reports how long the fast lane actually takes, from the app's own log — plus, as a trailing
# "agent" row, how long the agent lane's runs take, since the plan's decision on a one-shot agent
# mode (deferred) depends on how common and how slow those still are once the fast lane exists.
#
#   scripts/measure-actions.sh          # summarise every mac action (+ the agent lane) in the log
#   scripts/measure-actions.sh open_app # one verb (or "agent" for just the agent lane)
#
# Reads $HOME/Library/Logs/OpenClicky/app.log by default; set LOG=/path/to/file to point it at a
# different file (a synthetic fixture, an older rotated log, ...).
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
mac_samples="$(grep -E "mac action: [a-z_]+ .* in [0-9.]+ s" "$LOG" || true)"
mac_samples="$(sed -E 's/.*mac action: ([a-z_]+) .* in ([0-9.]+) s.*/\1 \2/' <<< "$mac_samples")"

agent_samples="$(grep -E "agent task finished in [0-9.]+ s" "$LOG" || true)"
agent_samples="$(sed -E 's/.*agent task finished in ([0-9.]+) s.*/agent \1/' <<< "$agent_samples")"

samples="$(printf '%s\n%s' "$mac_samples" "$agent_samples" | sed '/^$/d')"
[[ -n "$VERB" ]] && samples="$(grep "^$VERB " <<< "$samples" || true)"

if [[ -z "$samples" ]]; then
  echo "no timed mac action or agent lines in $LOG yet${VERB:+ for verb \"$VERB\"}"
  exit 0
fi

awk '
    # Nearest-rank percentile: p50 is rank (n+1)/2 (already exact for the median). p95 is
    # ceil(0.95n), clamped to [1, n] — flooring instead would round the rank down and could
    # silently pick a value below the true 95th percentile, hiding exactly the slow outlier
    # this script exists to catch.
    function print_stats(label, str,    values, i, j, t, n, p50idx, p95idx) {
      n = split(str, values, " ")
      if (n == 0) return
      for (i = 1; i <= n; i++) for (j = i + 1; j <= n; j++)
        if (values[i] + 0 > values[j] + 0) { t = values[i]; values[i] = values[j]; values[j] = t }
      p50idx = int((n + 1) / 2)
      p95idx = int(n * 0.95)
      if (p95idx < n * 0.95) p95idx++
      if (p95idx < 1) p95idx = 1
      if (p95idx > n) p95idx = n
      printf "%-18s %6d %7.2fs %7.2fs\n", label, n, values[p50idx], values[p95idx]
    }
    {
      if ($1 == "agent") agent = agent " " $2
      else times[$1] = times[$1] " " $2
    }
    END {
      printf "%-18s %6s %8s %8s\n", "verb", "runs", "p50", "p95"
      verbCount = 0
      for (verb in times) verbs[++verbCount] = verb
      for (i = 1; i <= verbCount; i++) for (j = i + 1; j <= verbCount; j++)
        if (verbs[i] > verbs[j]) { t = verbs[i]; verbs[i] = verbs[j]; verbs[j] = t }
      for (i = 1; i <= verbCount; i++) print_stats(verbs[i], times[verbs[i]])
      # Printed last and on its own — the agent lane, not a seventh verb of the fast lane.
      if (agent != "") print_stats("agent", agent)
    }' <<< "$samples"
