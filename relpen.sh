#!/usr/bin/env bash
# relpen on|off|status — switch the tablet between "Relative Pen Mode" (plugin) and
# stock Relative Mode. The plugin turns on KDE's relative mode for its pen by itself.
set -euo pipefail
CFG="$HOME/.config/OpenTabletDriver/settings.json"
BAK="$HOME/.config/OpenTabletDriver/settings.json.bak-relative"   # = stock Relative Mode

case "${1:-status}" in
  on)
    python3 - "$CFG" <<'PY'
import json, sys
p = sys.argv[1]; s = json.load(open(p))
for prof in s["Profiles"]:
    prof["OutputMode"] = {"Path": "RelativePenMode.RelativePenMode", "Settings": [], "Enable": True}
json.dump(s, open(p, "w"), indent=2)
PY
    systemctl --user restart opentabletdriver
    sleep 6
    journalctl --user -u opentabletdriver --since -10s --no-pager | grep -E "Output mode|Relative Pen|Sensitivity|Error" || true ;;
  off)
    cp "$BAK" "$CFG"
    systemctl --user restart opentabletdriver ;;
  status)
    python3 -c "import json;print(json.load(open('$CFG'))['Profiles'][0]['OutputMode']['Path'])" ;;
  *) echo "usage: relpen on|off|status"; exit 1 ;;
esac
