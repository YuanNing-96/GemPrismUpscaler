# GemPrismUpscaler · M2 阶段计划（M2a / M2b / M2c）

> 历史计划：记录 M2 当时的范围与决策；v1.0 的最终用户行为以 `docs/` 用户文档为准。

- 更新时间：2026-11-13
- 当前基线：`v0.5.0`（M2b 单输出主路径，2026-11-13 打 tag）；此前 `v0.4.2` 是验收快照；原 M2c `0.6.0` 不阻塞 v1.0
- M2 目标版本：M2a `0.4.0`、M2b `0.5.0`；原 M2c `0.6.0` 为 v1.0 后可选
- 目标环境：KDE Plasma 6.8.2 Wayland + XWayland；NVIDIA RTX 4060 Laptop；HDMI-A-1 3840×2160@144（HDR、150%）+ DP-1 1800×2880（100%）
- 本文是 M2 的计划与交接文档。`roadmap.md` 是摘要，本文是详细范围/验收/实现决策；[architecture.md](architecture.md) 记录架构，[compatibility.md](../compatibility.md) 记录支持矩阵，[backlog.md](backlog.md) 记录未排期项。

> M2a 只实现 Wayland 平台核心；M2b 的嵌套合成器在 M2b 阶段实施；原 M2c 的色彩/性能推迟到 v1.0 后作为可选优化。
> 按用户约定：M2 不做旧功能完整回归，只保主要路径；完整回归留 v1.0 前统一做。

---

## 1. 已确认的决策

| # | 决策 | 说明 |
|---|---|---|
| 1 | M2 拆成 M2a / M2b / M2c | M2a 平台核心 → M2b 嵌套合成器 → 原 M2c 色彩/性能（现 v1.0 后可选） |
| 2 | 不做 promote | 不允许为了输入映射把源窗口请求成全屏；源窗口状态保持不变 |
| 3 | 窗口化原生 Wayland 输入用嵌套合成器 | 只有嵌套合成器能同时做到“窗口化 + 无光标跳变 + raw input” |
| 4 | 嵌套实现：sway + helper | sway 作为私有虚拟显示器；helper 用 `wlr-screencopy` + `wlr-virtual-pointer` |
| 5 | Lutris 用 per-game 环境变量/wrapper | 不要求把整个 Lutris 搬进嵌套会话；Steam/Heroic 文档化 best-effort |
| 6 | KDE 优先，wlroots 协议级 | wlroots 真实桌面验收推后，可能不在 1.0 前完整验证 |
| 7 | 弹窗由嵌套 compositor 处理 | 抓整个嵌套输出；对话框在内部浮动/置顶；不需要 X11 弹窗探测 |
| 8 | 主窗口识别分层降级 | Profile / token+PID / sway IPC / transient+type / 尺寸；歧义时手动选择 |
| 9 | 4:3 与非 720p 必须支持 | 嵌套输出尺寸 = 主窗口客户区尺寸；最终 fit/stretch 走现有 LayoutCalculator |
| 10 | M2a 不做旧功能完整回归 | 构建 + 103 项测试 + M2a 主要路径冒烟 |

---

## 2. 目标与非目标

### 2.1 M2 总目标

在 Linux（优先 KDE Wayland）上，把 GemPrism 从“仅 X11/XWayland 窗口捕获”扩展为：

- 原生 Wayland 窗口可捕获、超分、呈现；
- Wayland 会话下输入不抢焦点、不产生光标跳变；
- 窗口化原生 Wayland 游戏在嵌套合成器模式下输入 1:1；raw input 原为 M2 目标，现推迟到 v1.0 后；
- 多显示器/热插拔/分数缩放/负坐标/不同宽高比可用；
- HDR/色彩、零拷贝与性能原计划在 M2c 收尾；**2026-11-13 决策：M2c 两项也不阻塞 v1.0，
  推迟到 v1.0 后作为可选优化**。

### 2.2 非目标（M2 内不做）

- 不做 game overlay HUD、不注入游戏进程、不改游戏文件/存档/Wine prefix。
- 不做帧生成。
- 不做 Windows/macOS 后端。
- 不做 GNOME 完整支持（Mutter 不支持 `zwlr_layer_shell_v1`；M2 只承诺 KDE + wlroots 协议路径）。
- 不在 M2a 做嵌套合成器、HDR、零拷贝、跨屏连续拼接。
- 不承诺在 M2 阶段完成 wlroots 真实桌面全量验收。

---

## 3. 当前基线与已知限制（M1c / 0.3.0）

- 构建：`dotnet build -c Release` 0 warning / 0 error；`dotnet test` 146/146（M2b 主路径收尾时）。
- 捕获：X11/XWayland 窗口，`XShmGetImage` + XDamage + 64px 脏矩形。
- 呈现：X11 override-redirect overlay + Vulkan Xlib swapchain。
- 输入：overlay 接收指针 + `XSendEvent` 转发；键盘不转发，焦点交还游戏。
- 已知限制：
  - **GPU 窗口捕获撕裂**：XShm 读取 GPU 更新窗口可能拿到半帧；M1c 未解决，M2 处理。
  - 原生 Wayland 窗口不支持。
  - 多显示器只有 M1a 基础版；热插拔、每屏 DPI 完整处理在 M2。
  - HDR 是 SDR 直通。
  - FPS/raw input 不支持。
  - 代码没有实际的后端接口：`UpscaleSession` 直接 new X11 类；`design.md` 的 `CaptureBackend/PresentBackend/InputBackend` 是目标架构。

---

## 4. 阶段总览

| 阶段 | 版本 | 主题 | 交付 | 不包含 |
|---|---|---|---|---|
| **M2a** | 0.4.0 | Wayland 平台核心 | 后端抽象；Wayland layer-shell overlay + Vulkan WSI；分数缩放；Wayland 输出枚举/热插拔；XWayland 混合（X11 捕获 + Wayland overlay + 转发）；原生全屏 portal/PipeWire 捕获 + 输入穿透；GlobalShortcuts 热键；CLI 文档 | 嵌套合成器、窗口化原生输入、原生焦点自动跟随、HDR、零拷贝、跨屏拼接 |
| **M2b** | 0.5.0 | 嵌套合成器 | 单输出私有 sway；`ext-image-copy-capture` 按 toplevel 抓帧；`wlr-virtual-pointer`/虚拟键盘注入；任意分辨率/宽高比；弹窗/启动器/Lutris；窗口识别；原生 Wayland 宿主窗口 | HDR、零拷贝；β2 指针锁/raw 推迟到 v1.0 后 |
| **M2c** | 0.6.0（不阻塞 v1.0） | 原色彩与性能收尾 | 嵌套 toplevel DMA-BUF 零拷贝（shm 回退）；HDR 桌面下 SDR 正确性；可选 10-bit 宿主输出实验。**2026-11-13：两项均推迟到 v1.0 后，可选** | 完整 HDR 游戏捕获；混合 GL+Vulkan；跨屏拼接；非嵌套 raw input；portal 零拷贝；XWayland DRI3+fence 撕裂 |

依赖：

```text
M1c (0.3.0)
  └─ M2a (0.4.0)  Wayland 呈现/捕获核心
       └─ M2b (0.5.0)  嵌套合成器（依赖 M2a 的 overlay/呈现/输入框架）
            └─ M3 → v1.0
                 └─ M2c（可选，v1.0 后；依赖 M2b 的抓帧路径）
```

---

## 5. M2a · Wayland 平台核心

### 5.1 目标

- X11 会话保持现有行为不回归。
- Wayland 会话下：
  - XWayland 窗口使用 **X11 捕获 + Wayland layer-shell overlay + 指针转发**（保住现有 `--window` 体验和视觉小说输入）。
  - 原生 Wayland **全屏/覆盖输出**窗口使用 portal + PipeWire 捕获 + 输入穿透；切到其他程序不自动收起（M2b）。
  - 多显示器、热插拔、每屏缩放/旋转、负坐标可用。
  - 全局热键在 Wayland 下可用（GlobalShortcuts；KDE 首次授权；若存成 `none` 需系统设置手动分配）。
- 提供 CLI/配置/UI 的 backend/capture/input 选择；`docs/cli.md` 完整说明。

### 5.2 架构

引入最小后端抽象，但**不重写 X11 路径**：

```text
Engine
  ├─ ICaptureSource        x11: XShmGetImage       wayland: portal + PipeWire
  ├─ IPresentTarget        x11: override-redirect  wayland: layer-shell + Vulkan WSI
  ├─ IInputRoute           x11: XSendEvent         wayland: wl_pointer + XSendEvent / passthrough
  ├─ IOutputSet            x11: XRandR             wayland: wl_output + xdg_output
  └─ IToplevelWatcher      x11: EWMH list          native: portal/stream + compositor protocols
```

实现策略：

- `UpscaleSession` 的共享逻辑（可见性状态机、FrameDiff、FrameStats、LayoutCalculator、EffectRegistry）保留。
- 新增 `WaylandUpscaleSession`（或 `WaylandEngine`）作为 peer implementation，复用共享组件；X11 `UpscaleSession` 不动，降低回归风险。
- `VulkanBlitter` 增加 Wayland surface 构造；其余上传/effect/Lanczos 路径复用。
- 新增目录：
  - `Core/Platform/Linux/Wayland/`：Wayland 连接、输出枚举、layer-shell overlay、Wayland surface。
  - `Core/Platform/Linux/Portal/`：ScreenCast D-Bus、pipewire fd、restore token。
  - `Core/Platform/Linux/PipeWire/`：PipeWire 客户端（或 spike 期 GStreamer 桥）。
  - `Core/Engine/`：`WaylandUpscaleSession`、共享输入/布局辅助。

### 5.3 Wayland 呈现

- 每个输出一个 `zwlr_layer_shell_v1` surface：
  - `layer=overlay`
  - 四边 anchor
  - `exclusive_zone=-1`
  - `keyboard_interactivity=none`
  - `set_size(0,0)`，由 compositor configure
- 输入区域：
  - XWayland 窗口化：内容矩形（接收后 `XSendEvent` 转发）。
  - 原生全屏：空 region（输入穿透）。
- 分数缩放：
  - 绑定 `wp_fractional_scale_manager_v1`、`wp_viewporter`、`zxdg_output_v1`。
  - layer-shell configure 的 width/height 是 surface-local（逻辑）尺寸；用 output 的 logical/physical 尺寸计算 scale。
  - buffer 用物理像素；`wp_viewport.set_destination(logicalW, logicalH)`；Vulkan swapchain extent = 物理尺寸。
  - 150% HDMI：逻辑 2560×1440 → 物理 3840×2160；100% DP：1800×2880 → 同尺寸。
- show/hide：
  - hide：`wl_surface.attach(null)` + commit，input region 置空。
  - show：下一次 Vulkan present 重新 attach；恢复时强制整帧。
- 热插拔：
  - `wl_registry.global/global_remove` 更新输出列表。
  - 当前输出消失 → 按名称回退/重新选屏；重建 overlay/swapchain。
- 多屏：
  - 每屏独立 layer surface + swapchain；overlay 只覆盖选中屏。
  - XWayland 源用 XRandR 物理矩形选屏，再按名称匹配 Wayland output（XRandR 名称与 wl_output name 通常一致）。
  - 原生 Wayland 源按 portal stream/输出选择；无法确定时用用户选屏/主屏。

### 5.4 XWayland 混合

- 捕获：保持现有 `X11Capture`（XShm）+ XDamage + 脏矩形。
- 呈现：Wayland layer-shell overlay + Vulkan Wayland WSI。
- 输入：
  - overlay 接收 `wl_pointer` motion/button/axis；坐标从逻辑转物理（按 scale）后走现有 `CoordinateMapper` → 源窗口坐标 → `XSendEvent`。
  - 键盘不转发；`keyboard_interactivity=none` 保持游戏焦点。
  - 指针进入/离开内容矩形时补发 `EnterNotify/LeaveNotify`（复用现有 `X11Input` 逻辑）。
- 生命周期/弹窗/焦点：继续用现有 X11 `X11WindowProbe` + `X11WindowEnumerator` + `OverlayVisibility`；overlay 是 Wayland surface，但源窗口仍是 X11，生命周期判断不变。
- 这是 M2a 的默认 Wayland 会话路径；它解决 overlay 输入/焦点问题，但**不解决 XShm 捕获撕裂**（撕裂的最终方案推迟到 v1.0 后，见 [post-v1.0.md](post-v1.0.md) §5）。

### 5.5 原生 Wayland 捕获（portal + PipeWire）

- Portal `org.freedesktop.portal.ScreenCast` v6：
  - `CreateSession` → `SelectSources`（window/monitor、`cursor_mode=hidden`、`persist_mode=2`、`restore_token`）→ `Start` → `OpenPipeWireRemote`。
  - `restore_token` 单次使用：每次 Start 后保存新 token；失效则重新弹系统选择器。
  - 使用 `pipewire-serial` + `PW_KEY_TARGET_OBJECT` 定位流。
- PipeWire：
  - 产品路径：P/Invoke `libpipewire-0.3`，`pw_thread_loop` + `pw_stream`。
  - 优先 `SPA_DATA_MemFd`（CPU 映射，复用现有上传/effect 路径）；DMA-BUF 作为 v1.0 后可选 fast path。
  - 支持格式/尺寸变化、`spa_meta_region` damage、流错误/关闭。
  - Spike/回退：可先用 GStreamer `pipewiresrc` 验证流格式，但产品路径不依赖 `gst-launch` 子进程。
- 源选择：
  - 原生 Wayland 没有通用窗口枚举；必须走 portal 系统选择器。
  - UI 增加“Wayland 窗口（系统选择器）”；CLI 增加 `--portal-window`。
  - `restore_token` 成功后尽量免打扰；失败给出明确提示和重新选择入口。
- 输入：全屏源用空 input region 穿透；窗口化原生源在 M2a 不做输入映射（M2b 嵌套解决），UI 明确提示。
- 源关闭/流错误 → 停止会话并复位 UI。

### 5.6 热键

- 优先 `org.freedesktop.portal.GlobalShortcuts` v2：
  - CreateSession → BindShortcuts（`toggle_overlay`，preferred trigger 来自配置）→ Activated 信号。
- 失败/不支持：
  - 记录日志；UI 显示“全局热键不可用”；继续用 XWayland `XGrabKey` 作为 best-effort 回退（仅 XWayland 焦点时可靠）。
  - 不静默失效。

### 5.7 CLI / 配置 / UI

新增/扩展：

```text
--backend auto|x11|wayland
--capture auto|x11|portal
--input auto|passthrough|forward
--portal-window                 # 原生 Wayland：打开系统选择器
--monitor <name>
```

配置：

```toml
[capture]
backend = "auto"     # auto | x11 | wayland
mode = "auto"        # auto | x11 | portal

[input]
mode = "auto"        # auto | passthrough | forward
```

- `auto` 规则：
  - X11 会话 → X11。
  - Wayland 会话 + XWayland 窗口 → X11 捕获 + Wayland overlay + forward。
  - Wayland 会话 + 原生窗口 → portal + passthrough。
- UI：
  - 显示当前 backend/capture/input 模式；
  - “Wayland 窗口（系统选择器）”入口；
  - 输出列表来自后端；
  - portal 授权/token 状态；
  - 不支持的组合明确提示。
- Profile：保留 `class/exe`；新增 `app_id`（原生 Wayland）字段（M2a 先解析，匹配能力按 portal/协议可用性逐步补齐）。

### 5.8 验收标准

1. KDE Wayland 下，XWayland 视觉小说窗口化：Wayland overlay 正常显示，鼠标点击/滚轮/拖拽/弹窗/焦点/最小化/关闭游戏/热键不回归。
2. 原生 Wayland 全屏窗口（vkcube/mpv）经 portal/PipeWire 捕获、超分、呈现；输入穿透（不抢焦点）。
3. 4K/150%（HDMI）与 1800×2880/100%（DP）双屏：位置、比例、输入映射正确；输出热插拔 1 秒内跟随。
4. 静止画面不重复 capture/present；动态画面无明显撕裂（原生 Wayland 由 PipeWire 合成帧保证）。
5. 纯 X11 会话：103/103 测试 + 主要路径冒烟不回归。
6. 构建 0 warning / 0 error；新增单元测试通过。
7. `docs/cli.md`、compatibility/design/roadmap 更新；版本 `0.4.0`。

### 5.9 实现状态（2026-11-07）

已实现并冒烟：

- 最小后端抽象：`IOverlay` / `OverlayBackend`；`VulkanBlitter` 增加 Wayland surface 构造，X11 路径保持。
- Wayland layer-shell overlay + Vulkan Wayland WSI；150% 分数缩放；HDMI 4K 与 DP 竖屏（旋转/100%）实测。
- XWayland 混合：X11 捕获 + Wayland overlay + `wl_pointer` → `XSendEvent`；弹窗隐藏/恢复、静止画面、输入（uinput 指针）冒烟。
- 多屏/输出选择：按 X11 monitor 名称匹配 `wl_output`；当前输出消失时回退到另一块屏。
- CLI/config：`--backend auto|x11|wayland`、`--input auto|passthrough|forward`、`--capture auto|x11|portal`（`--window` 路径仍回退 X11；原生窗口用 `--portal-window`）；`[capture]`、`[input] pointer_mode`。
- **portal + PipeWire 原生 Wayland 捕获（2026-11-07 验证）**：
  - `--portal-window` / `--portal-monitor`，Tmds.DBus.Protocol + 生成的 ScreenCast/Request/Session 代理。
  - KDE 系统选择器**默认每次启动都弹出**；`portal.token` 仍轮换保存，只有显式 `--portal-reuse-token` 才复用它跳过选择器。
  - PipeWire fd + serial → `gst-launch pipewiresrc` → 专用 frame pipe（避免 `-v` caps 日志污染帧）→ BGRA 帧。
  - caps 从 gst `-v` stdout 解析（KDE 窗口流可能没有 `size` 属性）。
  - 实测 vkcube 640×360 窗口：`pipewire caps 960x582`（150% + 窗口装饰）、60–69fps、GPU 59–68%、dest=3563×2160。
  - 已知：KDE 窗口捕获包含标题栏/阴影；M2a 的正式验收以全屏/无边框原生窗口为准，窗口化装饰裁剪留后续。
  - **重要限制**：KDE 的窗口流是**按输出 scale 合成后的画面**。720p 全屏游戏在 4K/150% 输出上，`pipewire caps` 是 3840×2160，不是 720p；对它做 2× 会得到 8K 中间纹理（实测 VL 10–12fps、VRAM 7.6GB、GPU 100%），且没有真实超分收益。M2a 已加保护：源尺寸 >= 输出尺寸时自动改用 bilinear 1×，避免 8K；真正的低分辨率原生源需要 M2b 嵌套合成器或改用 XWayland 窗口捕获。
- **2026-11-08 修复与 GlobalShortcuts**：
  - 修复 layer-shell 从隐藏恢复时的崩溃和层级丢失：`attach(null)` 会让 KWin 发 `closed` 并把 layer 重置为 Bottom；改为不 unmap，隐藏时移到屏幕外，显示时重新锚定并声明 Overlay。XWayland 弹窗/焦点恢复、原生 portal 收起/显示均稳定且保持最上层。
  - 接入 portal GlobalShortcuts v2 + `org.freedesktop.host.portal.Registry`；自动写 user-local desktop entry；XDG trigger 使用 `ALT+SHIFT+A`。
  - 原生会话支持快捷键/托盘手动显示隐藏；原生焦点自动跟随仍不在 M2a。
- 自动化：构建 0 warning / 0 error；测试 103/103（M2a 当时；M2b 收尾为 146/146）。

未完成（M2a 剩余）：

- 原生焦点自动跟随（portal 不提供源窗口身份/焦点；M2b 嵌套合成器）。
- 窗口化原生捕获的装饰裁剪；输出热插拔真实拔插回归；嵌套/原生窗口化输入（M2b）。
- 真实视觉小说手测与 M2a 验收记录。

### 5.10 实现顺序

1. 文档定稿（本文件 + roadmap/design/compatibility/backlog/cli/HANDOVER）。
2. 后端抽象 + X11 接回。
3. Wayland 连接/输出枚举 + layer-shell overlay + Vulkan Wayland WSI。
4. XWayland 混合（X11 捕获 + Wayland overlay + 输入转发）。
5. 多屏/分数缩放/热插拔。
6. portal/PipeWire 原生捕获。
7. GlobalShortcuts 热键。
8. CLI/config/UI。
9. 构建/测试/手工冒烟/验收记录。

### 5.11 风险

- layer-shell 在 KWin 上盖全屏游戏、input region/键盘交互行为需实测。
- 150% 分数缩放 + viewporter + Vulkan swapchain 的物理像素对齐。
- portal `restore_token` 单次使用、窗口选择体验、流格式（memfd vs dmabuf）。
- PipeWire P/Invoke/线程生命周期；GStreamer 回退不作为产品依赖。
- 原生 Wayland 窗口化输入在 M2a 不解决（M2b 嵌套）。
- XWayland 捕获撕裂仍在（最终方案推迟到 v1.0 后）。

---

### 5.12 M1b 与 M2a 呈现路径差异（回归判断）

- M1b/M1c：X11 override-redirect overlay；隐藏 `XUnmapWindow`，显示 `XMapRaised`。没有 layer-shell，因此不会跨屏，也不会有 layer 被重置的问题。
- M2a 默认：Wayland `zwlr_layer_shell_v1` overlay + Vulkan Wayland WSI。KWin 在 layer surface unmap 时会发 `closed` 并把 layer 重置为 Bottom，所以不能照搬 `XUnmapWindow`。
- 当前 M2a 隐藏：不 unmap，移到整个逻辑桌面右侧之外；显示恢复 anchor 并声明 Overlay。不要改回“当前输出左侧”或 `attach(null)`。
- 对照 M1b：`--backend x11` 仍走 X11 overlay。

---

## 6. M2b · 嵌套合成器（M2b 阶段实施）

### 6.0 实施状态（2026-11-08 18:54；2026-11-13 更新）

- **2026-11-12**：M2b 已按“单输出 + toplevel 抓取”路线完成**主路径**开发与验收
  （`m2b-single-output` 分支，验收快照 `v0.4.2`；正式里程碑仍为 `0.5.0`）。α/β1 的原拆分被这条
  路线取代，下文保留为历史计划。验收结论与剩余已知问题见 [roadmap.md](roadmap.md) 的 M2b 小节和
  `开发机归档/M2b-新路线-验收问题分组-20261111.md`。
- **2026-11-13：β2 推迟到 v1.0 后**。计划里的 β2（XWayland 指针锁/raw + 示例视觉小说 C）没有实现、
  没有验收；v1.0 前不开发，记录为已知问题，详见 [post-v1.0.md](post-v1.0.md) §1。git 里的
  `97dbbf2 M2bβ2: minimize mirroring...` 是旧 β1 路线验收修复，不是本计划的 β2。
- M2a 已完成并推送 `cd9d9b8`；M2bα 第一版功能已实现（历史记录）。
- **B0 已通过**：headless sway 1.12、screencopy、virtual pointer、虚拟键盘、`swaymsg get_tree`。
- **B0.5 已完成主要验证**：固定 sway 1.12 构建成功；虚拟指针无 XInput2 raw；nested XWayland XTest 能产生 raw（**仅 XI2 事件级探针，不是产品路径，也没有游戏验收**）；uinput/libinput 因 libseat 被 KDE 会话占用而不可直接用。
- **最终架构**：所有新启动游戏默认进 nested sway；旧 M2a X11/portal 保留回退。M2b 拆为 **α（嵌套平台+2D+动态分辨率）/ β1（patched sway+指针锁定/relative）/ β2（raw+示例视觉小说 C）**；其中 α/β1 已并入单输出路线，β2 未实现，v1.0 后处理。
- **分辨率策略**：nested 输出跟随游戏 `geometry`，不固定 1280×720；4:3/16:9/多分辨率动态 resize。
- **raw 策略（现状）**：XWayland 仍是 `XWarpPointer` 绝对注入；`XTestFakeRelativeMotionEvent` 未接产品路径；原生 Wayland 有 virtual pointer relative；**禁止宿主 XTest**（会触发 KDE EIS 授权）。raw/指针锁推迟到 v1.0 后，见 [post-v1.0.md](post-v1.0.md)。
- 详细计划和证据见 `开发机归档/M2b-αβ-实施计划.md`、`开发机归档/M2b-开发记录.md`；原 B0/B1–B5 交接见 `开发机归档/M2b-开发交接.md`。
- 用户约定：M2b 只做主路径，不创建 goal，用 plan/todo；不做 promote/改源窗口状态。

### 6.1 目标

解决“窗口化原生 Wayland 游戏输入”：不改源窗口状态、不做 promote、不移动真实光标、raw input/指针锁定可用。做法是把游戏放进一个 **私有嵌套 Wayland 合成器**（sway），主程序抓它的输出并注入输入。

> **2026-11-13 状态**：raw input/指针锁定**未实现**，只完成了主路径（2D/VN 输入、原生 Wayland
> relative、弹窗/缩放/宿主）。XWayland 的指针锁/raw 推迟到 v1.0 后，见 [post-v1.0.md](post-v1.0.md) §1。

> **历史计划说明（2026-11-13）**：§6.2–§6.6 是最初的“每游戏一个虚拟输出 + 整输出抓取”方案，
> 保留作为设计背景。**当前实现以 §6.0 和 [roadmap.md](roadmap.md) 为准**：单 headless 输出、
> `ext-image-copy-capture` 按 toplevel 抓取、每窗口一个原生 Wayland 宿主、XWayland 绝对注入。
> 下文中的“输出 = 客户区尺寸”“抓整个嵌套输出”“screencopy”等描述已不是现状。

### 6.2 架构

```text
GemPrismUpscaler（KDE 侧）
  ├─ layer-shell 全屏 overlay + Vulkan 超分（复用 M2a）
  ├─ 隐藏/ headless sway（源分辨率）
  │    └─ 游戏（原生 Wayland / XWayland）
  └─ Nested helper（C# + NWayland.Protocols.Wlr）
       ├─ wlr-screencopy / wlr-export-dmabuf → 主程序
       └─ 主程序指针/键盘 → wlr-virtual-pointer / 虚拟键盘 → 游戏
```

实现要点：

- **sway 私有实例**：`WLR_BACKENDS=headless` 优先（不受 KWin 遮挡/节流、无任务栏窗口）；NVIDIA headless 渲染失败时回退隐藏嵌套窗口（`WLR_BACKENDS=wayland`）。
- **输出尺寸**：sway 输出 = 游戏主窗口客户区尺寸（1280×720、1024×768、1920×1080…），强制全屏、`border none`、`gaps 0`。
- **捕获**：helper 连接 sway 的 Wayland socket；`wlr-screencopy`（优先 DMA-BUF，先 shm 回退）；帧通过 Unix socket/共享内存送给主程序。
- **输入**：
  - 指针：overlay 接收真实指针；helper 用 `wlr-virtual-pointer` 注入绝对/相对坐标；嵌套内 1:1，真实光标不动。
  - FPS/指针锁定：overlay 用 `zwp_locked_pointer_v1` + relative pointer；helper 注入相对位移。
  - 键盘：headless 时 overlay 捕获并虚拟键盘转发；隐藏窗口时可由 sway 窗口直接收键盘。虚拟键盘绑定可能需要补 NWayland 协议或调用 `wtype`，在 M2b spike 决定。
- **不改变源窗口**：游戏不在真实桌面开窗；真实桌面只看到 overlay（headless 时连 sway 窗口都没有）。
- **限制**：必须由 GemPrism/启动器把游戏启动到嵌套 session；不能接管已运行窗口。

### 6.3 主窗口识别与弹窗（已确认方案）

信号：

- Profile 显式匹配（app_id/class/title/exe/pid）最优先。
- 会话 token（wrapper 设置 `GEMPRISM_NESTED_TOKEN`；读 `/proc/<pid>/environ`）+ PID/进程树。
- sway IPC `get_tree`：`rect`/`window_rect`、`app_id`、`name`、`pid`、`shell`、`window_properties`（X11 class/instance/title/transient_for/window_type）、`floating`、`fullscreen_mode`、`focused`、`marks`。
- transient/window type 排除对话框；Wayland xdg-dialog/parent 若暴露则用。
- 尺寸/创建顺序/焦点做兜底；歧义时 UI 选择主窗口，写入 Profile。

策略：

- 只允许一个主窗口 fullscreen；其他新窗口默认 `floating`；`popup_during_fullscreen smart`。
- 对话框出现不改嵌套输出尺寸；只有主窗口候选变化（启动器→游戏、分辨率变化）才 resize。
- 抓帧永远抓整个嵌套输出，主窗口识别只影响尺寸/布局/切换；识别失败兜底为固定输出 + 手动选择。
- 4:3/非 720p：嵌套输出 = 主窗口客户区尺寸；最终 fit/stretch 走现有 LayoutCalculator；输入映射按内容矩形。

### 6.4 启动器 / Lutris

- `--nested -- <启动器/游戏>`：启动器和子进程继承嵌套环境；适用于启动器自己 spawn 游戏。
- `--nested-session` + `gemprism-nested-run` wrapper：GemPrism 起会话并写出 `WAYLAND_DISPLAY`/`DISPLAY`/token；Lutris 用 per-game 环境变量或 wrapper 把最终游戏进程送进 sway。
- 如果启动器通过已有 Steam/Lutris/D-Bus 转发游戏：把嵌套环境/token 配到**最终游戏**的启动选项（Steam `%command%` wrapper）。
- Steam/Heroic：文档化 best-effort；Steam overlay/反作弊行为记录，不承诺。
- 已运行窗口：不支持接管；必须重启/由启动器启动。

### 6.5 验收标准

1. 1280×720（16:9）、1024×768（4:3）、1920×1080 三种源在嵌套模式下捕获/超分/输入正确。
2. 无光标跳变；指针点击/滚轮/拖拽/键盘正确；原生 Wayland relative 可用。
3. 启动器 → 游戏切换、对话框显示/输入、关闭/退出正常。
4. Lutris per-game env/wrapper 实机通过；Steam/Heroic 文档化。
5. 静态画面不重复 present；性能/延迟记录（不做硬门槛）。
6. sway 缺失/helper 崩溃有明确错误与清理。

> **不在 M2b 验收范围**：XWayland 的指针锁/raw input、示例视觉小说 C X11 镜头。它们原属 β2，
> v1.0 前不开发，记录在 [post-v1.0.md](post-v1.0.md) §1。

### 6.6 风险

- sway headless + NVIDIA 渲染器；KWin 对隐藏嵌套窗口的节流。
- 窗口识别误判（启动器/大对话框）；靠 token/Profile/手动兜底。
- Lutris/Steam 环境变量被清洗；启动器转发；反作弊/overlay。
- 虚拟键盘绑定/键码/修饰键；Wayland xdg-dialog/parent 字段暴露情况。
- 多一层合成器的延迟/CPU；4K/高帧率性能。

---

## 7. 原 M2c · 色彩与性能收尾（v1.0 后可选，不阻塞 v1.0）

> **2026-11-13 决策**：v1.0 已能正常玩视觉小说，HDR 桌面下 SDR 也未观察到发灰；因此 M2c 仅剩的
> 两项（嵌套 DMA-BUF 零拷贝、SDR 色彩准确性）也列为已知问题，**v1.0 前不做**，v1.0 后作为
> 可选优化。完整 HDR、混合 GL+Vulkan、跨屏拼接、非嵌套 raw、portal 零拷贝、XWayland
> DRI3+fence 撕裂同样推迟。记录在 [post-v1.0.md](post-v1.0.md)，实现资料见
> [m2c-plan.md](m2c-plan.md)。

### 7.1 嵌套 toplevel DMA-BUF 零拷贝（v1.0 后可选）

- `ToplevelCapture` 接 `ext-image-copy-capture` 的 `dmabuf_device`/`dmabuf_format`；用 GBM 在
  compositor 指定的 DRM 设备上分配 dmabuf，包成 `wl_buffer`，缓冲池 2–3 个。
- Vulkan 通过 `VK_EXT_external_memory_dma_buf` + `VK_EXT_image_drm_format_modifier` 导入，
  直接作为 effect 的 source；跳过 CPU 回读/格式转换/整帧上传。
- 4K 源目标 ≥50fps，CPU capture <2ms/帧；720p/800p 视觉小说仍走原路径，不回归。
- 回退：无 dmabuf 格式、导入失败、跨 GPU、同步异常时自动回 shm，并记录原因。
- 脏矩形第一版不适用于 dmabuf（没有 CPU 像素）；先全帧，后续用 compositor damage 或 GPU hash。

### 7.2 色彩（v1.0 后可选回归）

- **现状**：继续精确选 `B8G8R8A8Unorm` + `SRGB_NONLINEAR`，HDR 桌面下 SDR 已实测正常；
  colorspace 不精确时记录并回退，不静默用错。
- **可选实验**：KWin 宿主提供 `wp_color_manager_v1` v3；若 Vulkan WSI 暴露 10-bit/HDR colorspace，
  可加 10-bit swapchain + SDR→PQ tone map，不作为 v1.0 门槛。
- **v1.0 后**：把“SDR 正确性”作为回归项，不新增功能。
- **不做**：嵌套内完整 HDR 游戏捕获。嵌套 sway 不 advertise `wp_color_manager_v1`，`HEADLESS-1`
  是 SDR、无 HDR metadata；给嵌套补 HDR 是独立项目。

### 7.3 推迟到 v1.0 后（原 M2c 也不做）

- 混合 GL+Vulkan：当前 effect 全是 SPIR-V compute，倾向 no-go；无 GL 独占需求不投入。
- 跨屏拼接：每屏独立 overlay 已够用；不做连续拼接。
- 非嵌套 X11 raw input：与 β2 同源，uinput/libei 不适合 v1.0 默认路径。
- XWayland 撕裂最终方案：v1.0 主路径是嵌套 + toplevel 抓取，不走 XShm；旧 `--window` 路径
  继续记录为已知限制，DRI3+fence/portal 方案推迟。
- portal/PipeWire 零拷贝：portal 继续 `gst-launch` CPU 路径，保证可用；原生 PipeWire + dmabuf
  推迟。

### 7.4 v1.0 后验收与顺序（当前不排期）

1. 4K 源 dmabuf vs shm：帧率、CPU capture、GPU effect 分段对比；画面 `AE=0`；无撕裂。
2. 回退路径：强制关闭 dmabuf 时 shm 正常；resize/格式变化不崩。
3. SDR/HDR 桌面回归；720p/800p 视觉小说主路径不回归。
4. 构建 0 warning / 0 error，测试通过。
5. 顺序：先 spike 确认 wlroots 的 dmabuf 格式可导入 → 抓帧抽象/GBM 池 → Vulkan 导入 →
   同步/性能 → 色彩保护 → 测试/文档。详见 [m2c-plan.md](m2c-plan.md)。

---

## 8. 分辨率与宽高比（M2a/M2b 共用）

- 源尺寸不是固定 720p；支持 1280×720、1024×768、1920×1080、800×600、16:10/超宽/竖屏（超宽/4K 源性能在 v1.0 后评估）。
- 最终呈现复用 `LayoutCalculator`：
  - 1024×768 → 2× 2048×1536 → Lanczos 到 2880×2160（4K 16:9 下左右各 480px 黑边）。
  - 1280×720 → 2× 2560×1440 → 3840×2160。
  - 1920×1080 → 2× 3840×2160 → 1:1；源 ≥ 输出时跳过/降级 2×，避免 8K 中间纹理。
  - 竖屏 1800×2880：fit 1800×1012/1350 等，按源比例。
- 输入映射：overlay 局部 → 内容矩形 → 源像素 → 注入；黑边不转发。
- 尺寸变化：源窗口/嵌套输出 resize → 重建 capture/swapchain → 重算内容矩形 → 强制重绘。
- 4:3 不做额外拉伸；由 fit/stretch 决定黑边/铺满。

---

## 9. 验收与测试矩阵（M2a）

自动化：

- `dotnet build GemPrismUpscaler.slnx -c Release`：0 code warning / 0 error。
- `dotnet test GemPrismUpscaler.slnx -c Release`：现有 146/146 + 新增测试。
- 新增测试建议：
  - Wayland 输出 scale/physical/logical 计算；
  - 4:3/16:9/竖屏内容矩形与输入映射；
  - backend/capture/input 选择规则；
  - portal token 存储/失效回退（如果 M2a 实现 portal）；
  - CLI 参数解析。

手工冒烟（KDE）：

- XWayland 视觉小说（示例视觉小说 A / B）窗口化：overlay/输入/弹窗/焦点/最小化/关闭。
- 原生全屏 vkcube/mpv：portal 选择器、捕获、超分、输入穿透、退出。
- 4K/150% HDMI + 1800×2880/100% DP：位置/比例/输入；热插拔。
- 静止画面 0 capture/0 present；动态画面 fps/CPU/GPU 记录。
- 纯 X11 会话：现有主要路径冒烟。

证据：

- 日志、截图、`--version`、构建/测试输出保存 `开发机归档/m2a-*`。
- 用户手测清单 `开发机归档/M2a-用户验收步骤.md`；开发记录 `开发机归档/M2a验收记录.md`。

---

## 10. 依赖与构建

- .NET 10.0.114；Avalonia 12.1.4。
- 新增 NuGet（计划）：`NWayland` 0.11.0、`NWayland.Protocols.Wlr` 0.11.0（与 Avalonia.Wayland 12.1.4 的 NWayland 0.11.0 对齐）。
- Wayland 运行库：`libwayland-client`、`libxkbcommon`（系统已有）。
- 构建期：`/usr/share/dbus-1/interfaces/org.freedesktop.portal.{ScreenCast,Request,Session}.xml`（Tmds.DBus.Generator 生成代理；由 xdg-desktop-portal 包提供，后续可 vendor）。
- Portal/PipeWire：`xdg-desktop-portal` 1.22.1、`xdg-desktop-portal-kde` 6.8.2、PipeWire 1.6.10（系统已有）。
- Vulkan：`VK_KHR_wayland_surface`、`VK_KHR_surface`、`VK_KHR_swapchain`；DMA-BUF 扩展（v1.0 后可选）。
- M2b：`sway`（用户手动安装）；`NWayland.Protocols.Wlr` 的 screencopy/virtual pointer；虚拟键盘绑定待定。

---

## 11. 版本、提交与交接

- M2a：`0.4.0`，提交信息前缀 `M2a:`；tag `v0.4.0` 由用户 push（agent 不 push）。
- M2b：`0.5.0`，`M2b:`；正式 tag `v0.5.0` 已在 M2b 范围收口时打（单输出主路径）。此前的
  验收快照 `v0.4.2` 不是正式里程碑。
- M2c：原 `0.6.0`，**不阻塞 v1.0**；v1.0 前不打 tag，两项作为 v1.0 后可选优化，资料见 §7 与
  [m2c-plan.md](m2c-plan.md)。
- v1.0 后项：见 [post-v1.0.md](post-v1.0.md)。
- 交接文件：
  - `docs/dev/m2-plan.md`（本文件）：范围/决策/验收。
  - `docs/dev/roadmap.md`：阶段摘要。
  - `docs/dev/architecture.md`：架构。
  - `docs/compatibility.md`：支持矩阵/限制。
  - `docs/cli.md`：CLI 使用。
  - `开发机归档/HANDOVER.md`：当前状态/下一步。
  - `开发机归档/M2a-用户验收步骤.md` / `开发机归档/M2a验收记录.md`：验收。
- 工作区/安全：不修改 `/etc`、btrfs/snapper、驱动、KWin/kscreen、游戏文件；不使用 sudo。

---

## 12. 未决/待 spike

- **M2c 首要 spike（v1.0 后可选）**：本地 sway 1.12/wlroots 0.20 通过
  `ext-image-copy-capture` 实际 advertise 的 `dmabuf_device`/`dmabuf_format` 是否包含 Vulkan
  可导入的 32-bit 格式与 modifier；没有就必须回退 shm，零拷贝范围要重估。
- v1.0 后（原 M2c）：GBM 分配给 dmabuf 的设备匹配（dev_t → render node）、跨 GPU 导入、`ready` 语义与
  显式同步；4K 源的分段性能。
- v1.0 后（原 M2c）：KWin `wp_color_manager_v1` v3 / Vulkan WSI 是否暴露 10-bit HDR colorspace（可选实验）。
- **已归档/推迟**：β2 XWayland 指针锁/raw、示例视觉小说 C X11、portal/PipeWire 零拷贝、混合管线、
  跨屏拼接、非嵌套 raw、DRI3+fence 撕裂；见 [post-v1.0.md](post-v1.0.md)。
- 历史项（已不阻塞）：layer-shell 在 KWin 的堆叠/input region、150% configure、portal token、
  M2b 虚拟键盘/窗口识别字段、Lutris/Steam 环境变量/反作弊。

---

## 13. 参考

- `docs/dev/roadmap.md`：里程碑摘要。
- `docs/dev/m2c-plan.md`：M2c 收敛范围、零拷贝实现与验收。
- `docs/dev/post-v1.0.md`：v1.0 前不做的功能与已知问题。
- `docs/dev/architecture.md`：架构与实现细节。
- `docs/compatibility.md`：支持矩阵/已知限制。
- `docs/dev/effects.md`：自定义 effect ABI。
- `docs/dev/history/zero-copy-spike.md`：DRI3/DMA-BUF 结论。
- `开发机归档/HANDOVER.md`：当前交接。
- `开发机归档/M1c验收记录.md`、`开发机归档/M1c测试报告.md`：M1c 基线。
