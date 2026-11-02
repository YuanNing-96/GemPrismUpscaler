# 宝石棱镜 · GemPrismUpscaler 设计文档

版本 0.1 · 对应 M0 规划

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
- M0 不做 HDR、多显示器、核显与混合显卡
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
| F6 | 按进程名 / 窗口类名自动套用配置 | M1 |
| F7 | effect 管线可通过配置文件组合，支持自定义 GLSL | M1 |
| F8 | 进出超分的缩放动画 | M1 |
| F9 | 脏矩形增量处理，静止画面不重复计算 | M1 |
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
│    wayland: portal + PipeWire + DMA-BUF   (M2)        │
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

本机实测（KWin 6.8.2 + XWayland 24.1.14，150% 缩放）：

- kscreen 的逻辑几何是 2560×1440，但 XWayland root 是 **3840×2160**；
- 一个 320×180 的 X11 窗口在屏幕上也是 320×180，KWin **不会**在合成阶段把 XWayland 窗口整体放大；
- `[Xwayland] Scale=1.5` 是让 X11 应用按 `Xft.dpi=144` 自己处理缩放；不处理 DPI 的原始 Xlib/Wine 窗口保持 1:1。

因此 overlay 必须按 **X11 root 的物理尺寸**创建（本机 3840×2160），不能按 2560×1440。1280×720 源到 4K 是 **3×**：

```
1280×720 --CNN 2×--> 2560×1440 --resample 1.5×--> 3840×2160
```

最后一步由我们做，不是合成器做；不存在“合成器线性放大导致软化”的问题，反而是物理像素 1:1。不要硬编码 2560×1440，用 `DisplayWidth/Height` 或 XRandR 当前 mode。100% 与 150% 都走同一套 root 尺寸逻辑。

若以后把 KWin 切成“由系统缩放 XWayland”模式，行为会不同；需要重新实测并做运行时探测。M0 的前置条件是当前“X11 应用自己缩放”模式。

### 4.6 弹窗让位

这是可用性问题，不是优化项。覆盖窗口盖住游戏的模态对话框会导致用户无法退出。

三层防御：

1. **监控**：M0a 用 200ms 轮询 `_NET_CLIENT_LIST`，用 `_NET_WM_PID` 过滤出目标进程的所有顶层窗口；只把**当前 viewable 且非 `_NET_WM_STATE_HIDDEN`、尺寸 ≥64×64** 的同 PID 窗口算作弹窗，避免被 Wine 的隐藏 helper/IME 窗口永久误判。后续可换 `SubstructureNotify` 事件驱动，并用 XRes 补 `_NET_WM_PID` 缺失的情况
2. **让位**：发现弹窗，立即隐藏覆盖窗口；弹窗关闭后恢复。恢复时必须强制重新捕获+呈现一次，因为 X11 窗口 unmap 后 Vulkan surface 的内容未定义，否则会黑屏到下一次画面变化。不做"把覆盖窗口压到弹窗下面"的尝试，X11 堆叠顺序不可靠
3. **兜底**：主窗口销毁 → 引擎停止并销毁覆盖窗口；主窗口最小化/unmap → 覆盖窗口隐藏，恢复后重新捕获；焦点变化 → 隐藏（见 6.2）；覆盖窗口进程崩溃 → watchdog 确保窗口被销毁

### 4.7 混合管线：接口先行，实现分期

实测跨 API 交接开销约 0.13 ms/次（见第 7 节），性能不是障碍。但工程复杂度是真实成本。

**策略**：

- `FrameHandle` 抽象为"Vulkan image"或"GL texture + fd"二选一
- effect 声明 `backend = "vk" | "gl"`，默认 `vk`
- 调度器按段分组：连续的 GL pass 合并为一段，减少跨界次数
- 默认路径：GLSL 源码经 glslang 编译为 SPIR-V，全部在 Vulkan 中执行
- 只有显式标记 `backend = "gl"` 且依赖 GL 特有能力的 pass 才走 GL

M0 保留混合路径作为时间盒实验（放在 M0a 闭环之后，建议 2 周）。关键前提：GL 必须与 Vulkan 跑在同一块 GPU 上。本机 XWayland GLX 在 AMD，而 Vulkan 在 NVIDIA，不能直接用 GLX；已验证用 **headless EGL surfaceless context**（NVIDIA）创建 GL 上下文并导出 DMA-BUF（`.agent/probe/egltest.c`）。Vulkan 侧导入与同步仍需 spike；若时间盒内不达标，降级为全 Vulkan，接口保持不变。

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
| OpenGL | Silk.NET.OpenGL（M0b 混合实验） | 混合管线需要；用 headless EGL 在 NVIDIA |
| X11 | 自写 P/Invoke（libX11/libXext/libXdamage） | Silk.NET.X11 覆盖不全；M0 不需要原生 C++ |
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

M0a 实际路径：

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
- XDamage 在 XWayland 下常报全窗口；M0a 用 Damage 做**帧级跳过**（无变化不捕获、不呈现），静止画面接近零开销。M1 若要按块重算，需要额外的分块哈希，不能直接依赖 Damage 矩形。
- M0 用 XShm 回读（720p 约 3.7 MB/帧）；M1 换 DRI3 + DMA-BUF 零拷贝。

### 6.2 呈现（X11）

- 创建 override-redirect 全屏窗口，尺寸取 **X11 root 物理尺寸**（本机 3840×2160）。
- override-redirect 窗口不受 WM 管理，不需要 `_NET_WM_STATE_ABOVE`；实测可以直接盖住 EWMH 全屏窗口。
- 不把 input shape 置空：overlay 要接收真实鼠标再转发（见 6.3）。
- 不把输入焦点留在配置界面：启动会话、热键重新显示、点击覆盖窗口时，通过 EWMH `_NET_ACTIVE_WINDOW`（source indication=2）+ `XSetInputFocus` 把焦点交还游戏。只调 `XSetInputFocus` 会被 KWin 的焦点窃取防护忽略（实测）。
- 设置 `WM_HINTS.input=False`，overlay 自身不参与焦点；`FollowFocus` 默认开启：游戏失去焦点时收起覆盖窗口，回到游戏时恢复。配置界面提供开关，CLI 用 `--no-follow-focus` 关闭。
- 每次重新显示都重新捕获并呈现一帧（unmap 后 surface 内容未定义）；显示时把当前真实指针位置同步给游戏一次，避免悬停状态停留在旧位置。
- 不要用 `XFixesHideCursor`（这是整个 X server 级别）；M0a 保持系统光标。
- 用 Vulkan swapchain（Xlib/XCB surface）呈现。M0a 用 `vkCmdBlitImage` 做线性重采样，不需要 shader/pipeline。
- 覆盖任务栏/面板是预期行为；停止/退出/崩溃时必须销毁窗口。

### 6.3 输入

**不能只靠点击穿透。** 1280×720 的游戏窗口 + 3840×2160 overlay：空 input shape 会把屏幕中央的点击透传到原游戏窗口之外，游戏收不到。

M0a 做法：

- overlay 的 input shape 保持完整，接收真实 `MotionNotify` / `ButtonPress` / `ButtonRelease`；
- 用输出内容矩形（fit 时扣除黑边）反算源窗口客户区坐标；
- 通过 `XSendEvent` 向源窗口发送 `MotionNotify`、`ButtonPress`、`ButtonRelease`、滚轮（Button4/5）；不移动真实指针；
- 指针进入/离开内容矩形时补发合成 `EnterNotify` / `LeaveNotify`（`NotifyNormal` / `NotifyNonlinear`）：overlay map 会让真实指针“离开”源窗口，部分引擎/工具包只靠 crossing 事件维护悬停状态；拖拽在内容矩形外松手时仍补发 release，避免按键卡住；
- 键盘不转发：焦点由 6.2 的激活逻辑交还游戏，按键直接到游戏；
- 热键 `Alt+Shift+A` 用 `XGrabKey` 在 root 上抓取；抓取失败要记录并回退 UI 按钮。

XTest 在本机 XWayland 下受 KWin EIS 授权门控（会弹“允许操控输入设备”），且即使授权也要 warp 真实指针，不适合作为常规映射。XSendEvent 不需要权限框。

M0 只支持视觉小说/2D 事件驱动输入；FPS/鼠标视角/raw input 不在范围内。

### 6.4 超分

M0a 当前：

- Vulkan `vkCmdBlitImage` 双线性重采样到目标尺寸（无 shader/pipeline）
- fit / stretch 两种几何；1280×720 → 3840×2160 实测约 60fps

M0b 实际（v0.1.2）：

- **Anime4K `Upscale_CNN_x2_S`**：4 层 3×3 CNN（8 输入通道按正/负半轴拆成两组 4 通道）+ Depth-to-Space 2× 残差。mpv user-shader 格式由 `tools/build_shaders.py` 展开为 5 个独立 Vulkan compute shader，中间纹理 `rgba16f`，SPIR-V 离线编译并作为嵌入资源分发；运行时不依赖 glslang。
- **Lanczos-2 收尾**：4×4 taps，从 2× 结果重采样到最终内容矩形；由 graphics pipeline 直接写入 swapchain，render pass 负责清黑边。这样不需要额外的 4K 中间纹理，也避免 format 不兼容的 `vkCmdBlitImage`。
- **几何**：fit / stretch；fit 时先用源宽高比算出内容矩形，再把 2× 结果映射到该矩形。1024×768 fit → 2× 2048×1536 → Lanczos 到 2880×2160，居中 x=480。
- **算法切换**：`EffectMode.Anime4K`（默认）/ `EffectMode.Bilinear`；UI“超分算法”下拉框，CLI `--effect anime4k|bilinear`。双线性路径保持 M0a 的单次 `vkCmdBlitImage`，用于对比和故障回退。
- **未包含**：FSR1 EASU/RCAS、`Clamp_Highlights`、混合 GL+Vulkan、TOML effect 描述（M1）。Anime4K S 是效果/性能折中；M/L 变体后续可按同一管线接入。

未来的 effect 描述格式（M1，尚未实现）：

```toml
name = "anime4k-2x"
description = "Anime4K CNN 2x upscale + Lanczos-2 finish"

[[pass]]
name = "upscale"
shader = "anime4k_conv0..3 + d2s"
scaling = "normal"
scale = [2.0, 2.0]

[[pass]]
name = "finish"
shader = "lanczos"
scaling = "normal"
scale = [1.5, 1.5]
```

### 6.5 配置与 Profile

```toml
[profile.default]
match = ["*.exe"]
pipeline = "anime4k-2x"
hotkey = "Alt+Shift+A"

[profile.galgame]
match = ["game.exe", "Launcher.exe"]
pipeline = "anime4k-2x-soft"
allow_windowed = true
```

### 6.6 动画（M1）

覆盖窗口本身保持全屏不动，只对 GPU 侧的输出矩形做插值：

- 进入：quad 从游戏窗口屏幕矩形 → 全屏矩形，`ease-out cubic`，约 220 ms
- 退出：反向，动画结束后销毁覆盖窗口
- 动画期间切换到轻量重采样，结束后恢复高质量
- 每帧跟踪游戏窗口位置，避免动画期间窗口移动导致错位

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

- Anime4K 多 pass 管线的真实开销（估计 2~5 ms，需实测）
- 脏矩形生效时的静止画面占用
- 4K 输出下的呈现开销

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

这些数字只证明“链路能跑、能到 60fps”，不等于 M0 最终验收；真实游戏（腐姬 / 秽翼的尤斯蒂娅）仍需用户验证。

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


---

## 8. 风险与未决问题

| 风险 | 影响 | 应对 |
|---|---|---|
| 独占全屏窗口压不住 | 覆盖窗口无法置顶 | 本机实测 override-redirect 可盖住 EWMH 全屏窗口；独占 D3D 全屏仍需真实游戏验证 |
| 弹窗检测误判 | 频繁让位，体验抖动 | 200ms 轮询 + viewable/尺寸过滤；后续补 XRes/`WM_TRANSIENT_FOR` |
| 分数缩放下的坐标换算 | 位置偏移 | M0 实测 XWayland 1:1；overlay 用 root 物理尺寸，不依赖逻辑 2560×1440 |
| 鼠标转发对 raw input 无效 | FPS/鼠标视角游戏不工作 | M0 只支持视觉小说/2D；raw input 需要另一套输入方案 |
| 轮询 `GetCursorPos`/DirectInput 的游戏看不到合成指针 | 悬停高亮可能仍不触发（点击/滚轮正常） | 合成事件只能更新 X/Wine 的消息队列，真实指针仍在 overlay 上；除非 warp 真实指针或引入虚拟输入设备/合成器方案（M2），M0 不承诺这类悬停 |
| 色彩空间 | 画面发灰或偏亮 | M0 测试关闭 HDR、假设 sRGB 直通；M1 加 linear 选项 |
| 多 GPU / 混合显卡 | 跨 GPU 拷贝；GL 在 AMD、Vulkan 在 NVIDIA | M0a 用 CPU 捕获 + NVIDIA Vulkan；混合管线用 headless EGL（同 GPU），已验证导出 |
| XTest/EIS 授权 | 弹权限框、移动真实指针 | 不使用 XTest；用 XSendEvent 转发 |
| 真实游戏验证 | 腐姬可能不到 60fps；秽翼静止时 1–2fps | 验收按“画面变化段 60fps、静止段不空转”执行，腐姬按源上限 |

### 未决问题

1. 覆盖窗口用 Vulkan swapchain 还是直接 present，取决于 compositor 的 vsync 行为
2. 脏矩形与 CNN 管线的配合：CNN 有感受野，边缘需要额外 padding
3. 是否需要为 4:3 源内容提供整数缩放预设

---

## 9. 参考项目

- [Magpie](https://github.com/Blinue/Magpie) —— effect 管线与几何模型的参考实现
- [Anime4K](https://github.com/bloc97/Anime4K) —— GLSL 超分算法
- [linux-rt-upscaler](https://github.com/baronsmv/linux-rt-upscaler) —— X11 捕获与输入转发的先例
- [gamescope](https://github.com/ValveSoftware/gamescope) —— 合成器层的上采样实现
