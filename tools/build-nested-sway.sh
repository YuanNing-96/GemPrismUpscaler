#!/bin/sh
# Build sway 1.12 with the cursor-hint IPC patch and install it into the nested
# runtime directory.
#
# The patch only exposes the client-requested pointer warp over sway's IPC
# (get_seats: cursor_hint_seq/x/y); the nested host uses it to rebase its
# relative pointer stream. See .doc/pointer-warp.md.
#
# Requirements: git, patch, meson, ninja, wlroots-0.20 and sway build deps.
# meson/ninja can come from a venv:
#   python3 -m venv /tmp/sway-build-venv
#   /tmp/sway-build-venv/bin/pip install meson ninja
#   PATH=/tmp/sway-build-venv/bin:$PATH tools/build-nested-sway.sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
OUT=${1:-"$ROOT/src/GemPrismUpscaler/bin/Release/net10.0/runtime/sway"}
SWAY_SRC=${GEMPRISM_SWAY_SRC:-/tmp/sway-1.12}
SWAY_TAG=1.12

if [ ! -d "$SWAY_SRC/.git" ]; then
    git clone --depth 1 --branch "$SWAY_TAG" https://github.com/swaywm/sway "$SWAY_SRC"
fi

git -C "$SWAY_SRC" checkout -- .
git -C "$SWAY_SRC" apply "$ROOT/tools/patches/sway-1.12-cursor-hint-ipc.patch"

(cd "$SWAY_SRC" && meson setup build -Dman-pages=disabled -Dwerror=false \
    && ninja -C build)

mkdir -p "$OUT"
cp -f "$SWAY_SRC/build/sway/sway" "$OUT/sway"
chmod 755 "$OUT/sway"
echo "installed $OUT/sway"
