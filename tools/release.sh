#!/usr/bin/env bash
# Build the v1.0 release tree and archives.
#
# Usage:
#   tools/release.sh                 # version from Directory.Build.props
#   VERSION=1.0.0 tools/release.sh
#
# Runtime overrides (normally auto-detected):
#   GEMPRISM_SWAY_BIN      patched sway (must contain cursor_hint_seq)
#   GEMPRISM_SWAYMSG_BIN   matching swaymsg
#   GEMPRISM_WLROOTS_LIB   libwlroots-0.20.so
#   GEMPRISM_VKEYBOARD_BIN gemprism-vkeyboard
#   GEMPRISM_SHIM_BIN      xdg-decoration-v2-shim.so
#
# Output: artifacts/GemPrismUpscaler-v<version>-linux-x64/
#         artifacts/GemPrismUpscaler-v<version>-linux-x64.tar.gz
#         artifacts/GemPrismUpscaler-v<version>-linux-x64.zip
set -euo pipefail

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
VERSION=${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)}
[ -n "$VERSION" ] || { echo "cannot read version" >&2; exit 1; }

NAME="GemPrismUpscaler-v${VERSION}-linux-x64"
OUT=${OUT_DIR:-"$ROOT/artifacts"}
STAGE="$OUT/$NAME"

find_first() {
    for candidate in "$@"; do
        if [ -n "$candidate" ] && [ -e "$candidate" ]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done
    return 1
}

resolve_lib() {
    local soname=$1
    local path
    path=$(ldconfig -p 2>/dev/null | awk -v n="$soname" '$1 == n { print $NF; exit }')
    [ -n "$path" ] && [ -e "$path" ] || return 1
    printf '%s\n' "$(readlink -f "$path")"
}

SWAY_BIN=${GEMPRISM_SWAY_BIN:-$(find_first \
    "$ROOT/src/GemPrismUpscaler/bin/Release/net10.0/runtime/sway/sway" \
    /tmp/sway-1.12-gp/build/sway/sway \
    "$ROOT/.agent/m2b-runtime/sway" || true)}
SWAYMSG_BIN=${GEMPRISM_SWAYMSG_BIN:-$(find_first \
    "$ROOT/src/GemPrismUpscaler/bin/Release/net10.0/runtime/sway/swaymsg" \
    /tmp/sway-1.12-gp/build/swaymsg/swaymsg \
    /usr/bin/swaymsg || true)}
WLROOTS_LIB=${GEMPRISM_WLROOTS_LIB:-$(find_first \
    /usr/lib/libwlroots-0.20.so \
    /usr/lib64/libwlroots-0.20.so || true)}
VKEYBOARD_BIN=${GEMPRISM_VKEYBOARD_BIN:-$(find_first \
    "$ROOT/tools/gemprism-vkeyboard" \
    "$ROOT/.agent/m2b-tools/gemprism-vkeyboard" || true)}
SHIM_BIN=${GEMPRISM_SHIM_BIN:-$(find_first \
    "$ROOT/.agent/m2b-runtime/xdg-decoration-v2-shim.so" \
    "$ROOT/src/GemPrismUpscaler/bin/Release/net10.0/runtime/sway/xdg-decoration-v2-shim.so" || true)}

for v in SWAY_BIN SWAYMSG_BIN WLROOTS_LIB VKEYBOARD_BIN; do
    if [ -z "${!v:-}" ]; then
        echo "missing $v; see the overrides at the top of this script" >&2
        exit 1
    fi
done

if ! grep -aq 'cursor_hint_seq' "$SWAY_BIN"; then
    cat >&2 <<EOF
sway is not the patched build: $SWAY_BIN
Build it with tools/build-nested-sway.sh (requires patched sway 1.12).
EOF
    exit 1
fi

if [ -z "${SHIM_BIN:-}" ]; then
    SHIM_BIN="$OUT/xdg-decoration-v2-shim.so"
    "$ROOT/tools/build-xdg-decoration-v2-shim.sh" "$SHIM_BIN"
fi

"$ROOT/tools/check-docs.py"

rm -rf "$STAGE"
rm -f "$OUT/$NAME.tar.gz" "$OUT/$NAME.zip" "$OUT/$NAME.sha256"
mkdir -p "$STAGE"

echo "publishing $NAME (single-file)"
dotnet publish "$ROOT/src/GemPrismUpscaler/GemPrismUpscaler.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$STAGE"

mkdir -p "$STAGE/runtime/sway" "$STAGE/licenses"
cp -f "$SWAY_BIN" "$STAGE/runtime/sway/sway"
cp -f "$SWAYMSG_BIN" "$STAGE/runtime/sway/swaymsg"
cp -f "$WLROOTS_LIB" "$STAGE/runtime/sway/libwlroots-0.20.so"
cp -f "$SHIM_BIN" "$STAGE/runtime/sway/xdg-decoration-v2-shim.so"
cp -f "$VKEYBOARD_BIN" "$STAGE/runtime/gemprism-vkeyboard"

# These are wlroots dependencies that a minimal KDE system may not have
# (the most common failure is libliftoff). Keep graphics/driver libraries
# from the host so the NVIDIA/Mesa stack is not mixed.
copy_optional_lib() {
    local soname=$1
    local license_pkg=$2
    local path
    path=$(resolve_lib "$soname") || {
        echo "missing bundled runtime library: $soname" >&2
        exit 1
    }
    cp -fL "$path" "$STAGE/runtime/sway/$soname"
    local license
    license=$(find_first "/usr/share/licenses/$license_pkg/LICENSE" \
        "/usr/share/licenses/$license_pkg/COPYING" || true)
    if [ -n "$license" ]; then
        cp -f "$license" "$STAGE/licenses/${license_pkg}-LICENSE"
    fi
}
copy_optional_lib libliftoff.so.0 libliftoff
copy_optional_lib libdisplay-info.so.3 libdisplay-info
copy_optional_lib libseat.so.1 seatd
copy_optional_lib liblua5.4.so.5.4 lua54

cp -f "$ROOT/src/GemPrismUpscaler/Assets/gemprism.png" "$STAGE/GemPrismUpscaler.png"
cp -f "$ROOT/README-package.md" "$STAGE/README.md"
cp -f "$ROOT/LICENSE" "$ROOT/NOTICE" "$ROOT/SOURCE.txt" "$STAGE/"
python3 "$ROOT/tools/package-docs.py" "$STAGE"

# Licenses for the bundled compositor and the effect shaders.
cp -f "$ROOT/shaders/anime4k/LICENSE" "$STAGE/licenses/Anime4K-LICENSE"
cp -f "$(find_first /usr/share/licenses/sway/LICENSE /tmp/sway-1.12-gp/LICENSE)" \
    "$STAGE/licenses/sway-LICENSE"
cp -f "$(find_first /usr/share/licenses/wlroots0.20/LICENSE /usr/share/licenses/wlroots/LICENSE)" \
    "$STAGE/licenses/wlroots-LICENSE"

# Normalize modes: readable docs/assets, executable entry points.
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE" -type f -exec chmod 644 {} +
chmod 755 "$STAGE/GemPrismUpscaler" \
    "$STAGE/runtime/sway/sway" "$STAGE/runtime/sway/swaymsg" \
    "$STAGE/runtime/gemprism-vkeyboard" \
    "$STAGE/docs/examples/nested-lutris.sh"
find "$STAGE/runtime" -name '*.so*' -exec chmod 755 {} +

# Smoke-test the staged app and documentation before archiving.
"$STAGE/GemPrismUpscaler" --version
"$STAGE/GemPrismUpscaler" --help >/dev/null
"$ROOT/tools/check-docs.py" "$STAGE/README.md" "$STAGE/docs"
LD_LIBRARY_PATH="$STAGE/runtime/sway" "$STAGE/runtime/sway/sway" --version
if LD_LIBRARY_PATH="$STAGE/runtime/sway" ldd "$STAGE/runtime/sway/sway" | grep -q 'not found'; then
    echo "bundled sway has unresolved shared libraries" >&2
    LD_LIBRARY_PATH="$STAGE/runtime/sway" ldd "$STAGE/runtime/sway/sway" | grep 'not found' >&2
    exit 1
fi
if LD_LIBRARY_PATH="$STAGE/runtime/sway" ldd "$STAGE/runtime/sway/libwlroots-0.20.so" | grep -q 'not found'; then
    echo "bundled wlroots has unresolved shared libraries" >&2
    LD_LIBRARY_PATH="$STAGE/runtime/sway" ldd "$STAGE/runtime/sway/libwlroots-0.20.so" | grep 'not found' >&2
    exit 1
fi

if find "$STAGE" -name '*.dll' -print -quit | grep -q .; then
    echo "managed DLLs leaked into the release tree" >&2
    exit 1
fi
if find "$STAGE" \( -name '.agent' -o -name '*.log' -o -name 'bin' -o -name 'obj' \
        -o -name '*.pdb' -o -path '*/docs/dev*' -o -name 'release-notes.md' \) -print -quit \
        | grep -q .; then
    echo "release tree contains development files" >&2
    exit 1
fi

tar -C "$OUT" -czf "$OUT/$NAME.tar.gz" "$NAME"
if command -v zip >/dev/null 2>&1; then
    (cd "$OUT" && zip -qry "$NAME.zip" "$NAME")
elif command -v bsdtar >/dev/null 2>&1; then
    (cd "$OUT" && bsdtar -a -cf "$NAME.zip" "$NAME")
else
    python3 - "$OUT" "$NAME" <<'PYZIP'
import os, sys, zipfile
out, name = sys.argv[1], sys.argv[2]
root = os.path.join(out, name)
with zipfile.ZipFile(os.path.join(out, name + ".zip"), "w",
                     compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
    for base, dirs, files in os.walk(root):
        for item in sorted(dirs):
            path = os.path.join(base, item)
            rel = os.path.relpath(path, out)
            info = zipfile.ZipInfo(rel + "/")
            info.external_attr = (0o755 << 16) | 0x10
            zf.writestr(info, b"")
        for item in sorted(files):
            path = os.path.join(base, item)
            rel = os.path.relpath(path, out)
            info = zipfile.ZipInfo(rel)
            info.external_attr = (os.stat(path).st_mode & 0o777) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, "rb") as src:
                zf.writestr(info, src.read())
PYZIP
fi
(cd "$OUT" && {
    sha256sum "$NAME.tar.gz"
    if [ -f "$NAME.zip" ]; then sha256sum "$NAME.zip"; fi
} > "$NAME.sha256")

echo
echo "release tree: $STAGE"
ls -lh "$OUT/$NAME.tar.gz" "$OUT/$NAME.zip" "$OUT/$NAME.sha256" 2>/dev/null || true