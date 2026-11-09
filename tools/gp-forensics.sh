#!/bin/sh
# Collect a forensic bundle from a running GemPrism nested session.
#
# usage: tools/gp-forensics.sh [game-pid]
#
# Collects: app log, sway tree, X11 window screenshots from the nested
# Xwayland display, game thread stacks (gdb), audio streams, process list.
# Writes /tmp/gp-forensics-<timestamp>.tar.gz and prints its path.
set -u

TS=$(date +%Y%m%d-%H%M%S)
OUT=/tmp/gp-forensics-$TS
mkdir -p "$OUT"

APP=$(pgrep -f "net10.0/GemPrismUpscaler --nested" | head -1)
GAME=${1:-$(pgrep -f "KhimeDL_CHS|bgi\.exe|Endfield" | head -1)}
D=$(ls -dt /tmp/gemprism-nested-* 2>/dev/null | head -1)
SWAYSOCK=$(ls "$D"/sway-ipc.*.sock 2>/dev/null | head -1)
DISP=$(cat "$D/display.txt" 2>/dev/null)

{
    echo "time=$TS"
    echo "app_pid=$APP"
    echo "game_pid=$GAME"
    echo "runtime_dir=$D"
    echo "sway_socket=$SWAYSOCK"
    echo "xwayland_display=$DISP"
    echo "--- processes"
    ps -eo pid,ppid,stat,%cpu,%mem,etime,cmd |
        grep -E "GemPrismUpscaler|KhimeDL|bgi\.exe|Endfield|wine|Xwayland" | grep -v grep
    echo "--- app log (tail 400)"
    tail -n 400 "$HOME/.local/share/GemPrismUpscaler/app.log" 2>/dev/null
} > "$OUT/meta.txt" 2>&1

if [ -n "$SWAYSOCK" ] && [ -S "$SWAYSOCK" ]; then
    swaymsg -s "$SWAYSOCK" -t get_tree > "$OUT/tree.json" 2> "$OUT/tree.err"
    if [ -n "$DISP" ]; then
        python3 - "$OUT/tree.json" "$OUT" "$DISP" <<'PY'
import json, os, subprocess, sys
tree, out, disp = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    data = json.load(open(tree))
except Exception:
    sys.exit(0)
ids = []
def walk(n):
    if n.get("window"):
        ids.append((n["window"], n.get("name") or ""))
    for c in n.get("nodes", []) + n.get("floating_nodes", []):
        walk(c)
walk(data)
for i, (xid, name) in enumerate(ids):
    safe = "".join(ch if ch.isalnum() or ch in "-_" else "_" for ch in name)[:40]
    subprocess.run(["import", "-display", disp, "-window", str(xid),
                    os.path.join(out, f"win-{i}-{safe}.png")],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
PY
    fi
fi

if [ -n "$GAME" ]; then
    timeout 40 gdb -p "$GAME" -batch \
        -ex "set pagination off" \
        -ex "thread apply all bt 8" > "$OUT/game-threads.txt" 2>&1
fi

pactl list sink-inputs > "$OUT/audio.txt" 2>&1
ps -eLo pid,tid,stat,pcpu,wchan:24,comm > "$OUT/threads.txt" 2>&1

tar -C /tmp -czf "/tmp/gp-forensics-$TS.tar.gz" "gp-forensics-$TS" 2>/dev/null
echo "/tmp/gp-forensics-$TS.tar.gz"