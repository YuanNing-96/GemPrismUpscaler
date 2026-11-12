> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M2a 验收问题排查记录（2026-11-08）

- 版本：`0.4.0`（修复未 bump；本地 tag 仍指向修复前提交）
- 环境：KDE Plasma 6.8.2 Wayland + XWayland；RTX 4060 Laptop；HDMI-A-1 3840×2160/150%，DP-1 1200×1920/100%（旋转）
- 用户验收记录：`开发机归档/M2a验收记录.md`
- 结论：**4 类问题中 3 类已修复/已接；原生 Wayland 自动跟随焦点仍不在 M2a（M2b）**

---

## 1. 拖拽/弹窗后崩溃（已修复）

### 现象

```text
zwlr_layer_surface_v1#16: error 0: a buffer has been attached to a layer surface
prior to the first layer_surface.configure event
stopping: present error: InvalidOperationException: vkAcquireNextImageKHR failed: ErrorSurfaceLostKhr
```

在以下路径稳定复现：

- XWayland 弹窗关闭 `popup closed: overlay restored` 后；
- 源窗口失焦隐藏、重新聚焦 `overlay shown` 后；
- 原生 portal 快捷键收起后恢复。

### 根因

`WaylandOverlay.Hide()` 用 `wl_surface.attach(null)` + `commit()` 把 layer surface **unmap**。KWin 在 unmap 时会：

1. 发出 `zwlr_layer_surface_v1.closed`；
2. 把 layer surface 状态重置为协议默认的 **Bottom**（`LayerShellV1Window.belongsToLayer()` 映射为 `BelowLayer`）。

恢复时再 attach buffer，KWin 既可能报 `buffer attached prior to first configure` / `SurfaceLostKhr`，也会把 overlay 放在普通窗口/全屏游戏下面。`set_layer(Overlay)` 也无效，因为 `closed` 之后该 layer surface 的请求会被忽略。

### 修复（最终）

不再 unmap：

- `Hide()`：保留 surface 和 Vulkan swapchain，清空 input region；把 anchor 改为 Top|Left，`set_margin(left = 整个逻辑桌面右边界 + 1 - 当前输出 X)`，surface 移到所有输出之外；layer 保持 Overlay。
- `Show()`：恢复四边 anchor、margin 0，重新 `set_layer(Overlay)`，用实际逻辑尺寸提交；surface 回到屏幕，layer 仍是 Overlay。
- 因为从未 unmap，KWin 不会发 `closed`，也不会重置 layer；弹窗/焦点恢复不会再丢 surface。

中间尝试过“隐藏 roundtrip + 显示时重请求 configure”，它消除了崩溃，但 KWin 已把 layer 重置为 Bottom，恢复后仍会被游戏窗口盖住；因此最终改为 offscreen hide。

### 修复

`src/GemPrismUpscaler.Core/Platform/Linux/Wayland/WaylandOverlay.cs`：

- `Hide()`：不 attach null；保留 surface/swapchain，把 anchor 设为 Top|Left，margin.left 设为 `desktopRight + 1 - outputX`（跨过所有输出右边界），surface 移到屏幕外；input region 由 engine 置空。
- `Show()`：恢复四边 anchor、margin 0，重新 `set_layer(Overlay)`，按实际逻辑尺寸提交；surface 回到屏幕。
- 删除 unmap/roundtrip/configure 恢复路径，避免 KWin `closed` 和 layer 重置。

### 验证

XWayland 弹窗（`tools/testpattern --popup`）：

```text
overlay hidden (popup window)
wayland overlay configured output='HDMI-A-1' ...
overlay shown
popup closed: overlay restored
stats fps=62.9 ... hidden=False
```

`stderr` 为空；恢复后 60+ fps。

原生 portal `Alt+Shift+A` 收起/恢复：

```text
portal overlay hidden (user)
portal global-shortcuts activated id=toggle-overlay
wayland overlay configured output='HDMI-A-1' ...
portal overlay shown (user)
stats fps=42.0 ... hidden=False
```

`stderr` 为空。

### 验证层级

KWin 脚本读取 layer 值（`Window.layer`）：

```text
初始 overlay: layer=9（Overlay）
隐藏/显示后 overlay: layer=9（Overlay）
全屏源窗口: layer=5
```

修复前隐藏/显示后 overlay 为 `layer=1`（Below），所以被全屏游戏盖住。修复后保持 9。

XWayland 窗口化源 + 显示后截图：HDMI 上是放大后的 testpattern，而不是小源窗口/桌面，确认 overlay 在最前。

---

## 2. 弹窗/最小化后不自动恢复、热键第二次不生效（已修复主因）

用户看到的“弹窗关闭不恢复、点游戏窗口不恢复、Alt+Shift+A 隐藏后无法再显示”，主因是上面的 surface lost：恢复动作一执行就结束会话，后续自然没有 overlay。

修复后：

- 弹窗关闭 → `overlay shown` → 画面恢复；
- 源窗口失焦再聚焦 → `overlay shown`；
- 最小化再恢复 → 走同一 `source viewable again` 路径。

XWayland 全局热键仍由 `XGrabKey` 提供；**仅在 XWayland/X11 焦点下可靠**。原生 Wayland 焦点下改用 portal GlobalShortcuts（见 §3）。

---

## 3. 原生 portal：选择器与热键（已修复/已接）

### 3.1 选择器不再自动套用上次窗口

旧行为：`PortalUpscaleSession` 无条件读取 `portal.token` 并传给 `SelectSources(restore_token)`，KDE 会跳过选择器直接选上次窗口，用户无法改选。

修复：

- 默认**不传** restore token，每次都弹系统选择器；
- 新增 `--portal-reuse-token` 显式复用；
- `portal.token` 仍轮换保存，供显式复用时使用。

日志：`portal picker mode: choose every start`。

### 3.2 portal GlobalShortcuts

现象：原生 Wayland 焦点下 `Alt+Shift+A` 无反应；XWayland 的 `XGrabKey` 收不到原生焦点按键。

实现：

- `PortalGlobalShortcuts`：GlobalShortcuts v2 `CreateSession` → 订阅 `Activated` → `BindShortcuts`；
- `PortalAppId`：通过 `org.freedesktop.host.portal.Registry` 注册 app id；自动写
  `~/.local/share/applications/org.gemprism.GemPrismUpscaler.desktop`（Registry 要求 app id 匹配 desktop 文件名）；
- trigger 使用 XDG 大写格式 `ALT+SHIFT+A`（KDE 对 `Alt+Shift+A` 报 `Unknown modifier "Alt"`）；
- 原生会话 `PortalUpscaleSession` 增加 `RequestToggleVisibility`、`IsOverlayVisible`；托盘也可收起/恢复；
- KDE 首次会弹“全局快捷键权限请求”。

验证（app id `org.gemprism.GemPrismUpscalerB`，避免测试残留）：

```text
portal global-shortcuts bound id=toggle-overlay trigger='Alt+Shift+A'
portal global-shortcuts activated id=toggle-overlay
portal overlay hidden (user)
...
portal overlay shown (user)
stats fps=42 ...
```

### 3.3 KDE 把快捷键存成 `none`

用户第一次授权后 `~/.config/kglobalshortcutsrc` 出现：

```ini
[org.gemprism.GemPrismUpscalerC]
toggle-overlay=none,Alt+Shift+A,显示/隐藏宝石棱镜超分覆盖层
```

第一个字段是**当前生效键**，为 `none` 时热键不会触发；第二个是默认值。处理：

1. 打开“系统设置 → 快捷键 → GemPrismUpscaler”；
2. 找到“显示/隐藏宝石棱镜超分覆盖层”；
3. 点右侧快捷键，按 `Alt+Shift+A`，应用。

程序日志会打 `trigger_description`；若为 `none`，会提示去系统设置。

---

## 4. 原生 Wayland 失去焦点不自动收起（未修复，M2a 限制）

现象：原生 portal 超分时切到其他程序，overlay 仍覆盖屏幕；`跟随焦点` 设置对原生源无效。

原因：

- portal ScreenCast 只返回 PipeWire node、serial、尺寸、`source_type`，**不返回源窗口 PID/UUID**；
- 没有通用的“当前活动窗口”Wayland 协议；KWin 的 active window 只能通过 KDE 私有脚本/DBus 获取，且还要把 active window 与 portal 源对应起来；
- XWayland 的 `X11WindowProbe.IsFocused` 对原生源只会看到 KWin 的 0x200000 焦点代理。

M2a 的可用替代：

- `Alt+Shift+A`（GlobalShortcuts，原生焦点可用）；
- 托盘“隐藏/显示覆盖层”（若托盘未被 overlay 盖住）；
- 把源和 overlay 放同一块屏，避免跨屏操作困难。

正式修复在 **M2b 嵌套合成器**：嵌套输出由我们控制，源窗口/焦点/弹窗都在同一合成器内，天然可判断。

---

## 5. 其他发现

- **KDE portal GlobalShortcuts 的陈旧会话**：如果上一个进程被强杀，未 `Close` 的 shortcut session 可能让下一次 `CreateSession` 长时间无响应；重启 `xdg-desktop-portal` / `xdg-desktop-portal-kde` 可清掉。正常退出会 `Session.Close`；程序已把注册超时限制为 5 s，避免拖住捕获。
- **窗口/显示器建议同屏**：测试时 vkcube 在 DP、overlay 在 HDMI，用户无法同时操作；这不是产品崩溃，改为同屏即可。CLI `--monitor` / GUI 输出显示器要与源一致。
- **offscreen 隐藏的边界**：隐藏时 surface 仍 mapped，只是移出所有输出。早期版本用到“左侧 -width-1”，在多屏（DP 在 HDMI 左侧）会把 overlay 放到 DP 上；现改为 `desktopRight + 1 - outputX`，KWin 实测隐藏几何 `x=3761`（桌面右边界 3761），显示恢复 `x=0` 回到原输出。其他合成器对超大正 margin 的行为需 M2b/后续验证。

---

## 6. 代码/文档变更

- `WaylandOverlay`：取消 unmap；隐藏移到整个逻辑桌面右侧之外，显示恢复 anchor + Overlay layer。
- `PortalUpscaleSession`：默认不复用 token；GlobalShortcuts；手动 toggle；隐藏时跳过 present；force render on show。
- `PortalGlobalShortcuts`、`PortalAppId`：新增。
- `HotkeyBinding.ToPortalTrigger()`：XDG 大写格式。
- `Program`：`--portal-reuse-token`；hotkey 传入 portal session。
- `MainWindow`：托盘 toggle 支持 portal。
- `GemPrismUpscaler.Core.csproj`：新增 GlobalShortcuts / Registry XML。
- 文档：`cli.md`、`changelog.md`、`compatibility.md`、`m2-plan.md`、`M2a-用户验收步骤.md`、`HANDOVER.md`、本文件。
- 测试：新增 portal trigger 格式；103/103。

## 7. 复现/验收日志

- 修复前（用户）：`开发机归档/M2a验收记录.md` 内 14:12 日志。
- 修复后 XWayland 弹窗：本机 `/tmp/gp-final2/app.log`（临时，未入库）。
- 修复后原生 toggle：`/tmp/gp-native/app.log`（临时，未入库）。
- 原生 portal 窗口 3840×2160/40–45fps：`/tmp/gp-user3/app.log`（临时，未入库）。
---

## 8. 为什么 M1b 没有这些问题（路径对照）

用户手测 M1b 时没有“隐藏跑到另一块屏/显示在下面”，因为 M1b 与 M2a 使用的呈现路径完全不同。

| | M1b / M1c | M2a 默认（Wayland 会话 + XWayland 源） |
|---|---|---|
| 呈现 | X11 override-redirect 窗口 | Wayland `zwlr_layer_shell_v1` + Vulkan Wayland WSI |
| 隐藏 | `XUnmapWindow` | 不 unmap；surface 保留，移到所有输出右侧之外 |
| 显示 | `XMapRaised` | 恢复 anchor/margin，重新声明 Overlay layer |
| 层级 | X11 override-redirect 天然在普通窗口之上；`XMapRaised` 再抬升 | 依赖 layer-shell Overlay layer；KWin unmap 会把 layer 重置为 Bottom |
| 跨屏 | unmap 后从整个 X 屏幕消失，不会落到另一块屏 | 早期 offscreen 用当前输出左侧 `-width-1`，多屏时会落到 DP |
| 崩溃 | 无 layer-shell 生命周期 | `attach(null)` 后重新 attach 可能 `buffer attached prior to first configure` / `SurfaceLostKhr` |

M1b 的 `X11Overlay` 代码：

```csharp
// 显示
XClearWindow(...);
XMapRaised(...);
// 隐藏
XUnmapWindow(...);
```

M2a 的 `WaylandOverlay` 不能照搬 `XUnmapWindow`，因为 Wayland 客户端不能 raise 自己的 layer surface；KWin 在 unmap 时还会发 `closed` 并重置 layer。最终用“保持 mapped + 移到全局桌面右侧外”的方式同时保住 layer 和 swapchain。

因此：

- 这不是“M1b 有但没发现”的旧问题，而是 **M2a 新引入 layer-shell 呈现 + 修复层级时的一次坐标回归**。
- 想对照 M1b 行为，可用 `--backend x11`（X11 overlay），隐藏/显示走 `XUnmapWindow`/`XMapRaised`。
