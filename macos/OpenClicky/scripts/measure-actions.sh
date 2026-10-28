#!/usr/bin/env bash
# Reports how long the fast lane actually takes, from the app's own log — plus, as a trailing
# "agent" row, how long the agent lane's runs take, since the plan's decision on a one-shot agent
# mode (deferred) depends on how common and how slow those still are once the fast lane exists.
#
#   scripts/measure-actions.sh          # summarise every mac action (+ the local rung, + the agent lane) in the log
#   scripts/measure-actions.sh open_app # one verb (or "agent" for just the agent lane)
#   scripts/measure-actions.sh local:open_app   # the local ask rung's own rows: local:<verb>, local:rewrite,
#                                               # local:cloud (every reason the rung handed over, collapsed)
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

# The local ask rung (Apple's on-device model before the cloud): "local rung: open_app in 0.71 s",
# "local rung: rewrite 230→157 chars in 2.75 s", "local rung: cloud (timed out) in 1.50 s". Every
# cloud reason collapses into one local:cloud row: what matters is how often the rung handed over,
# and how long the person waited for it to.
local_samples="$(grep -E "local rung: .* in [0-9.]+ s" "$LOG" || true)"
local_samples="$(sed -E 's/.*local rung: (cloud|rewrite|[a-z_]+).* in ([0-9.]+) s.*/local:\1 \2/' <<< "$local_samples")"

samples="$(printf '%s\n%s\n%s' "$mac_samples" "$local_samples" "$agent_samples" | sed '/^$/d')"
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
    function print_sorted(arr,    keys, count, i, j, t) {
      count = 0
      for (key in arr) keys[++count] = key
      for (i = 1; i <= count; i++) for (j = i + 1; j <= count; j++)
        if (keys[i] > keys[j]) { t = keys[i]; keys[i] = keys[j]; keys[j] = t }
      for (i = 1; i <= count; i++) print_stats(keys[i], arr[keys[i]])
    }
    {
      if ($1 == "agent") agent = agent " " $2
      else if ($1 ~ /^local:/) localTimes[$1] = localTimes[$1] " " $2
      else times[$1] = times[$1] " " $2
    }
    END {
      printf "%-18s %6s %8s %8s\n", "verb", "runs", "p50", "p95"
      print_sorted(times)
      # The local ask rung after the fast lane verbs: its rows are whole turns (gate, model,
      # mapping), and local:cloud is the wait before the ordinary path took over.
      print_sorted(localTimes)
      # Printed last and on its own — the agent lane, not a seventh verb of the fast lane.
      if (agent != "") print_stats("agent", agent)
    }' <<< "$samples"
