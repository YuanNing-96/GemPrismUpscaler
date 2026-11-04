#!/usr/bin/env bash
# Run one M1b benchmark scenario (or the full matrix) and keep the app log +
# metrics CSV under .agent/logs/m1b/.
#
# Examples:
#   tools/bench-m1b.sh --effect anime4k-s --pattern dirty --seconds 10
#   tools/bench-m1b.sh --effect anime4k-m --pattern animate --seconds 10 \
#       --monitor HDMI-A-1
#   tools/bench-m1b.sh --effect anime4k-l --pattern animate --seconds 10 \
#       --output-size 3840x2160          # 4K without a 4K monitor
#   tools/bench-m1b.sh --all --seconds 6
#
# Patterns: static, dirty, animate, repaint, popup.
# Effects:  anime4k-s, anime4k-m, anime4k-l, bilinear.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler"
PATTERN_SRC="$ROOT/tools/testpattern.c"
PATTERN_BIN="$ROOT/tools/testpattern"
OUTDIR="${BENCH_OUTDIR:-$ROOT/.agent/logs/m1b}"

EFFECT="anime4k-s"
PATTERN="dirty"
SECONDS_TO_RUN=10
MONITOR=""
OUTPUT_SIZE=""
SCREENSHOT_DIR=""
ALL=0
EXTRA_ARGS=()

usage() {
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --effect) EFFECT="$2"; shift 2 ;;
        --pattern) PATTERN="$2"; shift 2 ;;
        --seconds) SECONDS_TO_RUN="$2"; shift 2 ;;
        --monitor) MONITOR="$2"; shift 2 ;;
        --output-size) OUTPUT_SIZE="$2"; shift 2 ;;
        --screenshot) SCREENSHOT_DIR="$2"; shift 2 ;;
        --no-dirty-rect) EXTRA_ARGS+=(--no-dirty-rect); shift ;;
        --stretch) EXTRA_ARGS+=(--stretch); shift ;;
        --all) ALL=1; shift ;;
        -h|--help) usage 0 ;;
        *) echo "unknown option: $1" >&2; usage 2 ;;
    esac
done

case "$PATTERN" in
    static|dirty|animate|repaint|popup) ;;
    *) echo "invalid --pattern '$PATTERN'" >&2; exit 2 ;;
esac
case "$EFFECT" in
    anime4k|anime4k-s|anime4k-m|anime4k-l|bilinear) ;;
    *) echo "invalid --effect '$EFFECT'" >&2; exit 2 ;;
esac
if ! [[ "$SECONDS_TO_RUN" =~ ^[0-9]+$ ]] || (( SECONDS_TO_RUN < 3 )); then
    echo "--seconds must be an integer >= 3" >&2; exit 2
fi

if [[ -z "${DISPLAY:-}" ]]; then
    echo "DISPLAY is not set; run this from the graphical session" >&2; exit 1
fi
if [[ ! -x "$APP" ]]; then
    echo "missing $APP" >&2
    echo "build first: dotnet build $ROOT/GemPrismUpscaler.slnx -c Release" >&2
    exit 1
fi
if ! command -v cc >/dev/null 2>&1; then
    echo "cc is required to build tools/testpattern" >&2; exit 1
fi
if [[ ! -x "$PATTERN_BIN" || "$PATTERN_SRC" -nt "$PATTERN_BIN" ]]; then
    cc -O2 -o "$PATTERN_BIN" "$PATTERN_SRC" -lX11
fi

mkdir -p "$OUTDIR"
if [[ -n "$SCREENSHOT_DIR" ]]; then mkdir -p "$SCREENSHOT_DIR"; fi

CURRENT_PATTERN_PID=""
CURRENT_APP_PID=""
cleanup() {
    if [[ -n "$CURRENT_APP_PID" ]]; then kill -TERM "$CURRENT_APP_PID" 2>/dev/null || true; fi
    if [[ -n "$CURRENT_PATTERN_PID" ]]; then kill -TERM "$CURRENT_PATTERN_PID" 2>/dev/null || true; fi
}
trap cleanup EXIT INT TERM

if "$APP" --status >/dev/null 2>&1; then
    echo "another GemPrismUpscaler instance is running; stop it first" >&2
    exit 1
fi

run_one() {
    local effect="$1" pattern="$2" tag="$3"
    local stamp; stamp="$(date +%Y%m%d-%H%M%S)"
    local base="$OUTDIR/${stamp}-${tag}"
    local logfile="${base}.log" csvfile="${base}.csv"
    local winfile; winfile="$(mktemp)"
    local patlog="${base}.pattern.log"

    echo "== $tag: effect=$effect pattern=$pattern seconds=$SECONDS_TO_RUN"
    "$PATTERN_BIN" "--$pattern" --win-id-file "$winfile" >"$patlog" 2>&1 &
    CURRENT_PATTERN_PID=$!
    for _ in $(seq 1 50); do
        [[ -s "$winfile" ]] && break
        sleep 0.1
    done
    if [[ ! -s "$winfile" ]]; then
        echo "testpattern did not publish a window id; see $patlog" >&2
        return 1
    fi
    local win; win="$(cat "$winfile")"

    local args=(--window "$win" --effect "$effect" --no-follow-focus)
    [[ -n "$MONITOR" ]] && args+=(--monitor "$MONITOR")
    [[ -n "$OUTPUT_SIZE" ]] && args+=(--output-size "$OUTPUT_SIZE")
    args+=("${EXTRA_ARGS[@]}")

    GEMPRISM_LOG="$logfile" GEMPRISM_STATS="$csvfile" "$APP" "${args[@]}" >"${base}.out" 2>&1 &
    CURRENT_APP_PID=$!

    local shot=""
    if [[ -n "$SCREENSHOT_DIR" ]]; then
        sleep $(( SECONDS_TO_RUN > 3 ? SECONDS_TO_RUN - 2 : 1 ))
        shot="$SCREENSHOT_DIR/${tag}.png"
        if command -v spectacle >/dev/null 2>&1; then
            timeout 10 spectacle -b -n -o "$shot" >/dev/null 2>&1 || true
        else
            echo "spectacle not found; screenshot skipped" >&2
        fi
        sleep 1
    else
        sleep "$SECONDS_TO_RUN"
    fi

    kill -TERM "$CURRENT_APP_PID" 2>/dev/null || true
    wait "$CURRENT_APP_PID" 2>/dev/null || true
    CURRENT_APP_PID=""
    kill -TERM "$CURRENT_PATTERN_PID" 2>/dev/null || true
    wait "$CURRENT_PATTERN_PID" 2>/dev/null || true
    CURRENT_PATTERN_PID=""
    rm -f "$winfile"

    echo "   log: $logfile"
    echo "   csv: $csvfile"
    [[ -n "$shot" && -f "$shot" ]] && echo "   png: $shot"
    if [[ -f "$logfile" ]]; then
        grep 'stats ' "$logfile" | tail -n 2 | sed 's/^/   /' || true
    fi
    if [[ -f "$logfile" && ! -s "$logfile" ]]; then
        echo "   (no app log; see ${base}.out)" >&2
    fi
}

if (( ALL )); then
    for pattern in static dirty animate repaint; do
        for effect in anime4k-s anime4k-m anime4k-l bilinear; do
            run_one "$effect" "$pattern" "${pattern}-${effect}"
        done
    done
else
    tag="${PATTERN}-${EFFECT}"
    [[ -n "$OUTPUT_SIZE" ]] && tag+="-$(echo "$OUTPUT_SIZE" | tr 'x' 'x')"
    [[ " ${EXTRA_ARGS[*]:-} " == *" --no-dirty-rect "* ]] && tag+="-nodirty"
    run_one "$EFFECT" "$PATTERN" "$tag"
fi
