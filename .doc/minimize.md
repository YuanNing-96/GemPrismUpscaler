# 游戏自我最小化（Esc）：现状、限制与 sway 侧修复方案

> 状态：**v1.0 按当前方案发布**（应用层只吞黑帧，宿主窗口不跟随最小化）。
> 本文记录现象、已排除的做法，以及以后想彻底修时该怎么改 sway / wlroots。
> 相关代码：`NestedWindowProbe`（监听 `WM_CHANGE_STATE`）、
> `NestedUpscaleSession.MirrorMinimize`（丢全黑帧）、
> `WaylandHostWindow.Iconify`（宿主 `set_minimized`，当前不调用）。

## 1. 现象

秽翼（Wine/Proton，X11 模式）在游戏内按 Esc 会“隐藏自己”，KDE 直连时窗口进任务栏。
在嵌套会话里：

| 现象 | 现在 | 说明 |
|---|---|---|
| 按 Esc 后宿主人窗口黑闪一下 | **已修** | 客户端隐藏前画一帧全黑；应用在收到最小化请求后 600ms 内丢弃“整帧全黑”的候选帧 |
| 按 Esc 后鼠标悬浮高亮消失 | **接受** | 游戏自己认为窗口被最小化、暂停渲染；不影响按键与点击，属于游戏行为 |
| 宿主窗口跟随最小化 | **不做** | 见第 3 节：应用层无法可靠唤醒 Wine |
| Wayland 模式（160×31 小条） | **不做** | Wine 把 toplevel 缩成小条，KDE 直连同样异常，且没有协议信号 |

按 Esc 之后游戏仍在运行（可以继续点击/按键），只是不再重绘悬浮高亮。这是当前 v1.0 的已知限制。

## 2. X11 的真实信号（已实测）

Wine 按 ICCCM 向嵌套 XWayland 的 root 发 `WM_CHANGE_STATE(IconicState)`：

```
$ DISPLAY=:2 /tmp/x11-watch 0x2400001
05:15:30.508 ClientMessage win=0x2400001 type=WM_CHANGE_STATE fmt=32 d=3,0,0,0,0
```

wlroots 0.20.2 会把它翻译成 `wlr_xwayland_surface::events.request_minimize`
（`xwayland/xwm.c:1875`），sway 也接了（`sway/desktop/xwayland.c:622`），但实现是：

```c
bool focused = seat_get_focus(seat) == &view->container->node;
wlr_xwayland_surface_set_minimized(xsurface, !focused && e->minimize);
```

**窗口正被聚焦时 `!focused` 为假，请求被丢弃**（这是 sway 对 Wine/老客户端“伪最小化请求”的
防御）。另外 wlroots 的 `wlr_xwayland_surface_set_minimized()`（`xwayland/xwm.c:2716`）只修改
`surface->minimized` 和 `WM_STATE` / `_NET_WM_STATE` 属性，**并不会把 X 窗口 unmap**。
所以嵌套里永远看不到 `WM_STATE=Iconic`，客户端也永远收不到 unmap/map 转换。

## 3. 应用层为什么做不了“宿主跟随最小化”

尝试过三种做法（完整记录见 `.agent/M2b-归档-游戏自我最小化.md`）：

1. 收到请求 → 最小化宿主；恢复时 unmap+map 游戏窗口唤醒它 → Wine 在恢复后 ~40ms 又发一次最小化请求。
2. 改成真 WM 序列（`WM_STATE=Iconic` + unmap / `WM_STATE=Normal` + map）→ 恢复后 ~25ms 又发。
3. 在 KDE 直连上抓 KWin 的真实顺序并完全照搬：

   ```
   最小化：_NET_WM_STATE 加 HIDDEN → Unmap → WM_STATE=Iconic
   恢复：  _NET_WM_STATE 去 HIDDEN → Map   → WM_STATE=Normal
   ```

   KDE 直连恢复后客户端不再发请求；嵌套里照搬后 map 之后 25ms 仍然收到：

   ```
   05:15:30.481 Map win=0x2400001
   05:15:30.508 ClientMessage win=0x2400001 type=WM_CHANGE_STATE d=3
   ```

结论：Wine 的 Win32 最小化状态（`WS_MINIMIZE`）不会被“客户端侧 map”清除，游戏自己会再调一次
最小化。要彻底修，必须让嵌套合成器变成真正的窗口管理器（由它 unmap/map 窗口），也就是第 4 节。

> 注：KDE 直连的“从任务栏恢复”是否稳定，建议以后再做补丁前先复验一次；如果那边也会弹回，
> 那问题在 Wine/游戏侧，sway 补丁也救不了。

## 4. 以后要彻底修：需要的改动

目标语义：sway 里一个 view 有 `minimized` 状态——被最小化时窗口从输出上消失（X11 客户端收到
真实 unmap），恢复时重新 map；状态通过 IPC 暴露给应用，应用据此最小化/恢复宿主窗口。

### 4.1 wlroots 补丁（必须）

- `wlr_xwayland_surface_set_minimized()` 增加“真正 unmap/map X 窗口”的行为（或新增
  `wlr_xwayland_surface_iconify()`），并让 xwm 区分 **WM 主动 unmap** 与 **客户端自己
  withdrawn**：目前 `xwm_handle_unmap_notify()`（`xwayland/xwm.c:1432`）会把任何 unmap 当成
  withdrawn（`xwayland_surface_dissociate()`），WM 主动 unmap 会直接把 surface 拆掉。
- 恢复时重新 `xcb_map_window`，并保持 surface/关联对象不重建（或允许重建但保留 `minimized` 语义）。
- 预计 ~80–150 行 C，外加 wlroots 单测/手工回归。

### 4.2 sway 补丁

- 去掉 `handle_request_minimize()` 里的 `!focused` 限制（或加配置项），把 `minimized` 记到
  `sway_view`/`sway_container`。
- 最小化时把容器从输出布局里摘掉（不参与 arrange/damage/焦点遍历），焦点交给下一个 view；
  恢复时插回原来的 workspace/位置。
- IPC：`get_tree` 增加 `minimized: bool`（`sway/ipc-json.c`），新增命令
  `[con_id=N] unminimize`（或让 `focus`/`activate` 顺带恢复，`set_activated` 已经有
  `wlr_xwayland_surface_set_minimized(surface, false)` 的先例，`sway/desktop/xwayland.c:281`）。
- 预计 ~150–250 行 C + IPC 文档 + 回归测试。

### 4.3 应用侧改动

- `NestedWindowTracker` 解析 `minimized`；会话把它映射到宿主 `set_minimized`。
- 宿主 `Restored`（KWin `activated` configure）时调用 IPC `unminimize`，而不是自己去 map 窗口。
- capture 在 view 消失/重建期间的处理（现有 `RekeyWindow`/`RecreateCapture` 可复用）。
- 预计 ~80–120 行 C#。

### 4.4 成本评估

| 项 | 估计 |
|---|---|
| wlroots 补丁 + 回归 | 1–2 人日 |
| sway 补丁 + IPC + 回归 | 1.5–2.5 人日 |
| 应用侧 + 联调（X11/Wayland、对话框、全屏、多窗口） | 1–2 人日 |
| 构建/维护 | 现在只维护一个 sway 补丁；真做最小化要**同时维护 wlroots 和 sway 两个 fork**，`tools/build-nested-sway.sh` 要改成先构建 wlroots 再构建 sway |
| **合计** | **约 3–5 人日**，之后每个上游版本都要重新 rebase 补丁 |

风险点：

- 改变嵌套里所有 X11 窗口的 map/unmap 语义，可能影响对话框、全屏切换、窗口销毁路径；
- 我们依赖 `ext-image-copy-capture` / foreign-toplevel 抓帧，view 被摘掉期间 capture 会失效，
  恢复时必须重建（现有逻辑可复用，但要重新验证）；
- Wayland 游戏仍然没有信号（Wine 的 160×31 不是协议最小化），sway 补丁只解决 X11。

**结论：成本偏高（3–5 人日 + 长期双补丁维护），v1.0 按当前方案发布。** 等上游 sway/wlroots
有了最小化语义，或者确实有用户强烈需求时再做。

## 5. 当前实现位置（便于以后替换）

- 监听请求：`src/GemPrismUpscaler.Core/Platform/Linux/Nested/NestedWindowProbe.cs`
  （`TryTakeMinimizeRequest`，`SubstructureNotifyMask`；不能选 `SubstructureRedirectMask`）
- 会话处理：`NestedUpscaleSession.MirrorMinimize`（当前只设 `SuppressBlackUntil`）
- 全黑帧判定：`NestedUpscaleSession.IsUniformlyBlack`（采样网格 mean≤3 且 95% 以上像素亮度<8）
- 宿主最小化/恢复（已实现、当前不调用）：`WaylandHostWindow.Iconify` / `Restored`
- 恢复宿主时重建 capture / 聚焦：`RekeyWindow` → `RecreateCapture`、`FocusWindow`
- 调试开关：`GEMPRISM_TRACE_FRAMES=1`（逐帧打印 `mean`/`dark%`/`sig`）

## 6. 相关记录

- 归档（三次尝试与放弃原因）：`.agent/M2b-归档-游戏自我最小化.md`
- 分组文档 G2：`.agent/M2b-新路线-验收问题分组-20261111.md`
- 更新记录：`.doc/changelog.md`（“X11 游戏按 Esc 最小化时宿主‘黑闪一下’”）
- 现有 sway 补丁的做法（可参考补丁形式与构建脚本）：
  `tools/patches/sway-1.12-cursor-hint-ipc.patch`、`tools/build-nested-sway.sh`、`.doc/pointer-warp.md`
- 提交：`a1ee03e`（最终行为）、`b327d17` / `b300bc6` / `a0427e4`（尝试记录）