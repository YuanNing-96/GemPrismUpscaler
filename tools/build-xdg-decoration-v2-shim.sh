#!/bin/sh
# Build the bundled xdg-decoration-v2 shim used by the nested sway session.
# Usage: tools/build-xdg-decoration-v2-shim.sh [output.so]
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$here/.." && pwd)
out=${1:-$root/.agent/m2b-runtime/xdg-decoration-v2-shim.so}
cc=${CC:-cc}

cflags=
if command -v pkg-config >/dev/null 2>&1; then
    cflags=$(pkg-config --cflags wayland-server 2>/dev/null || true)
fi

mkdir -p "$(dirname -- "$out")"
# shellcheck disable=SC2086
$cc -shared -fPIC -O2 -Wall -Wextra $cflags \
    -o "$out" "$here/xdg-decoration-v2-shim.c" -ldl

echo "built $out"