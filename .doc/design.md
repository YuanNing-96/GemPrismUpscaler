# Kaleido 设计文档

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

### 4.5 分数缩放的处理

KDE Plasma 在 150% 缩放下，XWayland 窗口的 pixmap 仍是逻辑尺寸（实测 1280×720 的窗口，pixmap 也是 1280×720），缩放发生在合成器阶段。

由此得出 M0 的呈现策略：

```
捕获 1280×720 → 超分 2× → 2560×1440 逻辑 overlay → 合成器 1.5× → 屏幕 3840×2160
```

**不要**按 4K 物理像素创建 X11 overlay：合成器会把它当作 5760×3240 的逻辑尺寸，直接超出屏幕。

这个策略的额外好处是超分倍率正好 2.0，是 Anime4K 的原生工作点。代价是最后一跳由合成器做线性放大，会有轻微软化，M1 可用 Wayland layer-shell overlay 直接输出物理像素来消除。

### 4.6 弹窗让位

这是可用性问题，不是优化项。覆盖窗口盖住游戏的模态对话框会导致用户无法退出。

三层防御：

1. **监控**：在 root window 上监听 `SubstructureNotify`，捕获 `MapNotify` / `DestroyNotify` / `ConfigureNotify`，用 `_NET_WM_PID` 过滤出目标进程的所有顶层窗口
2. **让位**：发现主窗口之外的新窗口被映射，立即隐藏覆盖窗口，弹窗关闭后延迟 200ms 恢复。不做"把覆盖窗口压到弹窗下面"的尝试，X11 堆叠顺序不可靠
3. **兜底**：主窗口销毁 → 进程退出；焦点变化 → 隐藏；覆盖窗口进程崩溃 → watchdog 确保窗口被销毁

### 4.7 混合管线：接口先行，实现分期

实测跨 API 交接开销约 0.13 ms/次（见第 7 节），性能不是障碍。但工程复杂度是真实成本。

**策略**：

- `FrameHandle` 抽象为"Vulkan image"或"GL texture + fd"二选一
- effect 声明 `backend = "vk" | "gl"`，默认 `vk`
- 调度器按段分组：连续的 GL pass 合并为一段，减少跨界次数
- 默认路径：GLSL 源码经 glslang 编译为 SPIR-V，全部在 Vulkan 中执行
- 只有显式标记 `backend = "gl"` 且依赖 GL 特有能力的 pass 才走 GL

M0 尝试实现混合路径；若在时间盒内无法稳定，降级为全 Vulkan，接口保持不变，后续补齐不返工。

**跨 API 的两条实现路径**：

| 路径 | 机制 | 适用 |
|---|---|---|
| A | GL 纹理 → `eglCreateImageKHR` → `eglExportDMABUFImageMESA` → Vulkan 导入 | Mesa 上顺畅，部分驱动拒绝从 GL 纹理导出 |
| B | Vulkan 分配可导出 image → `vkGetMemoryFdKHR` → GL `glImportMemoryFdEXT` 导入并渲染 | 兼容性更好，推荐作为默认 |

---

## 5. 技术选型

| 层 | 选型 | 理由 |
|---|---|---|
| 语言 | C# / .NET 9 | 跨平台、单文件分发、开发效率高 |
| AOT | NativeAOT | 无运行时依赖，启动快 |
| UI | Avalonia 12.1 | 原生 Wayland 后端已发布，可回退 X11 |
| Vulkan | Silk.NET.Vulkan | 需要 external memory 与 drm modifier，高层封装会挡路 |
| OpenGL | Silk.NET.OpenGL | 混合管线需要 |
| X11 | Silk.NET.X11 + 自写 P/Invoke | XComposite / XDamage / XShape 覆盖不全 |
| Shader | GLSL，离线编译为 SPIR-V | 运行时不依赖 glslang |
| 配置 | TOML | 手写友好 |
| 原生辅助 | C++17 小库（可选） | 捕获热路径，必要时下沉 |

### 5.1 关于 Avalonia 的 Wayland 支持

Avalonia 12.1.0 起提供原生 Wayland 后端，但**没有自动回退**：显式调用 `UseWayland()` 后，在没有合成器的环境中会直接启动失败。正确写法是条件判断：

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

```
XCompositeRedirectWindow(自动重定向)
  → XCompositeNameWindowPixmap 取 pixmap
  → XDamage 监听脏区域
  → 按脏矩形 XGetImage / XShmGetImage
  → 上传为 GPU 纹理
```

要点：

- **必须**重定向，否则窗口被遮挡时捕获到的是垃圾数据
- 窗口最小化时捕获会失败或返回陈旧帧，需要检测并暂停管线
- 排除窗口边框：只取 client window，用 `_NET_FRAME_EXTENTS` 计算偏移
- M0 用 XShm 回读（720p 约 3.7 MB/帧，开销 < 1 ms）；M1 换 DRI3 + DMA-BUF 零拷贝

### 6.2 呈现（X11）

- 创建 override-redirect 全屏窗口，`_NET_WM_STATE_ABOVE`
- 逻辑尺寸 = 屏幕逻辑尺寸（150% 缩放下为 2560×1440）
- `XShapeCombineRectangles` 把 input shape 置空，实现点击穿透
- 不调用 `XSetInputFocus`，焦点始终留在游戏窗口
- 隐藏光标：`XFixesHideCursor`
- 用 Vulkan swapchain 或直接 present 到窗口

### 6.3 输入

- 点击穿透由 input shape 保证，**不需要**转发鼠标事件
- 键盘同理，焦点在游戏窗口
- 唯一需要转发的是热键：`XGrabKey` 在 root window 上抓取
- 若某些游戏依赖 `XTest` 合成的相对移动，M1 再补

### 6.4 超分

M0 内置：

- Anime4K `Upscale_CNN_x2_M` + `Clamp_Highlights`
- Lanczos 重采样（收尾用）
- FSR1 EASU + RCAS

effect 描述格式：

```toml
name = "anime4k-2x"
description = "Anime4K CNN 2x upscale with highlight clamping"

[[pass]]
name = "clamp"
shader = "shaders/anime4k/Anime4K_Clamp_Highlights.glsl"
scaling = "normal"
scale = [1.0, 1.0]

[[pass]]
name = "upscale"
shader = "shaders/anime4k/Anime4K_Upscale_CNN_x2_M.glsl"
scaling = "normal"
scale = [2.0, 2.0]
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
| 测试内容 | 1280×720 → 3840×2160 bilinear compute，300 帧 |

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

---

## 8. 风险与未决问题

| 风险 | 影响 | 应对 |
|---|---|---|
| 独占全屏窗口压不住 | 覆盖窗口无法置顶 | M0 要求窗口化运行；后续研究 override-redirect 全屏的兼容 |
| 弹窗检测误判 | 频繁让位，体验抖动 | 加防抖窗口（200 ms）与白名单 |
| 分数缩放下的坐标换算 | 位置偏移 | M0 用实测的 pixmap 尺寸推导，不依赖 `_XWAYLAND_GLOBAL_OUTPUT_SCALE` |
| 原生 Wayland 无法注入输入 | 只能靠"对输入透明" | layer-shell + 空 input region + `keyboard_interactivity=none` |
| 色彩空间 | 画面发灰或偏亮 | M0 假设 sRGB 直通，M1 加 linear 工作空间选项 |
| 多 GPU | DMA-BUF 跨设备拷贝 | M0 锁定独显，强制设备选择 |

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
