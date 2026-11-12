> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M2a 开发记录（Wayland 平台核心）

- 日期：2026-11-07 ~ 2026-11-08（Asia/Shanghai）
- 版本：`0.4.0`（M2a 核心 + portal + GlobalShortcuts 已实现；正式验收待补）
- 环境：KDE Plasma 6.8.2 Wayland + XWayland；NVIDIA RTX 4060 Laptop；HDMI-A-1 3840×2160@144（HDR、150%）+ DP-1 1920×1200@60（旋转 270°、100%，XWayland 坐标 1800×2880）
- 状态：**M2a 核心、portal/PipeWire、GlobalShortcuts 已实现并冒烟；原生焦点自动跟随/正式验收待补**

---

## 1. 已实现

### 1.1 后端抽象

- 新增 `IOverlay` / `OverlayBackend` / `OverlayPointerEvent`。
- `X11Overlay` 实现 `IOverlay`；`UpscaleSession` 改为持有 `IOverlay`。
- `VulkanBlitter` 增加 Wayland surface 构造：
  - `VK_KHR_wayland_surface`，`VkWaylandSurfaceCreateInfoKHR`。
  - X11 constructor 修复了字段初始化顺序（base constructor 里 surface 创建时 `_display` 必须已赋值）。
- `UpscaleSession` 新增 `OverlayBackend` / `pointerPassthrough` 构造参数；Wayland overlay 失败时自动回退 X11 overlay。

### 1.2 Wayland 呈现

- 新增 `Core/Platform/Linux/Wayland/WaylandOverlay.cs`：
  - NWayland 0.11.0 + `NWayland.Protocols.Wlr` 0.11.0。
  - `zwlr_layer_shell_v1` overlay、四边 anchor、`exclusive_zone=-1`、`keyboard_interactivity=none`。
  - `wp_viewporter` + `wp_fractional_scale_manager_v1`；buffer 物理像素、viewport destination 逻辑尺寸。
  - `wl_output` + `zxdg_output_v1` 输出枚举；旋转输出按 90/270 换算 scale。
  - show/hide、input region、输出消失回退、Pointer 事件（Enter/Motion/Button/Axis/Leave）。
- 新增 `WaylandGeometry` 纯函数：scale 计算、physical→logical 裁剪；有单元测试。

### 1.3 XWayland 混合

- X11 捕获（XShm + XDamage + 脏矩形）保持不变。
- 呈现走 Wayland layer-shell overlay + Vulkan Wayland WSI。
- 输入：`wl_pointer` → 物理坐标（scale）→ `CoordinateMapper` → `XSendEvent`；键盘不转发。
- 生命周期/弹窗/焦点继续用 X11 `X11WindowProbe`/`OverlayVisibility`。
- `--backend auto`：Wayland 会话 + XWayland 窗口 → Wayland overlay；失败回退 X11。

### 1.4 CLI / 配置

- `--backend auto|x11|wayland`
- `--input auto|passthrough|forward`
- `--capture auto|x11|portal`（`--window` 路径的 portal 仍未接；原生窗口用 `--portal-window`）
- 配置新增：

```toml
[capture]
backend = "auto"
mode = "auto"

[input]
pointer_mode = "auto"
```

- `ConfigStore` 白名单加入 `[capture]` 与 `input.pointer_mode`，避免被当成未知键回退。
- UI 的 StartSession 按配置/会话选择 backend；`_pointerPassthrough` 从配置读取。

### 1.5 portal + PipeWire 原生 Wayland 捕获（2026-11-07）

- 新增 `Core/Platform/Linux/Portal/PortalScreenCast.cs`：
  - `Tmds.DBus.Protocol` + `Tmds.DBus.Generator`，从系统 XML 生成 ScreenCast/Request/Session 代理。
  - 预订阅 Request `Response` 信号（避免即时响应竞态）。
  - `CreateSession` → `SelectSources`（window/monitor、cursor hidden、persist_mode=2）→ `Start` → `OpenPipeWireRemote`。
  - 解析 `streams`（node、pipewire-serial、size、source_type、mapping_id）与 `restore_token`。
- 新增 `PortalTokenStore`：`~/.config/GemPrismUpscaler/portal.token`，单次 token 轮换；KDE 下后续 Start 免打扰。
- 新增 `PipeWireGstCapture`：
  - portal fd + serial → `gst-launch-1.0 -v pipewiresrc fd=… target-object=… ! videoconvert ! video/x-raw,format=BGRA ! fdsink fd=<专用 pipe>`。
  - `-v` caps 日志在 stdout，帧走独立 pipe，避免污染。
  - 从 caps 解析实际尺寸（KDE 窗口流可能没有 portal `size`）。
- 新增 `PortalUpscaleSession`：`--portal-window`/`--portal-monitor`，layer-shell overlay + Vulkan 超分 + PipeWire 帧；overlay input region 为空（输入穿透）。
- GUI 会话页新增“Wayland 窗口…”按钮：后台线程等待系统选择器，不阻塞 UI；停止/失败清理。
- 版本 bump `0.4.0`；`--version` 已验证。
- 实测：vkcube 640×360（150% + 装饰）→ caps 960×582 → dest 3563×2160，60–69fps，无 Vulkan/portal 错误。
- **重要限制**：KDE 窗口流按输出 scale 合成。用户实测 720p 全屏 + VL：`pipewire caps 3840x2160`，2× 产生 8K 中间纹理，fps 10–12、VRAM 7.6GB、GPU 100%，且无可见超分收益。M2a 已加保护：源尺寸 >= 输出尺寸时改用 bilinear 1×；真正的低分辨率原生源需要 M2b 嵌套或 XWayland 捕获。
- 已知：KDE 窗口捕获含标题栏/阴影；M2a 正式验收以全屏/无边框原生窗口为准。

### 1.6 自动化

- `dotnet build GemPrismUpscaler.slnx -c Release`：0 warning / 0 error。
- `dotnet test GemPrismUpscaler.slnx -c Release`：**103/103**。
- 新增 `M2aWaylandTests`：1.5/1/2 scale、旋转输出、physical→logical、4:3 内容矩形、capture/input 配置 normalize。

---

## 2. 手工冒烟证据

命令模板：

```bash
./tools/testpattern --animate --size 1280x720 --win-id-file /tmp/tp.id --duration-ms 20000
GEMPRISM_CONFIG=/tmp/gemprism-test/config.toml \
GEMPRISM_LOG=/tmp/gemprism-test/app.log \
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler \
  --window <id> --monitor HDMI-A-1 --backend wayland
```

### 2.1 HDMI 4K / 150%

```text
wayland overlay configured output='HDMI-A-1' logical=2560x1440 physical=3840x2160 scale=1.5
engine started ... dest=3840x2160+0+0 ... extent=3840x2160
stats fps=50.9..60.2 captured≈presented; capture≈13ms diff≈1ms effect≈0.04ms gpuTail≈0.14ms
```

- 截图：`/tmp/gemprism-test/shot.png`（overlay 覆盖 HDMI，右侧为超分后的 testpattern；左侧 DP 为桌面）。
- 静止（`--static`）：首帧 captured=1/presented=1，之后 0 capture / 0 present。
- 弹窗（`--popup`）：`popup detected: overlay hidden` → `popup closed: overlay restored`，恢复时强制整帧。

### 2.2 DP 竖屏 / 旋转

```text
wayland overlay configured output='DP-1' logical=1200x1920 physical=1200x1920 scale=1
engine started ... dest=1200x900+0+510 ... extent=1200x1920
```

- 1024×768（4:3）→ 1200×900 + 上下黑边；比例正确。
- 说明：XWayland 的 DP 坐标是 1800×2880（KWin 的 XWayland 缩放），Wayland overlay 使用原生逻辑 1200×1920；这是预期差异。

### 2.3 输入

- 用 `/dev/uinput` 创建相对鼠标（不是 XTest；XTest 会触发 KWin EIS 权限窗口，应用本身不使用 XTest）。
- 日志：

```text
wayland pointer Motion 2043,1071 ... 3718,1743
wayland pointer ButtonPress 3718,1743 button=1
wayland pointer ButtonRelease 3718,1743 button=1
```

- 说明：`XWarpPointer`/XTest 的绝对跳转不产生 `wl_pointer.motion`；真实设备/uinput 会产生。应用的产品路径是 `XSendEvent`，不需要权限窗口。

### 2.4 原生 Wayland portal

- 选择 vkcube 窗口后：`portal screencast started streams=1 token=yes`；stream node/serial 有效。
- `pipewire caps 960x582`；`portal session started stream=960x582 ... dest=3563x2160`。
- stats 60–69fps；capture≈0.2ms、upload≈0.3ms、effect≈0.2ms、gpuTail≈0.7ms；无 Vulkan/portal 错误。
- 截图 `/tmp/gemprism-test/portal-shot.png`：超分画面正确；窗口化捕获含标题栏/阴影（已知）。
- restore token 存 `~/.config/GemPrismUpscaler/portal.token`；第二次 Start 免系统对话框（实测）。

### 2.5 X11 回退

`--backend x11` 仍正常：

```text
engine started ... overlay=0x3800001 ... dest=3840x2160+0+0
stats fps≈36 (testpattern)
```

---

## 3. 未完成 / 已知问题

1. **portal + PipeWire 已实现（M2a）**：
   - 入口是 `--portal-window` / `--portal-monitor`，GUI 也有“Wayland 窗口…”；`--window` + `--capture portal` 仍未接。
   - KDE 窗口捕获包含标题栏/阴影；正式验收用全屏/无边框源。
   - **2026-11-08 修复**：`attach(null)` 导致 KWin `closed` + layer 重置 Bottom；改为 offscreen hide，显示时恢复 anchor 并声明 Overlay；选择器默认每次弹出；`--portal-reuse-token` 可选复用。
2. **GlobalShortcuts 已实现（2026-11-08）**：
   - portal GlobalShortcuts v2 + `org.freedesktop.host.portal.Registry`；XDG trigger `ALT+SHIFT+A`。
   - 原生焦点下可用；KDE 首次授权。若 active key 存成 `none`，需系统设置手动分配。
   - **原生焦点自动跟随仍不做**：portal 不提供源窗口身份/焦点；M2b 嵌套合成器解决。M2a 用热键/托盘手动收起。
3. **输出热插拔未实机拔插**：
   - 代码监听 `global_remove`，当前输出消失时回退到另一块 X11 monitor；真实拔插待测。
4. **原生 Wayland 窗口化输入**：M2a 不做（M2b 嵌套合成器）。
5. **XWayland 捕获撕裂**：M2a 仍用 XShm；最终方案在 M2c。
6. **版本**：已 bump 到 0.4.0；本地 annotated tag `v0.4.0` 仍指向修复前提交，验收通过后移动/重建。
7. **真实视觉小说手测未做**：示例视觉小说 A / B/示例视觉小说 C 等需要用户按清单手测。

---

## 4. 关键文件

- `src/GemPrismUpscaler.Core/Platform/Linux/IOverlay.cs`
- `src/GemPrismUpscaler.Core/Platform/Linux/Wayland/WaylandOverlay.cs`
- `src/GemPrismUpscaler.Core/Platform/Linux/Wayland/WaylandGeometry.cs`
- `src/GemPrismUpscaler.Core/Platform/Linux/X11/X11Overlay.cs`
- `src/GemPrismUpscaler.Core/Engine/UpscaleSession.cs`
- `src/GemPrismUpscaler.Core/Rendering/VulkanBlitter.cs`
- `src/GemPrismUpscaler.Core/Configuration/AppConfig.cs`
- `src/GemPrismUpscaler.Core/Configuration/ConfigStore.cs`
- `src/GemPrismUpscaler/Program.cs`
- `src/GemPrismUpscaler/Views/MainWindow.axaml.cs`
- `tests/GemPrismUpscaler.Tests/M2aWaylandTests.cs`
- 文档：`docs/dev/m2-plan.md`、`docs/dev/roadmap.md`、`docs/dev/architecture.md`、`docs/compatibility.md`、`docs/dev/backlog.md`、`docs/cli.md`、`开发机归档/HANDOVER.md`

---

## 5. 下一步

1. 用户按 `开发机归档/M2a-用户验收步骤.md` 手测 XWayland 视觉小说 + 双屏/150% + 弹窗/输入 + 原生 portal/快捷键。
2. 验收通过后移动/重建本地 `v0.4.0` tag；agent 不 push。
3. 原生焦点自动跟随、窗口化原生输入归 M2b；正式验收前不实现。
4. 详细根因/证据见 `开发机归档/M2a问题排查-20261108.md`。
