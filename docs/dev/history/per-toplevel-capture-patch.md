> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M2b 归档：per-toplevel 抓取补丁（render-on-request）

状态：**已归档，未保留在分支上**（2026-11-10）
原始提交：`c6a1f17`（amend 前为 `3b1c8c1`），分支 `m2b-single-output`
归档原因：黑屏根因不是它；修好呈现反馈后不再需要。

## 这个补丁解决什么

toplevel 抓取源（`ext-image-copy-capture` + scene node）只在 scene 有
pending damage 时才渲染。窗口内容静止（或内容在 client 子表面里、主
scene 没有新 damage）时，抓取端请求帧会一直等不到新帧，宿主停留在第一
帧黑图上。补丁在 `source_request_frame()` 里强制整屏 damage 并同步
`source_render()`。

## 为什么归档

- 示例视觉小说 B 单输出 + toplevel 抓取的**黑屏根因是呈现反馈被抢**，不是抓不到帧：
  - 虚拟输出 refresh=0；`wlr_surface_send_enter()` 把内部抓取输出
    `CAPTURE-N` 插到 `current_outputs` 头部；两者 refresh 相同（0），
    CAPTURE 成为 frame pacing output；
  - CAPTURE 是合成器内部输出，只发 `wlr_output.events.commit`，不发
    `events.present`，于是 `wp_presentation_feedback` 永远不 presented，
    DXVK 卡在 `vkWaitForPresentKHR`，3 张 swapchain 图之后不再提交。
- 修复方式是把虚拟输出的刷新率设为物理屏真实刷新率（见分支提交
  `nested: pace the virtual output at the monitor refresh rate`），
  CAPTURE（0 Hz）不再胜出。
- 修复后使用**系统 sway / 未打补丁的 wlroots**验证：
  - testwin 对照：0 Hz 时抓取中 presented 冻结（pending 28→83→138）；
    143.936 Hz 时 presented 141→196→243，pending 保持 1。
  - 示例视觉小说 B （DXVK + Wayland，wayland 客户端）：
    `feedback=916 presented=916 discarded=0 commits=967`，画面正常。

因此当前 v1.0 路径不需要这个补丁。

## 恢复方法

1. 把下面的 patch 放回 `tools/patches/wlroots-0.20.2-capture-render-on-request.patch`；
2. 把构建脚本放回 `tools/build-nested-runtime.sh`（`chmod +x`）；
3. 运行 `tools/build-nested-runtime.sh`，产物在
   `src/GemPrismUpscaler/bin/Release/net10.0/runtime/sway/`；
4. `NestedSession` 会自动优先使用同目录的 `sway` 并为它设置
   `LD_LIBRARY_PATH`；`GEMPRISM_WLROOTS_LIB_DIR=off` 可强制使用系统库。

## 补丁内容

```diff
--- a/types/ext_image_capture_source_v1/scene.c	2026-11-10 03:55:12.075152470 +0800
+++ b/types/ext_image_capture_source_v1/scene.c	2026-11-10 03:55:12.075740195 +0800
@@ -134,12 +134,22 @@
 static void source_request_frame(struct wlr_ext_image_capture_source_v1 *base,
 		bool schedule_frame) {
 	struct scene_node_source *source = wl_container_of(base, source, base);
-	if (source->output.frame_pending) {
-		wlr_output_send_frame(&source->output);
-	}
-	if (schedule_frame) {
-		wlr_output_update_needs_frame(&source->output);
+
+	/* A capture client asking for a frame wants the current content of the
+	 * source, even when the scene hasn't been damaged since the previous
+	 * frame. Waiting for the next damage event leaves static windows (and
+	 * windows whose content lives in client sub-surfaces) without frames.
+	 * Render on demand: force a full damage region and render now. */
+	int width, height;
+	wlr_output_effective_resolution(&source->output, &width, &height);
+	if (width > 0 && height > 0) {
+		pixman_region32_clear(&source->scene_output->pending_commit_damage);
+		pixman_region32_union_rect(&source->scene_output->pending_commit_damage,
+			&source->scene_output->pending_commit_damage, 0, 0, width, height);
 	}
+	source_render(source);
+
+	(void)schedule_frame;
 }

 static void source_copy_frame(struct wlr_ext_image_capture_source_v1 *base,
```

## 构建脚本

```sh
#!/bin/bash
# Build the pinned nested-compositor runtime (sway + patched wlroots 0.20.2).
#
# The nested session launches its own headless sway. We pin both binaries so a
# semantic patch in wlroots stays effective regardless of distro updates, and
# so the pair is validated together.
#
# Usage:
#   tools/build-nested-runtime.sh [OUT_DIR]
#
# OUT_DIR defaults to src/GemPrismUpscaler/bin/Release/net10.0/runtime/sway
# (the directory NestedSession.ResolveSwayPath() looks at).
#
# Environment:
#   GEMPRISM_WLROOTS_TARBALL  local wlroots-0.20.2.tar.gz (otherwise downloaded)
#   GEMPRISM_SWAY_SOURCE      sway binary to bundle (otherwise `command -v sway`)
#   GEMPRISM_WLROOTS_VERSION  default 0.20.2
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
VERSION=${GEMPRISM_WLROOTS_VERSION:-0.20.2}
PATCH="$ROOT/tools/patches/wlroots-${VERSION}-capture-render-on-request.patch"
DEFAULT_OUT="$ROOT/src/GemPrismUpscaler/bin/Release/net10.0/runtime/sway"
OUT=${1:-$DEFAULT_OUT}
URL="https://gitlab.freedesktop.org/wlroots/wlroots/-/archive/${VERSION}/wlroots-${VERSION}.tar.gz"
WORK=$(mktemp -d /tmp/gp-wlroots-build-XXXXXX)
trap 'rm -rf "$WORK"' EXIT

[ -f "$PATCH" ] || { echo "missing patch: $PATCH" >&2; exit 1; }

# --- wlroots source ---------------------------------------------------------
TARBALL=${GEMPRISM_WLROOTS_TARBALL:-}
if [ -z "$TARBALL" ]; then
    TARBALL="$WORK/wlroots-${VERSION}.tar.gz"
    echo "downloading $URL"
    curl -fsSL -o "$TARBALL" "$URL"
fi
mkdir -p "$WORK/src"
tar xf "$TARBALL" -C "$WORK/src"
SRC="$WORK/src/wlroots-${VERSION}"
[ -d "$SRC" ] || { echo "unexpected tarball layout in $TARBALL" >&2; exit 1; }

echo "applying $(basename "$PATCH")"
patch -p1 -d "$SRC" < "$PATCH"

# --- toolchain --------------------------------------------------------------
PY=python3
if ! command -v meson >/dev/null || ! command -v ninja >/dev/null; then
    VENV="$WORK/venv"
    "$PY" -m venv "$VENV"
    "$VENV/bin/pip" install -q meson ninja
    export PATH="$VENV/bin:$PATH"
fi

# --- build ------------------------------------------------------------------
meson setup "$WORK/build" "$SRC" -Dexamples=false -Dxwayland=enabled \
    -Dbuildtype=debugoptimized >/dev/null
ninja -C "$WORK/build" >/dev/null
LIB="$WORK/build/libwlroots-${VERSION%.*}.so"
[ -f "$LIB" ] || LIB=$(ls "$WORK/build"/libwlroots-*.so | head -1)
[ -f "$LIB" ] || { echo "wlroots build produced no shared library" >&2; exit 1; }

# --- bundle -----------------------------------------------------------------
SWAY_SRC=${GEMPRISM_SWAY_SOURCE:-$(command -v sway || true)}
[ -n "$SWAY_SRC" ] && [ -f "$SWAY_SRC" ] || {
    echo "no sway binary to bundle (set GEMPRISM_SWAY_SOURCE)" >&2
    exit 1
}
mkdir -p "$OUT"
cp -f "$LIB" "$OUT/"
cp -f "$SWAY_SRC" "$OUT/sway"
chmod 755 "$OUT/sway"

SONAME=$(readelf -d "$OUT/sway" | sed -n 's/.*NEEDED.*\[\(libwlroots[^]]*\)\].*/\1/p' | head -1)
[ -n "$SONAME" ] || { echo "cannot determine sway's wlroots soname" >&2; exit 1; }
if [ ! -f "$OUT/$SONAME" ]; then
    # SONAME may be libwlroots-0.20.so while the build produced the same name;
    # create a symlink if the soname differs from the built file name.
    ln -sf "$(basename "$LIB")" "$OUT/$SONAME"
fi

echo "runtime bundled in $OUT"
ls -l "$OUT" | sed 's/^/  /'
echo
echo "The app prefers this directory automatically; to force it:"
echo "  GEMPRISM_SWAY=$OUT/sway <app> --nested ..."
```
