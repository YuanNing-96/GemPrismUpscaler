# 宝石棱镜 · GemPrismUpscaler 架构设计

> 本文是 M0–M2b 的设计档案，部分章节仍描述当时的两套后端/旧 UI。
> v1.0 的用户行为以 `docs/` 用户文档为准。

版本 1.0.0（M2b 主路径 + M3 打磨）· 原 M2c（嵌套零拷贝 +
SDR 色彩）不阻塞 v1.0，推迟为 v1.0 后可选优化（[m2c-plan.md](m2c-plan.md)）。v1.0 后项见
[post-v1.0.md](post-v1.0.md)。M1c（设置 / 托盘 / Profile / 自定义 effect / VL）已交付。

---

## 1. 目标与非目标

### 1.1 目标

在 Linux 上提供一个通用的实时窗口超分工具，行为对标 Windows 的 Magpie：

- 对**任意窗口**生效，不要求游戏本身支持高分辨率
- **不注入**目标进程，不修改游戏文件
- 支持**任意源分辨率 → 任意目标分辨率**，两者宽高比可以不同
- 超分算法**可插拔**，用户能自己写着色器
- 在 Wayland 会话下可用

### 1.2 非目标

- 不做帧生成（frame generation），那是另一个问题域
- 不做游戏内覆盖层（overlay HUD），避免与反作弊冲突
- M1a 不做 HDR 色彩管理、完整多显示器、核显与混合显卡；FPS/raw input 放到后续阶段
- 不做视频播放器的超分，mpv 已经有成熟方案

### 1.3 与相邻方案的差异

| 方案 | 局限 |
|---|---|
| gamescope | 必须在启动命令里包一层，无法对已在运行的游戏生效；上采样器固定为 FSR/NIS/SGSR |
| vkBasalt | 注入 Vulkan 层，受 32/64 位、图形 API 限制，且不改变 swapchain 尺寸 |
| Magpie | 仅 Windows |
| 本方案 | 独立进程捕获，不注入，API 无关，算法可扩展 |

---

## 2. 需求

### 2.1 功能需求

| 编号 | 需求 | 优先级 |
|---|---|---|
| F1 | 捕获指定窗口的画面，窗口被遮挡时仍能正确捕获 | M0 |
| F2 | 将画面超分到屏幕分辨率并全屏显示 | M0 |
| F3 | 支持源与目标宽高比不一致（fit / fill / crop / 绝对尺寸） | M0 |
| F4 | 检测目标进程弹出的新窗口，超分画面自动让位 | M0 |
| F5 | 全局热键切换超分开关 | M0 |
| F6 | 按进程名 / 窗口类名自动套用配置 | M1c ✅ |
| F7 | effect 管线可通过配置文件组合，支持自定义 GLSL | M1c ✅（TOML + 预编译 SPIR-V） |
| F8 | 进出超分的缩放动画 | M1c 未做，留 v1.0 前评估 |
| F9 | 脏矩形增量处理，静止画面不重复计算 | M1b |
| F10 | 原生 Wayland 窗口捕获 | M2 |
| F11 | 混合 OpenGL / Vulkan effect 管线 | M2 |

### 2.2 非功能需求

| 编号 | 需求 | 指标 |
|---|---|---|
| N1 | 端到端延迟 | < 16 ms（60 fps 预算内） |
| N2 | 静止画面 GPU 占用 | 接近 0（脏矩形生效时） |
| N3 | 目标进程不受影响 | 零注入、零 API hook |
| N4 | 崩溃安全 | 主进程退出时覆盖窗口必须被销毁，不留"看不见的最上层窗口" |
| N5 | 配置可读性 | TOML，手写可维护 |

### 2.3 明确的行为约定

- 超分开启时，**输入必须完整转发**给原窗口，用户感知不到中间层
- 检测到弹窗时**优先保证用户能操作**，宁可临时关闭超分
- 源窗口尺寸变化时，管线重建而不是拉伸旧画面

---

## 3. 架构

### 3.1 总览

```
┌──────────────────────────────────────────────────────┐
│  Profile 层                                           │
│  按 exe 名 / 窗口类名 / 标题匹配，产出 PipelineConfig   │
├──────────────────────────────────────────────────────┤
│  WindowTracker                                        │
│  监听目标进程的顶层窗口，维护"主窗口 + 弹窗"集合        │
├──────────────────────────────────────────────────────┤
│  CaptureBackend            →  FrameSource             │
│    x11:     XComposite + XDamage + XShm               │
│    wayland: portal + PipeWire (memfd; DMA-BUF 后置)    │
│    nested:  ext-image-copy-capture + wl_shm           │
│              (v1.0 后可选: GBM DMA-BUF → Vulkan)       │
├──────────────────────────────────────────────────────┤
│  Layout 层                                            │
│  源 rect + 屏幕 rect + 宽高比策略 → 每级 pass 的目标尺寸 │
├──────────────────────────────────────────────────────┤
│  EffectPipeline                                       │
│  多 pass，每 pass 声明 backend / 尺寸规则 / 参数        │
│    vk: Vulkan compute                                 │
│    gl: OpenGL（经 DMA-BUF 交接）                       │
├──────────────────────────────────────────────────────┤
│  PresentBackend            →  Surface                 │
│    x11:     override-redirect 全屏窗口 + 空 input shape│
│    wayland: layer-shell overlay + 空 input region (M2)│
├──────────────────────────────────────────────────────┤
│  InputBackend                                         │
│    x11:     XTest / XSendEvent 合成事件               │
│    wayland: 不做转发，overlay 对输入完全透明           │
└──────────────────────────────────────────────────────┘
```

### 3.2 数据流

```
窗口像素
  → 捕获（GPU 或 CPU 纹理）
  → [可选] 色彩空间归一化
  → pass 1 … pass N（每个 pass 可能换尺寸、换后端）
  → 最终重采样到目标 rect
  → 呈现（含黑边填充）
```

### 3.3 线程模型

| 线程 | 职责 |
|---|---|
| UI 线程 | Avalonia 消息循环，仅处理配置界面 |
| Capture 线程 | 等待 X11 事件、读取脏矩形、提交捕获 |
| Render 线程 | 驱动 effect 管线、提交 present |
| Watchdog 线程 | 监控主窗口存活、弹窗状态、自身健康 |

捕获与渲染可以合并为一条线程（M0 建议如此），降低同步复杂度；脏矩形量大时再拆。

---

## 4. 关键设计决策

### 4.1 独立进程而非注入

**决策**：通过窗口系统捕获，不进入目标进程。

**理由**：
- 不区分 32/64 位
- 不挑图形 API（D3D9/11/12、OpenGL、Vulkan 走同一条捕获路径）
- 不会被反作弊误判
- 游戏更新不会导致失效

**代价**：多一次捕获与呈现的拷贝，延迟高于注入方案。实测表明这个代价可以接受（见第 7 节）。

### 4.2 X11 优先，但一份后端覆盖两种窗口

XWayland 窗口在客户端视角就是普通 X11 客户端，XCB 协议完全一致。因此：

- **X11 会话**和**XWayland 窗口**共用同一个捕获后端
- 差异只在窗口坐标换算与呈现层的缩放处理

原生 Wayland 窗口需要完全不同的捕获（portal + PipeWire）与输入策略，作为独立后端放在 M2。

### 4.3 几何模型：照搬 Magpie 的 ScalingType

每个 effect 条目独立声明尺寸策略，而不是全局一个缩放模式：

```toml
[[pass]]
name = "anime4k_upscale"
shader = "shaders/anime4k/Anime4K_Upscale_CNN_x2_M.glsl"
scaling = "normal"     # normal | fit | absolute | fill
scale = [2.0, 2.0]     # 二维，支持非等比

[[pass]]
name = "finish"
shader = "shaders/resample_lanczos.glsl"
scaling = "absolute"
size = [3840, 2160]
```

| 取值 | 含义 |
|---|---|
| `normal` | `scale` 表示相对输入的倍率 |
| `fit` | `scale` 表示相对于屏幕可容纳的最大等比缩放的比例 |
| `absolute` | `size` 表示目标像素尺寸 |
| `fill` | 充满目标区域，忽略 scale |

全局还有 `cropping`（四边浮点裁剪）与 `alignment`（九宫格对齐），用于处理黑边和偏移。

这套模型的表达能力覆盖"任意分辨率任意比例"，且实现成本低。

### 4.4 两级缩放

CNN 类算法（Anime4K）是固定倍率模型，不能让它们直接输出任意尺寸。

**做法**：模型只做整数倍放大（2× 或 4×），最后用一次高质量重采样（Lanczos / EASU）精确落到目标尺寸。

```
1280×720 --CNN 2×--> 2560×1440 --resample 1.5×--> 3840×2160
1024×768 --CNN 2×--> 2048×1536 --resample 1.406×--> 2880×2160（+左右黑边）
```

这样模型永远工作在训练时的倍率上，不会因为非整数倍产生相位伪影。

### 4.5 分数缩放与 overlay 尺寸

本机实测（KWin 6.8.2 + XWayland 24.1.14，150% 缩放 + 一块 100% 竖屏）：

- kscreen 的逻辑几何是 2560×1440 + 1200×1920，但 XWayland root 是 **5640×2880**；
- XWayland 的每块显示器矩形是“逻辑尺寸 × 该屏缩放”，例如 HDMI 2560×1440×1.5 = 3840×2160，位置 +1800+489；DP-1 1200×1920×1.0 旋转后 = 1800×2880，位置 0,0；
- 一个 320×180 的 X11 窗口在屏幕上仍是 320×180，KWin **不会**在合成阶段把 XWayland 窗口整体放大；
- `[Xwayland] Scale=1.5` 是让 X11 应用按 `Xft.dpi=144` 自己处理缩放；不处理 DPI 的原始 Xlib/Wine 窗口保持 1:1。

因此 **M1a 起 overlay 必须按 XRandR 的单块显示器矩形创建**，不能按整个 root，也不能按逻辑尺寸。多屏时选择游戏窗口所在屏（或用户手动指定），overlay/swapchain 只覆盖该屏：

```
1280×720 --CNN 2×--> 2560×1440 --resample 1.5×--> 3840×2160（HDMI）
1280×720 --CNN 2×--> 2560×1440 --resample 0.703×--> 1800×1012（DP-1 竖屏，上下黑边）
```

最后一步由我们做，不是合成器做；不存在“合成器线性放大导致软化”的问题，反而是物理像素 1:1。显示器矩形从 `XRRGetMonitors` 读取，包含每屏的缩放和旋转。100%、150% 和混合缩放都走同一套逻辑。

若以后把 KWin 切成“由系统缩放 XWayland”模式，行为会不同；需要重新实测并做运行时探测。M1a 的前置条件是当前“X11 应用自己缩放”模式。

### 4.6 弹窗让位

这是可用性问题，不是优化项。覆盖窗口盖住游戏的模态对话框会导致用户无法退出。

三层防御：

1. **监控**：M0a 用 200ms 轮询 `_NET_CLIENT_LIST`，用 `_NET_WM_PID` 过滤出目标进程的所有顶层窗口；只把**当前 viewable 且非 `_NET_WM_STATE_HIDDEN`、尺寸 ≥64×64** 的同 PID 窗口算作弹窗，避免被 Wine 的隐藏 helper/IME 窗口永久误判。后续可换 `SubstructureNotify` 事件驱动，并用 XRes 补 `_NET_WM_PID` 缺失的情况
2. **让位**：发现弹窗，立即隐藏覆盖窗口；弹窗关闭后恢复。恢复时必须强制重新捕获+呈现一次，因为 X11 窗口 unmap 后 Vulkan surface 的内容未定义，否则会黑屏到下一次画面变化。不做"把覆盖窗口压到弹窗下面"的尝试，X11 堆叠顺序不可靠
3. **兜底**：主窗口销毁 → 引擎停止并销毁覆盖窗口；主窗口最小化/unmap → 覆盖窗口隐藏，恢复后重新捕获；焦点变化 → 隐藏（见 6.2）；覆盖窗口进程崩溃 → watchdog 确保窗口被销毁

### 4.7 混合管线：接口先行，实现分期

实测跨 API 交接开销约 0.13 ms/次（见第 7 节），性能不是障碍。但工程复杂度是真实成本。

**M0 决策（2026-11-02）**：M0 不做混合管线，效果全部在 Vulkan 中执行（Anime4K S 5 pass + Lanczos-2）；保留 `EffectMode`/后续 backend 扩展点。**2026-11-13 复核**：当前 effect 全是 SPIR-V compute，没有 GL 独占需求，倾向 no-go；v1.0 前不做，推迟到 v1.0 后（[post-v1.0.md](post-v1.0.md) §2）。下面的策略仅作为设计背景保留。

**策略**：

- `FrameHandle` 抽象为"Vulkan image"或"GL texture + fd"二选一
- effect 声明 `backend = "vk" | "gl"`，默认 `vk`
- 调度器按段分组：连续的 GL pass 合并为一段，减少跨界次数
- 默认路径：GLSL 源码经 glslang 编译为 SPIR-V，全部在 Vulkan 中执行
- 只有显式标记 `backend = "gl"` 且依赖 GL 特有能力的 pass 才走 GL

M0 已决定不做混合路径，直接采用全 Vulkan；**M1 仍不做混合着色器管线**。以下前提/路径仅作为 M2 的参考。关键前提：GL 必须与 Vulkan 跑在同一块 GPU 上。本机 XWayland GLX 在 AMD，而 Vulkan 在 NVIDIA，不能直接用 GLX；已验证用 **headless EGL surfaceless context**（NVIDIA）创建 GL 上下文并导出 DMA-BUF（`开发机归档/probe/egltest.c`）。Vulkan 侧导入与同步仍需 spike；若验证不达标，保持全 Vulkan，接口不变。

**跨 API 的两条实现路径**：

| 路径 | 机制 | 适用 |
|---|---|---|
| A | GL 纹理 → `eglCreateImageKHR` → `eglExportDMABUFImageMESA` → Vulkan 导入 | Mesa 上顺畅，部分驱动拒绝从 GL 纹理导出 |
| B | Vulkan 分配可导出 image → `vkGetMemoryFdKHR` → GL `glImportMemoryFdEXT` 导入并渲染 | 兼容性更好，推荐作为默认 |

---

## 5. 技术选型

| 层 | 选型 | 理由 |
|---|---|---|
| 语言 | C# / .NET 10 | 跨平台、单文件分发、开发效率高 |
| AOT | 先 self-contained 单文件；NativeAOT 后议 | M0a 未验证 Avalonia/Silk.NET 的 AOT |
| UI | Avalonia 12.1.4 + Avalonia.Wayland | 原生 Wayland 后端；无 Wayland 时回退 X11 |
| Vulkan | Silk.NET.Vulkan | 需要 external memory 与 drm modifier，高层封装会挡路 |
| OpenGL | Silk.NET.OpenGL（M0b 混合实验） | v1.0 不做混合管线；仅设计背景保留，若将来需要则 headless EGL |
| X11 | 自写 P/Invoke（libX11/libXext/libXdamage） | Silk.NET.X11 覆盖不全；M0 不需要原生 C++ |
| Wayland | `NWayland` 0.11.0 + `NWayland.Protocols.Wlr` 0.11.0 | 与 Avalonia.Wayland 12.1.4 的 NWayland 版本对齐；Wlr 包提供 layer-shell/screencopy/virtual pointer |
| 屏幕共享 | xdg-desktop-portal ScreenCast v6 + PipeWire | 原生 Wayland 无通用窗口枚举/捕获；M2a 用 memfd；portal DMA-BUF 与嵌套 toplevel DMA-BUF 均推迟到 v1.0 后 |
| Shader | M0a 无 shader；M0b GLSL 离线编译为 SPIR-V | 运行时不依赖 glslang（用户自定义 shader 另议） |
| 配置 | TOML | M1 接入；M0a 用 UI/CLI 参数 |
| 原生辅助 | 无 | P/Invoke + Silk.NET 已够；去掉 CMake 依赖 |

### 5.1 关于 Avalonia 的 Wayland 支持

Avalonia 12.1 提供原生 Wayland 后端（需引用 `Avalonia.Wayland` 包），但**没有自动回退**：显式调用 `UseWayland()` 后，在没有合成器的环境中会直接启动失败。正确写法是条件判断：

```csharp
public static AppBuilder BuildAvaloniaApp()
{
    var builder = AppBuilder.Configure<App>().UsePlatformDetect();
    if (OperatingSystem.IsLinux() &&
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null)
    {
        builder = builder.UseWayland();
    }
    return builder;
}
```

界面只负责配置，不参与超分渲染。覆盖窗口是独立创建的原生 X11 窗口，与 Avalonia 的渲染栈互不干扰。

### 5.2 性能约束

- 每帧路径上**零分配**：使用 `Span<T>`、对象池、`stackalloc`
- 避免 GC 抖动，它会直接表现为卡顿
- P/Invoke 调用每帧控制在几十次量级

---

## 6. 实现细节

### 6.1 捕获（X11）

当前路径：

```
XShmGetImage(直接读 client window)
  → XDamage 通知有变化
  → 复制共享内存到 staging
  → 上传 Vulkan image
```

要点：

- KWin 合成会话里窗口已经被重定向，实测 `XShmGetImage` 直接读被遮挡窗口就能拿到窗口自己的内容；**不需要**先 `XCompositeRedirectWindow`。只有在无合成器的原生 X11 会话才需要重定向；若 RedirectWindow 返回 BadAccess 就跳过。
- 窗口最小化时捕获可能返回陈旧帧；检测 `map_state` / `_NET_WM_STATE_HIDDEN` 后暂停。
- 直接捕获 client window 本身就不含边框；`_NET_FRAME_EXTENTS` 只在需要把 client 坐标换算到 frame 坐标时使用。
- XDamage 在 XWayland 下常报全窗口；M1a 用 Damage 做**帧级跳过**。M1b 在帧级跳过之上加 **64px 分块哈希**：XDamage 仍可能每帧触发，但像素不变时不上传、不跑 effect、不 present；局部变化时只上传/重算变化块。
- XShm 回读（720p 约 3.7 MB/帧）。M1b 主路径直接从 shm 指针做哈希和分块上传，不再整帧 `Marshal.Copy` 到托管数组；`XGetImage` 回退保留。
- 脏矩形的 halo 来自 manifest：S=5、M=8、L=5 源像素（L 的并行分支不增加空间深度），再加 2px 安全余量。只上传变化块，但 effect 在“变化块 + halo”上重算。
- DRI3/DMA-BUF 零拷贝 spike 已完成：GPU 窗口线性 1-fd 可导出并在 NVIDIA Vulkan 导入；软件窗口 2-fd/DCC 需要回退。详见 [zero-copy-spike.md](history/zero-copy-spike.md)，M1b 不放进默认链路。
- 窗口位置每 200ms 重新查询，用于自动选屏和跨屏移动；窗口尺寸变化时重建 capture。

### 6.2 呈现（X11）

- 创建 override-redirect 窗口，**位置和尺寸取 XRandR 的单块显示器矩形**（例如 HDMI `3840×2160+1800+489`、DP-1 `1800×2880+0+0`），不再用整个 root。
- 每个显示器独立 Vulkan swapchain；窗口跨屏移动或选屏改变时销毁旧 overlay/blitter 再重建，重建后强制重绘一帧。
- override-redirect 窗口不受 WM 管理，不需要 `_NET_WM_STATE_ABOVE`；实测可以直接盖住 EWMH 全屏窗口。
- 不把 input shape 置空：overlay 要接收真实鼠标再转发（见 6.3）。
- 不把输入焦点留在配置界面：启动会话、热键重新显示、点击覆盖窗口时，通过 EWMH `_NET_ACTIVE_WINDOW`（source indication=2）+ `XSetInputFocus` 把焦点交还游戏。只调 `XSetInputFocus` 会被 KWin 的焦点窃取防护忽略（实测）。
- 设置 `WM_HINTS.input=False`，overlay 自身不参与焦点；`FollowFocus` 默认开启：游戏失去焦点时收起覆盖窗口，回到游戏时恢复。配置界面提供开关，CLI 用 `--no-follow-focus` 关闭。
- 每次重新显示都重新捕获并呈现一帧（unmap 后 surface 内容未定义）；显示时把当前真实指针位置同步给游戏一次，避免悬停状态停留在旧位置。多屏时 root 指针坐标要减去当前显示器原点，再映射到 overlay 局部坐标。
- 不要用 `XFixesHideCursor`（这是整个 X server 级别）；保持系统光标。
- 用 Vulkan swapchain（Xlib surface）呈现。双线性路径用 `vkCmdBlitImage`；Anime4K 路径用 compute + Lanczos graphics pass。
- M1b 起 Anime4K 先写一张**持久 4K 合成图**：整帧时清黑边并全画；局部时 `LoadOp=LOAD`，viewport 保持内容矩形、scissor 限制到脏输出矩形。随后把合成图整图 copy 到当前 swapchain 图像。这样交换链轮转不会丢掉未更新的区域，也不需要 partial present 扩展。
- 覆盖任务栏/面板是预期行为；停止/退出/崩溃时必须销毁窗口。

### 6.3 输入

**不能只靠点击穿透。** 1280×720 的游戏窗口 + 4K overlay：空 input shape 会把屏幕中央的点击透传到原游戏窗口之外，游戏收不到。

当前做法：

- overlay 的 input shape 保持完整，接收真实 `MotionNotify` / `ButtonPress` / `ButtonRelease`；
- 用输出内容矩形（fit 时扣除黑边）反算源窗口客户区坐标；多屏时 overlay 事件本身是窗口局部坐标，只有 `XQueryPointer` 的 root 坐标需要减去显示器原点；
- 通过 `XSendEvent` 向源窗口发送 `MotionNotify`、`ButtonPress`、`ButtonRelease`、滚轮（Button4/5）；不移动真实指针；
- 指针进入/离开内容矩形时补发合成 `EnterNotify` / `LeaveNotify`（`NotifyNormal` / `NotifyNonlinear`）：overlay map 会让真实指针“离开”源窗口，部分引擎/工具包只靠 crossing 事件维护悬停状态；拖拽在内容矩形外松手时仍补发 release，避免按键卡住；
- 键盘不转发：焦点由 6.2 的激活逻辑交还游戏，按键直接到游戏；
- 热键 `Alt+Shift+A` 用 `XGrabKey` 在 root 上抓取；抓取失败要记录并回退 UI 按钮。

XTest 在本机 XWayland 下受 KWin EIS 授权门控（会弹“允许操控输入设备”），且即使授权也要 warp 真实指针，不适合作为常规映射。XSendEvent 不需要权限框。

M1 只支持视觉小说/2D 事件驱动输入；FPS/鼠标视角/raw input 放到后续阶段（见 backlog）。

### 6.4 超分

M0a 当前：

- Vulkan `vkCmdBlitImage` 双线性重采样到目标尺寸（无 shader/pipeline）
- fit / stretch 两种几何；1280×720 → 3840×2160 实测约 60fps

M0b 实际（v0.1.2）：

- **Anime4K `Upscale_CNN_x2_S`**：4 层 3×3 CNN（8 输入通道按正/负半轴拆成两组 4 通道）+ Depth-to-Space 2× 残差。mpv user-shader 格式由 `tools/build_shaders.py` 展开为 5 个独立 Vulkan compute shader，中间纹理 `rgba16f`，SPIR-V 离线编译并作为嵌入资源分发；运行时不依赖 glslang。
- **Lanczos-2 收尾**：4×4 taps，从 2× 结果重采样到最终内容矩形；由 graphics pipeline 直接写入 swapchain，render pass 负责清黑边。这样不需要额外的 4K 中间纹理，也避免 format 不兼容的 `vkCmdBlitImage`。
- **几何**：fit / stretch；fit 时先用源宽高比算出内容矩形，再把 2× 结果映射到该矩形。1024×768 fit → 2× 2048×1536 → Lanczos 到 2880×2160，居中 x=480。
- **算法切换**：`EffectMode.Anime4K`（默认）/ `EffectMode.Bilinear`；UI“超分算法”下拉框，CLI `--effect anime4k|bilinear`。双线性路径保持 M0a 的单次 `vkCmdBlitImage`，用于对比和故障回退。
- **未包含**：FSR1 EASU/RCAS、`Clamp_Highlights`、混合 GL+Vulkan、TOML effect 描述（M1c）。Anime4K S 是效果/性能折中；M/L 变体在 M1b 把管线通用化后接入。

M1b 实际（v0.2.1）／M1c 增量（v0.3.0）：

- **manifest 驱动 N-pass**：`tools/build_shaders.py` 不再假设 5 pass / 固定纹理名，而是从 `//!SAVE` / `//!BIND` / `//!WIDTH` 推出每个 variant 的 pass 图、纹理尺寸和 receptive-field radius，生成 `anime4k_{s,m,l,vl}_pN.comp.spv` + `anime4k_manifest.json`。S=5/6 纹理，M=9/10，L=10/11，**VL=18/19**（L 有并行分支，16 通道层拆成多张 rgba16f）。
- **统一的 push constant**：`vec2 outSize; vec2 regionOffset; vec2 regionSize; vec2 texSize[N]`。整帧 dispatch 全部输出；脏矩形 dispatch 扩展区域；`HOOKED_pos` 仍用全尺寸归一化，保证局部/整帧一致。S/M/L 为 72/104/112 B，仍在 128 B 保证上限内；**VL 为 176 B**，运行时查询 `maxPushConstantsSize`，不足时不启用。构建脚本上限放宽到 256 B。
- **`Anime4KChain`**：按 manifest 分配中间纹理、descriptor set、pipeline；支持多个输入/并行分支；`Record(cmd, IReadOnlyList<RectI>)` 对每个矩形按 pass 输出倍率缩放后 dispatch，所有矩形处理完再发一次输出 barrier。M1c 起它也加载自定义 effect 的外部 `.spv`。
- **算法选择**：`EffectMode.Bilinear / Anime4KS / Anime4KM / Anime4KL / Anime4KVL / Custom`；`anime4k` 是 S 的 CLI 别名；默认 S。UI/CLI 通过 `EffectRegistry` 统一解析 id（内置 + 自定义）。

**M1c 自定义 effect（已实现）**：

- 用户 effect = `effect.toml` + 一个或多个预编译 `.spv`，放在配置目录 `effects/`（支持一层子目录）。
- TOML 描述纹理、pass 图、`inputs`/`output`、`output_scale`、`radius`、`requires_full_frame`；运行时不依赖 glslang。
- ABI v1 固定：descriptor binding 0..N-1 为输入 sampler，binding N 为 rgba16f storage image；push constant 固定 `outSize/regionOffset/regionSize/texSize[N]`；最终统一由 Lanczos-2 收尾。
- 完整字段、坐标/脏矩形规则、样例与校验规则见 [docs/dev/effects.md](effects.md)。

### 6.5 配置与 Profile（M1c，已实现）

配置：`~/.config/GemPrismUpscaler/config.toml`（`GEMPRISM_CONFIG` 可覆盖），schema=1，原子写入，损坏备份回退。

```toml
[general]
second_instance = "notify"   # notify | activate
close_action = "exit"        # exit | tray
follow_focus = true
remember_last = true
auto_refresh_windows = false
refresh_interval_ms = 1000

[hotkey]
toggle_overlay = "Alt+Shift+A"

[logging]
path = ""
level = "info"

[display]
monitor = "auto"

[effect]
default = "anime4k-s"
scaling = "fit"
dirty_rect = true

[tray]
enabled = true

[diagnostics]
developer_mode = false

[[profile]]
name = "galgame"
class = ["game.exe", "Launcher.exe"]
exe = ["*/game.exe"]
effect = "anime4k-l"
scaling = "fit"
monitor = "auto"
follow_focus = true
dirty_rect = true
hotkey = "Alt+Shift+A"
```

- 优先级：CLI 显式参数 > Profile 匹配 > `[last]`（`remember_last`）> 设置默认 > 内置默认。
- Profile 匹配 WM_CLASS、`/proc/<pid>/exe`、Wine cmdline 中的 `.exe`；通配符 `*`/`?`；文件顺序先匹配先赢。
- 选中窗口时自动套用；成功启动后写 `[last]`。

### 6.6 动画（M1c 未做）

M1c 按用户决定**不做动画**，留到 v1.0 前评估。设计意图仍是：覆盖窗口本身保持全屏不动，只对 GPU 输出矩形做插值；动画期间轻量重采样，结束后恢复高质量；弹窗/失焦优先即时隐藏。实现前需要先解决“动画中源窗口被关闭/最小化”的竞态。

---

### 6.7 指标与脏矩形（M1b）

**指标**：

- CPU 分段：capture（XShmGetImage）、diff（分块哈希）、upload（staging 拷贝）、effect（命令录制）、present（提交+present，含 vblank 等待）。
- GPU 分段：Vulkan timestamp query 4 个点（帧开始、上传后、effect 后、命令结束）；`gpuUpload` / `gpuEffect` / `gpuTail`。GPU 时间在下一帧 fence 后读取，报告中最多滞后一帧。
- 系统：NVML 整卡 GPU 利用率/显存；`Process.TotalProcessorTime` 的进程 CPU；每秒平均/p95。
- 静止场景没有帧样本时，报告 `gpu=n/a`，不沿用上一帧的 GPU 时间；`captured` / `presented` 仍反映真实计数。
- 可选 `GEMPRISM_STATS=/path/stats.csv`，表头见 `FrameReport.CsvHeader`。

**脏矩形**：

1. 从 XShm 指针按 64px 分块做 64-bit 哈希，与上一帧比较；尺寸变化/显示重建/恢复时强制整帧。
2. 变化块合并成行 run → 作为 `UploadRects`；只把这些行拷到全宽 staging，`vkCmdCopyBufferToImage` 的 `bufferRowLength` 必须是整帧宽度（否则会按子矩形紧凑行读取，产生横条纹）。
3. `ComputeRects = UploadRects` 按 manifest radius 扩展并合并；effect 对每个区域按 pass 倍率 dispatch。中间纹理只有区域被更新，但区域外仍是上一帧有效内容；halo 保证边界像素的输入有效。
4. `OutputRects = ComputeRects` 映射到内容矩形，加 2px Lanczos 支撑；presenter 用整内容矩形 viewport + 脏矩形 scissor，把结果写进持久合成图。
5. 变化面积 ≥50% 时回退整帧；整帧路径清合成图后全画，局部路径 `LoadOp=LOAD`。
6. `--no-dirty-rect` 关闭分块路径，便于 A/B 和故障回退。

### 6.8 零拷贝（M1b spike）

DRI3 + DMA-BUF 的探针与结论见 [zero-copy-spike.md](history/zero-copy-spike.md)。摘要：GPU 窗口（vkcube）可导出线性 1-fd DMA-BUF，并在 NVIDIA Vulkan 上 `vkBindImageMemory` 成功；软件窗口给出 2-fd/DCC，需要回退 XShm。未做 fence 同步和真实游戏端到端验证，**不进入 M1b 默认链路**。

## 6.9 M2 平台扩展架构

> M2 原拆成 M2a（Wayland 平台核心）、M2b（嵌套合成器）、M2c（色彩/性能）；原 M2c 推迟到 v1.0 后可选。完整范围、决策与验收见 [m2-plan.md](m2-plan.md)。本节只记录架构。

### 6.9.1 后端抽象与兼容策略

M1c 的 `UpscaleSession` 直接 new X11 类，没有实际的 `CaptureBackend/PresentBackend/InputBackend`。M2a 引入**最小后端抽象**，但不重写稳定的 X11 路径：

```text
Engine / WaylandUpscaleSession
  ├─ ICaptureSource      x11: XShmGetImage          wayland: portal + PipeWire
  ├─ IPresentTarget      x11: override-redirect     wayland: layer-shell + Vulkan WSI
  ├─ IInputRoute         x11: XSendEvent            wayland: wl_pointer + XSendEvent / passthrough
  ├─ IOutputSet          x11: XRandR                wayland: wl_output + xdg_output
  └─ IToplevelWatcher    x11: EWMH list             native: portal/stream + compositor protocols
```

- 共享组件：`LayoutCalculator`、`FrameDiff`、`FrameStats`、`OverlayVisibility`、`EffectRegistry`、`Anime4KChain`、`LanczosPresenter`。
- X11 `UpscaleSession` 保持原样；Wayland 会话新增 peer implementation，降低回归风险。
- `VulkanBlitter` 增加 Wayland surface 构造；上传/effect/Lanczos/呈现路径复用。

### 6.9.2 Wayland 呈现

- 每个输出一个 `zwlr_layer_shell_v1` surface：`layer=overlay`、四边 anchor、`exclusive_zone=-1`、`keyboard_interactivity=none`、`set_size(0,0)`。
- 输入区域：
  - XWayland 窗口化：内容矩形（接收后 `XSendEvent` 转发）。
  - 原生全屏：空 region（输入穿透）。
- 分数缩放：
  - 绑定 `wp_fractional_scale_manager_v1`、`wp_viewporter`、`zxdg_output_v1`。
  - layer-shell configure 的 width/height 是 surface-local（逻辑）尺寸；用 output logical/physical 尺寸计算 scale。
  - buffer 用物理像素；`wp_viewport.set_destination(logicalW, logicalH)`；Vulkan swapchain extent = 物理尺寸。
  - 150% HDMI：逻辑 2560×1440 → 物理 3840×2160；100% DP：1800×2880 → 同尺寸。
- hide：`wl_surface.attach(null)` + commit，input region 置空；show：下一次 present 重新 attach，恢复时强制整帧。
- 热插拔：`wl_registry.global/global_remove` 更新输出；当前输出消失时按名称回退/重新选屏，重建 overlay/swapchain。

### 6.9.3 XWayland 混合

- 捕获：保持 `X11Capture`（XShm）+ XDamage + 脏矩形。
- 呈现：Wayland layer-shell overlay + Vulkan Wayland WSI。
- 输入：overlay 接收 `wl_pointer`；逻辑坐标按 scale 转物理，走现有 `CoordinateMapper` → 源窗口坐标 → `XSendEvent`；键盘不转发，`keyboard_interactivity=none` 保持游戏焦点。
- 生命周期/弹窗/焦点：继续用 X11 `X11WindowProbe`/`X11WindowEnumerator`/`OverlayVisibility`；源窗口仍是 X11，判断逻辑不变。
- 该路径解决 overlay 输入/焦点问题，但**不解决 XShm 捕获撕裂**。v1.0 主路径改用嵌套 +
  toplevel 抓取，不走 XShm；旧 `--window` 的撕裂最终方案（portal 合成帧 / DRI3+fence）推迟到
  v1.0 后（[post-v1.0.md](post-v1.0.md) §5）。

### 6.9.4 原生 Wayland 捕获

- Portal `ScreenCast` v6：`CreateSession` → `SelectSources`（window/monitor、`cursor_mode=hidden`、`persist_mode=2`、`restore_token`）→ `Start` → `OpenPipeWireRemote`。
- `restore_token` 单次使用：每次 Start 后保存新 token；失效重新弹系统选择器。
- PipeWire：当前产品路径是 `gst-launch pipewiresrc` + CPU 帧管道；原生 `libpipewire` 客户端与 `SPA_DATA_Dmabuf` 零拷贝推迟到 v1.0 后；嵌套 toplevel DMA-BUF 同为可选。支持格式/尺寸变化、`spa_meta_region` damage、流错误。
- 使用 `pipewire-serial` + `PW_KEY_TARGET_OBJECT` 定位流。
- 源选择必须走 portal 系统选择器（Wayland 无通用窗口枚举）；`restore_token` 成功后尽量免打扰。
- 输入：全屏源空 input region 穿透；窗口化原生源在 M2a 不做映射（M2b 嵌套解决）。

### 6.9.5 多屏与坐标

- Wayland：`wl_output`（mode/geometry/scale/transform/name）+ `zxdg_output_v1`（logical position/size/name）；输出热插拔。
- XWayland 源：用 XRandR 物理矩形选屏，再按名称匹配 Wayland output（`HDMI-A-1`/`DP-1` 通常一致）。
- 呈现用物理像素；layer-shell 输入/configure 用逻辑像素；两者通过 output scale 换算。
- 输入映射：overlay 局部 → 内容矩形 → 源像素 → 注入；黑边不转发。
- 原生 Wayland 源无法确定输出时：用户选屏/主屏；M2b 嵌套由 sway 私有输出解决。

### 6.9.6 M2b 嵌套合成器（已实现）

- 私有 sway（headless，单一输出 `HEADLESS-1`）作为虚拟显示器；游戏运行在其中。
- 虚拟桌面在会话开始时按用户所选物理显示器**快照分辨率与刷新率**；之后的热插拔不改变
  运行中的桌面（避免刷新率变化导致 wlroots 选错 frame-pacing 输出而黑屏）。
- 抓帧：`ext-image-copy-capture` 按 toplevel 抓取（`GEMPRISM_TOPLEVEL_CAPTURE=1`），
  不依赖输出尺寸；旧的“每窗口一个虚拟输出”池仍保留为多窗口模式。
- 宿主窗口：每个游戏 toplevel 一个 KDE 窗口。默认是**原生 Wayland `xdg_toplevel`**
  （服务端装饰、分数缩放、`wp_pointer_warp_v1`）；`GEMPRISM_HOST_X11=1` 回退 X11/EWMH。
- 呈现：Vulkan swapchain 直接建在宿主 `wl_surface` 上；缓冲像素 = 显示器物理像素，
  `wp_viewport` 映射回逻辑尺寸，因此超分画面在 1.5× 屏上仍是 1:1。
- 输入：内部 XWayland 目前是 `XWarpPointer` 绝对定位 + XTest 键/鼠标按钮；**没有 X11 grab
  检测、相对注入、raw**（v1.0 后 β2）。原生 Wayland 客户端走 `wlr-virtual-pointer` 绝对/相对 +
  `zwp_virtual_keyboard_manager_v1` helper（`tools/gemprism-vkeyboard`）。
- 光标：游戏的 `zwp_locked_pointer_v1` 提示经 sway 补丁 → IPC → 应用 →
  `wp_pointer_warp_v1`，KWin 可见光标跟着跳；详见 [pointer-warp.md](history/pointer-warp.md)。
- 光标形状：sway 补丁把 `wp_cursor_shape_v1` 的形状名暴露到 `get_seats.cursor_shape`，
  宿主用 KDE 的 `wp_cursor_shape_manager_v1` 直接显示（KDE 原生光标，大小/主题一致）。
  Wine 实际使用图像光标，图像转发列为 v1.0 之后的优化项；X11 游戏的指针跳转由嵌套
  XWayland 的 `XQueryPointer` 轮询补偿。详见 [cursor.md](history/cursor.md)。
- 宿主窗口：窗口化时固定为游戏窗口大小（`set_min_size` = `set_max_size`），KWin 因此隐藏
  最大化按钮、禁止拖拽缩放，只保留最小化/关闭；全屏切换前清掉尺寸限制，退出后恢复。
  X11 宿主回退（`GEMPRISM_HOST_X11=1`）仍是旧的装饰行为。
- 窗口识别：Profile / token + PID / sway IPC / transient+window type；对话框单独宿主窗口并
  `xdg_toplevel.set_parent`。窗口化宿主尺寸取游戏窗口实际矩形，退出超分后恢复。
- 超分/还原切换：在 `SetFullscreen`/`UnsetFullscreen` **之前**按目标尺寸创建好 swapchain
  （Wayland 宿主接受任意 buffer 尺寸，`wp_viewport` 把预呈现帧映射到当前窗口）。configure
  到达时合成器已经持有目标尺寸的帧，不会再把旧窗口缓冲拉伸约 300ms。X11 宿主
  （`GEMPRISM_HOST_X11=1`）的 swapchain 受窗口 extent 限制，仍走 configure 后重建。
- 最小化：X11 游戏按 ICCCM 向嵌套 XWayland 的 root 发 `WM_CHANGE_STATE(IconicState)`，
  sway 没有最小化概念、忽略该请求（`WM_STATE` 一直 Normal、surface 保持 mapped），客户端在
  隐藏自己前会画一帧全黑。应用监听这条 ClientMessage（`SubstructureNotifyMask`，不能选
  `SubstructureRedirectMask`，否则会把请求从 sway 手里抢走），收到后在 600ms 内丢弃整帧全黑的
  候选帧，宿主保持上一帧、不闪；不最小化宿主。宿主跟随最小化没有实现：Wine 的 Win32 最小化
  状态不会被客户端的 map 清除，窗口被 map 后 ~25ms 又会发一次最小化请求（照搬 KDE 直连实测的
  KWin 顺序也一样），宿主会在点开后立刻弹回任务栏。Wayland 游戏（Wine 把窗口缩成 160×31）
  没有对应信号，同样不处理。

### 6.9.7 原 M2c：嵌套零拷贝与色彩收尾（v1.0 后可选，不阻塞 v1.0）

**决策（2026-11-13）**：v1.0 已能正常玩视觉小说，HDR 桌面下 SDR 也未观察到发灰；因此原 M2c
仅剩的两项（嵌套 DMA-BUF 零拷贝、SDR 色彩准确性）也不在 v1.0 前做。完整 HDR、混合 GL+Vulkan、
跨屏拼接、非嵌套 raw、portal 零拷贝、XWayland DRI3+fence 撕裂同样推迟。实现资料保留在
[m2c-plan.md](m2c-plan.md)，v1.0 后项见 [post-v1.0.md](post-v1.0.md)。

- **DMA-BUF 抓帧**：`ToplevelCapture` 从 `ext-image-copy-capture` 的 `dmabuf_device`/
  `dmabuf_format` 协商格式；用 GBM 在 compositor 指定的 DRM 设备上分配 2–3 个 buffer，
  通过 `zwp_linux_dmabuf_v1` 包成 `wl_buffer`，抓完 `ready` 后直接导入 Vulkan。
- **Vulkan 导入**：`VK_EXT_external_memory_dma_buf` + `VK_EXT_image_drm_format_modifier`；
  `VkImageDrmFormatModifierExplicitCreateInfoEXT` + `VkImportMemoryFdInfoKHR`；按
  `(width,height,fourcc,modifier)` 缓存 image/view；跳过 CPU 读回/转换/整帧上传。
- **回退**：无 dmabuf 格式、导入失败、跨 GPU、同步异常 → shm；`GEMPRISM_NESTED_DMABUF=0`
  可强制关闭；日志记录原因。
- **同步**：第一版按 `ready`（协议：“frame is copied, available for reading”）直接采样；
  实测有撕裂再引入 `wp_linux_drm_syncobj`/fence + 双缓冲；不假设同步正确。
- **色彩**：继续精确选 `B8G8R8A8Unorm` + `SRGB_NONLINEAR`，HDR 桌面下 SDR 不发灰；10-bit
  宿主输出是可选实验，不作为 v1.0 门槛。嵌套内完整 HDR 不做（sway 未 advertise
  `wp_color_manager_v1`）。
- **脏矩形**：dmabuf 第一版全帧（没有 CPU 像素做 tile hash）；后续用 compositor damage/GPU
  hash，不进 v1.0 门槛。

---

## 7. 性能预算

### 7.1 参考测试平台

> 以下数据来自参考平台的实测，用于确定性能量级，不代表最低配置要求。

| 项 | 规格 |
|---|---|
| GPU | NVIDIA RTX 4060 Laptop，8 GB |
| 驱动 | NVIDIA 专有驱动，Vulkan 1.4 |
| 显示 | 3840×2160，150% 系统缩放 |
| 桌面 | KDE Plasma 6，Wayland 会话 |
| 测试内容 | 1280×720 → 3840×2160；M0a XShm + vkCmdBlitImage |

### 7.2 实测结果

| 指标 | 数值 |
|---|---|
| 纯 Vulkan，提交并等待 | 0.200 ms |
| 纯 Vulkan，连续提交吞吐 | 0.182 ms |
| GL 渲染 → DMA-BUF → Vulkan | 0.327 ms |
| **跨 API 净开销** | **0.127 ms** |
| 跨 API 路径 | DRM format modifier（未退化为 linear tiling） |

### 7.3 解读

- 单 pass 的 GPU 计算成本可以忽略，60 fps 预算的 1.2%
- 跨 API 每次约 0.13 ms，占 60 fps 预算的 0.76%
- 瓶颈不在 GPU 计算，而在捕获、同步与呈现

### 7.4 待验证

- ~~Anime4K 多 pass 管线的真实开销~~：M1b 已测 S/M/L（§7.8）。
- ~~脏矩形生效时的静止画面占用~~：M1b 已测 static/repaint（§7.8）。
- ~~4K 输出下的呈现开销~~：M1b 4K override 已测 gpuTail 约 1.7–2.0 ms；真实 4K@144 已复测（§7.9）。
- 4K 源（2×→8K）、真实游戏长稳、多 GPU/跨 GPU 仍未测。

### 7.5 M0a 本机实测（2026-11-02）

| 项 | 结果 |
|---|---|
| 合成测试窗口 | 1280×720，60fps 动画，X11 |
| overlay | 3840×2160 override-redirect，XWayland root 物理尺寸 |
| 捕获 | XShmGetImage 直读被遮挡窗口，无 XComposite 重定向 |
| 呈现 | NVIDIA Vulkan + Xlib swapchain，`vkCmdBlitImage` |
| 帧率 | 多次 10–20s 采样，57.9–61.7 fps；典型 59.9 fps |
| 静止画面 | 首次捕获后 0 capture / 0 present，fps=0 |
| 输入 | overlay (1920,1080) → 源窗口 (640,360)，测试窗口收到 ButtonPress/Release |
| 弹窗 | 同 PID 顶层窗口出现后 overlay 隐藏，关闭后恢复 |
| 未测 | 真实游戏、Anime4K、端到端光子延迟、4K 源、多显示器 |

这些数字只证明“链路能跑、能到 60fps”，不等于 M0 最终验收；真实游戏（示例视觉小说 A / 示例视觉小说 B）仍需用户验证。

### 7.6 M0b 本机实测（v0.1.2，2026-11-02）

| 项 | 结果 |
|---|---|
| 效果链 | Anime4K S 5 pass（rgba16f）+ Lanczos-2 graphics pass |
| 1280×720→3840×2160 动态画面 | 42–51 fps；当前桌面 HDR/144Hz 模式下合成器上限约 48–50 fps，基线 `813e6a0` 同环境 49–51 fps |
| GPU 占用 | Anime4K 约 35–46%，显存 515 MiB；双线性约 33–40%，453 MiB（RTX 4060 Laptop） |
| 4:3 fit（1024×768 → 2880×2160） | 左右黑边像素均值 0，内容居中，比例正确 |
| 4:3 stretch | 铺满 3840×2160，无黑边 |
| 画质（测试卡，4K 截图） | 梯度均值 3.43 vs 双线性 2.71（+26%）；Laplacian 均值 3.49 vs 1.79（+96%）；文本/1px 线更锐利 |
| 静止画面 | 首次捕获+呈现后 0 capture / 0 present；不会因效果链空转 |

结论：Anime4K 路径在 4K 下有足够 GPU 余量；当前帧率主要受合成器/显示模式上限限制。真实游戏的画质与帧率仍需用户主观验收。

### 7.7 M1a 本机实测（v0.2.0，2026-11-04）

| 项 | 结果 |
|---|---|
| 多显示器枚举 | XRandR 1.5：HDMI-A-1 3840×2160+1800+489（primary）、DP-1 1800×2880+0+0 |
| 1280×720 → DP-1 竖屏 fit | overlay 1800×2880+0+0；内容 1800×1012+0+934；合成测试约 46–50 fps |
| 1280×720 → HDMI | overlay 3840×2160+1800+489；内容 3840×2160；合成测试约 41–44 fps（当前合成器上限） |
| 自动选屏 | 窗口从 DP-1 移到 HDMI 再移回，overlay 分别重建为 1800×2880 和 3840×2160，输入/呈现正常 |
| 手动选屏 | 窗口在 DP-1、`--monitor HDMI-A-1` 时 overlay 固定 HDMI，不跟随窗口 |
| 单实例 | 第二 CLI 返回 4；第二 GUI 显示“已在运行”提示；`--status` 返回 pid/mode/session/monitor |
| 退出 | `SIGTERM`、窗口关闭、游戏关闭后无残留进程/overlay |

### 7.8 M1b 本机实测（v0.2.1，2026-11-05）

以下为 4K 外接屏接入前的开发机数据（单屏 eDP-2 1920×1200；4K 用 `--output-size 3840x2160` 覆写）；真实 4K 外接屏结果见 §7.9。

| 项 | 结果 |
|---|---|
| 静态 `static` | 0 capture / 0 present；`gpu=n/a`；整卡 GPU 0%；CPU ~3% |
| 每帧重绘同像素 `repaint` | captured≈60/s、presented=0、gpu=n/a、整卡 0%；CPU ~9% |
| 小脏块 one-shot | 与 `--no-dirty-rect` 最终截图 `AE=0`；capture≈4.7ms、diff≈4–5ms（静态窗口首次读回） |
| 小脏块动画 | capture≈0.45ms、diff≈1.05ms、gpuEffect≈0.02–0.17ms、gpuTail≈0.1–0.3ms；47.9fps |
| 全屏动画 | 脏矩形开/关均 47.9fps；GPU effect≈2.9ms（1920×1200）；CPU 11% vs 6% |
| 4K override S/M/L | gpuEffect≈0.65 / 1.3–1.5 / 2.1–2.3ms；gpuTail≈1.7–2.0ms；vram 555/585/593MiB；47.9fps |
| 双线性 | 47.9fps；vram≈395MiB |
| 弹窗/恢复 | 隐藏/恢复日志与强制整帧重绘正常 |
| 零拷贝 | vkcube 线性 1-fd 导入成功；testpattern 2-fd/DCC 不适用 |

`present` 约 17ms 是 `QueuePresent` 等待 vblank/队列，不是 CPU 工作；48fps 是软件 testpattern/合成测试窗口的上限。真实游戏（Vulkan、同屏）见 §7.9：4K L 59.5–60.7fps，60fps 预算余量充足。

### 7.9 真实 4K 外接屏验收（2026-11-05 20:16）

环境：HDMI-A-1 3840×2160@144（HDR、150%）+ DP-1 1800×2880；示例视觉小说 C 1280×720@60 窗口模式；UI 选 Anime4K L，输出固定 HDMI-A-1。

| 项 | 结果 |
|---|---|
| fps | **59.5–60.7**（中位 59.9） |
| presented | **60/s** |
| capture | **3.3–4.6 ms**（p95 9–18 ms） |
| diff | 0.9–1.9 ms |
| gpuEffect / gpuTail | 1.1–2.0 / 0.8–1.1 ms |
| CPU / GPU util | 14–26% / 60–86% |
| 显存（整卡，含游戏） | 4796–4932 MiB |

- 游戏窗口与 overlay 的 MangoHud **均显示 60**；4K/60 通过，无需改刷新率。
- 对照：软件 testpattern 4K 37–38fps（capture 23ms）、vkcube 跨屏 50.9–59.8fps（capture 11–14ms）。差异在源窗口的 capture 路径，不在 Anime4K。
- 不要把 testpattern 的 4K fps 当真实游戏上限；4K fps 验收应以真实游戏/Vulkan 源为准。
- 日志：未随发布文档分发。
---

## 8. 风险与未决问题

| 风险 | 影响 | 应对 |
|---|---|---|
| 独占全屏窗口压不住 | 覆盖窗口无法置顶 | 本机实测 override-redirect 可盖住 EWMH 全屏窗口；独占 D3D 全屏仍需真实游戏验证 |
| 弹窗检测误判 | 频繁让位，体验抖动 | 200ms 轮询 + viewable/尺寸过滤；后续补 XRes/`WM_TRANSIENT_FOR` |
| 分数缩放下的坐标换算 | 位置偏移 | XWayland 每屏物理矩形由 XRandR 提供；overlay 用单屏物理尺寸，输入 root 坐标减屏原点；已用 150% + 100% 混合布局实测 |
| 鼠标转发对 raw input 无效 | FPS/鼠标视角游戏不工作 | M1 只支持视觉小说/2D；raw input 放后续阶段，需要虚拟输入设备/合成器方案 |
| 轮询 `GetCursorPos`/DirectInput 的游戏看不到合成指针 | 悬停高亮可能仍不触发（点击/滚轮正常） | 合成事件只能更新 X/Wine 的消息队列，真实指针仍在 overlay 上；除非 warp 真实指针或引入虚拟输入设备/合成器方案（M2），M0 不承诺这类悬停 |
| 色彩空间 | HDR 游戏可能发灰或偏亮 | v1.0 为 SDR 直通；HDR 桌面下 SDR 内容用户实测正常；完整 HDR 游戏捕获/10-bit 输出推迟到 v1.0 后 |
| 多 GPU / 混合显卡 | 跨 GPU 拷贝；GL 在 AMD、Vulkan 在 NVIDIA | 用 CPU 捕获 + NVIDIA Vulkan；混合管线 v1.0 不做，headless EGL 方案推迟到 v1.0 后 |
| XTest/EIS 授权 | 弹权限框、移动真实指针 | 不使用 XTest；用 XSendEvent 转发 |
| 真实游戏验证 | 示例视觉小说 A可能不到 60fps；示例视觉小说 B静止时 1–2fps | 验收按“画面变化段 60fps、静止段不空转”执行；M1b 示例视觉小说 C 4K L 已测 59.5–60.7fps，M1a 老功能留 1.0 回归 |
| 脏矩形边界/halo | 块缝、文字/1px 线断裂 | manifest radius + 2px 安全余量；one-shot、弹窗、resize 与整帧截图 `AE=0`；示例视觉小说 C 4K L 59.5–60.7fps |
| 零拷贝 DCC/多 fd | 导入失败或画面错 | 运行时检查 `nfd==1 && modifier==0`，否则回退 XShm；M1b 不默认启用 |
| 4K 源分块哈希 | CPU 约 4 倍（~4ms/帧） | M1b 只承诺常见 VN 分辨率；4K 源在 M2/后续优化（128px tile、SIMD、GPU hash） |

### 未决问题

1. 覆盖窗口用 Vulkan swapchain 还是直接 present，取决于 compositor 的 vsync 行为
2. 零拷贝的 DRI3 fence → Vulkan external semaphore 同步、真实游戏/跨 GPU 验证（M2 候选）
3. 是否需要为 4:3 源内容提供整数缩放预设

---

## 9. 参考项目

- [Magpie](https://github.com/Blinue/Magpie) —— effect 管线与几何模型的参考实现
- [Anime4K](https://github.com/bloc97/Anime4K) —— GLSL 超分算法
- [linux-rt-upscaler](https://github.com/baronsmv/linux-rt-upscaler) —— X11 捕获与输入转发的先例
- [gamescope](https://github.com/ValveSoftware/gamescope) —— 合成器层的上采样实现
