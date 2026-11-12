> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# GemPrismUpscaler · M0 可行性评估

日期：2026-11-02
评估对象：`README.md`、`docs/dev/architecture.md`、`docs/dev/roadmap.md`，以及本机实际环境
结论先行：**M0 可行，但不能按现有文档原样实现**。核心链路的每个关键原语都在本机验证通过了；不过文档里有 3 个会直接导致验收失败的设计错误，另有若干范围/环境/工程结构问题需要先定。

> **2026-11-02 第二轮修订**：XTest 的真实原因是 KWin EIS 授权门控（不是完全不可用）；混合管线已找到同 GPU 的 headless EGL 方案；跨平台接口、延迟定义、FPS/raw input 边界见 §10。§10 与前面冲突时，以 §10 为准。

---

## 1. 本机实测环境

| 项 | 实测值 | 与文档的差异 |
|---|---|---|
| 系统 | CachyOS（Arch 系），内核 7.3.1 | 与“Arch Linux 系”一致 |
| 会话 | KDE Plasma 6.8.2，Wayland，XWayland 24.1.14 | 一致 |
| 显示 | HDMI-A-1，3840×2160@144Hz，缩放 150%，HDR **已开启**，VRR Never | 文档写 SDR；实际 HDR 开着 |
| 逻辑桌面 | kscreen 几何 2560×1440，scale 1.5 | 一致 |
| XWayland root | **3840×2160**（xrandr / XGetGeometry） | 文档 §4.5 把“逻辑 2560×1440”当成了 X11 窗口坐标系 |
| GPU | **混合显卡**：AMD Radeon 610M iGPU + NVIDIA RTX 4060 Laptop dGPU | 文档写“NVIDIA 独显”，且把“核显与混合显卡”排除在 M0 外 |
| 显示输出 | HDMI-A-1 接在 **NVIDIA**（card1 / 01:00.0）；KWin 进程确实在 NVIDIA 上 | — |
| XWayland GLX | `glxinfo` 报 **AMD Radeon 610M**（renderD129） | XWayland 的 GL 与 KWin 合成不在同一 GPU |
| Vulkan | 1.4.363；GPU0=AMD RADV，GPU1=NVIDIA 615.78.08；两者都支持 `VK_EXT_external_memory_dma_buf`、`VK_EXT_image_drm_format_modifier`、`VK_KHR_xcb_surface` | 设备选择必须显式做，默认枚举第一块是 AMD |
| .NET | SDK 10.0.114；项目 `net10.0`，Avalonia 12.1.4；`dotnet build` 成功 | README 写“.NET 9 SDK” |
| 工具 | `glslangValidator`、`glslc`、`spirv-val` 已装；**cmake 未装**；`vkcube` 已装 | README 要求 CMake 3.20+，当前构建用不到 |
| Avalonia | 12.1.4；`Avalonia.Wayland` 12.1.4 存在；当前项目**未引用** | README 说“无 Wayland 时自动回退”，当前代码并未启用 Wayland 后端 |

---

## 2. 已验证的关键事实（探针，源码在 `开发机归档/probe/`）

| # | 验证项 | 结果 | 对 M0 的意义 |
|---|---|---|---|
| 1 | 320×180 的 X11 窗口在 150% 下实际显示尺寸 | 截图中精确 **320×180**，位置 (1760,989) | XWayland 窗口是 **1:1 物理像素**，KWin 不在合成阶段放大 |
| 2 | 2560×1440 的 override-redirect 窗口 | 精确覆盖 2560×1440，只占屏幕 **2/3** | 文档 §4.5 的 overlay 尺寸是错的 |
| 3 | 3840×2160 的 override-redirect 窗口 | 精确覆盖 3840×2160 全屏 | 正确的 M0 overlay 尺寸 |
| 4 | 全屏游戏窗口之上放 override-redirect 窗口 | 绿色 overlay 完全盖住 3840×2160 的 EWMH 全屏窗口 | 文档 §8 “独占全屏压不住”的风险在本机不成立（至少 EWMH 全屏） |
| 5 | overlay 是否抢焦点 | map 前后 `XGetInputFocus` 都是游戏窗口 | 键盘焦点可以留在游戏 |
| 6 | 窗口被 overlay 完全遮挡时的捕获 | `XShmGetImage` / `XGetImage` 直接读窗口，取到的是**窗口自己的内容**，不是 overlay | 捕获不需要先 `XCompositeRedirectWindow`；KWin 已经做了重定向 |
| 7 | `XCompositeNameWindowPixmap` | 对被遮挡窗口返回 320×180 的正确内容 | 也可用，但比直接 `XShmGetImage` 多一步 |
| 8 | XDamage 局部重绘 | 画两个 20×20 / 30×10 小矩形，Damage 报的是 **640×360 全窗口** | M1 的“按脏矩形只算变化区域”不能直接依赖 XDamage 的矩形 |
| 9 | XTest 合成输入 | 指针没动；KDE 弹出“请求操控输入设备”授权框，日志显示 `kwin_eis_prompter` 注册 portal 失败 | XTest 被 KWin 的 **EIS 授权门控**，本次未授权；**不是 API 不可用**。M0 仍优先 XSendEvent（无权限框、不 warp 光标） |
| 10 | `XSendEvent` 合成鼠标事件 | 目标窗口收到 `ButtonPress x=10 y=20` | 鼠标转发可行；这是正确方向 |
| 11 | `XGrabKey` 抓默认热键 Alt+Shift+A | 成功，无 BadAccess | 全局热键可行 |
| 12 | XRes 查窗口 PID | vkcube 窗口没有 `_NET_WM_PID`，但 `XResGetClientPid` 返回 14023 | 弹窗过滤应使用 XRes，不能只靠 `_NET_WM_PID` |
| 13 | NVIDIA Vulkan 通过 XCB 呈现 | `vkcube --gpu_number 1 --wsi xcb` 正常运行，nvidia-smi 看到进程 | 4K overlay 的 Vulkan 呈现路径可行（性能另测） |
| 14 | Avalonia X11 的 HiDPI | `ClientSize=1920×986.67`，`RenderScaling=1.5`，screen=3840×2160 | 当前 X11 后端在 150% 下是可用的；Wayland 后端仍是更好选择 |
| 15 | KWin 是否支持 wlr-layer-shell | `wayland-info` 有 `zwlr_layer_shell_v1` v5 | M2 在 KDE 上的 layer-shell 方案可行；GNOME 的限制仍成立 |
| 16 | 真实 Vulkan 窗口捕获 | 对 vkcube 的 500×500 窗口取 pixmap，内容非空、有图案 | 捕获 Vulkan/XWayland 窗口本身可行 |

> 说明：探针用了真实桌面窗口和全屏 overlay，持续时间都在 1 秒级，已清理；截图在 `/tmp/probe_*.png`，没有进入项目。

---

## 3. 会直接导致验收失败的三个设计问题

### 3.1 overlay 尺寸与 150% 缩放（最硬的错误）

文档位置：`docs/dev/architecture.md` §4.5（196–208 行）、§6.2（303–310 行）；`docs/dev/roadmap.md` 第 17、26 行。

文档的模型是：

> 捕获 1280×720 → 超分 2× → 2560×1440 逻辑 overlay → 合成器 1.5× → 屏幕 3840×2160
> 不要按 4K 物理像素创建 X11 overlay，合成器会把它当作 5760×3240 的逻辑尺寸。

实测是：

- KScreen 逻辑几何确实是 2560×1440、scale 1.5；
- 但 XWayland root 是 **3840×2160**，X11 窗口坐标就是物理像素；
- 320×180 的窗口在屏幕上就是 320×180；2560×1440 的 overlay 只占 2/3 屏；3840×2160 的 overlay 才是全屏；
- 也就是说，**“缩放发生在合成器阶段”在本机不成立**。KWin 的 `[Xwayland] Scale=1.5` 是让 XWayland 应用按 Xft.dpi=144 自己处理缩放；不处理 DPI 的原始 Xlib/Wine 窗口仍是 1:1。

后果：

- 按文档实现，1280×720 的游戏开启超分后只会得到 2560×1440 的左上角画面，验收 #1 直接失败；
- 正确的输出是 3840×2160，源到目标总倍率是 **3×**，不是 2×；
- 两级缩放仍然成立：Anime4K 2× → 2560×1440，再 Lanczos 1.5× → 3840×2160；但最后一步必须由我们做，不是合成器做；
- 文档说的“最后一跳由合成器线性放大、会轻微软化”不存在；反而可以做到物理像素 1:1，画面更锐利。

实现要求：

- overlay 尺寸、最终 pass 的目标尺寸都取 `DisplayWidth/Height`（或 XRandR 当前 mode），不要硬编码 2560×1440；
- 100% 与 150% 都走同一套“root 物理尺寸”逻辑；
- 如果以后用户把 KWin 的 XWayland 缩放切成“由系统缩放”，行为会变；M0 应把当前模式写进前置条件，并在运行时校验。

### 3.2 鼠标输入模型：点击穿透不够，必须转发

文档位置：`docs/dev/architecture.md` §6.3（312–318 行）；`docs/dev/roadmap.md` 第 18 行“点击穿透 + 全局热键”；验收 #2。

文档说：

> 点击穿透由 input shape 保证，不需要转发鼠标事件。

这句话在“overlay 和游戏窗口一样大、位置相同”时成立；但 M0 的场景是 **1280×720 的小窗口 + 3840×2160 的全屏 overlay**。举例：

- 游戏窗口 1280×720，位于 (100,100)；
- 用户看到的是铺满屏幕的超分画面，点击屏幕中央 (1920,1080)；
- 空 input shape 让点击穿透到 (1920,1080) 下面的窗口——那里是桌面，不是游戏窗口（游戏窗口只覆盖 100..1380 × 100..820）；
- 游戏收不到这次点击，验收 #2 失败。

所以 M0 必须做 **鼠标坐标映射 + 合成事件转发**：

1. overlay 的 input shape **不要置空**，让它接收真实鼠标事件；
2. 用输出内容矩形（扣除黑边）反算到源窗口客户区坐标；
3. 通过 `XSendEvent` 向游戏窗口发送 `MotionNotify` / `ButtonPress` / `ButtonRelease` / 滚轮（Button4/5）；
4. 键盘不转发，焦点始终留在游戏窗口；overlay 不调用 `XSetInputFocus`；
5. 全局热键仍用 `XGrabKey`。

实测支持：

- `XSendEvent` 在 XWayland 下能送达目标窗口；
- overlay map 不抢焦点；
- `XGrabKey` 可用；
- `XTest` 受 KWin EIS 授权门控：本次探针触发了 KDE 的“请求操控输入设备”弹窗，但没有完成授权（日志里 `kwin_eis_prompter` 注册 portal 失败），所以事件被丢弃。这不是 XTest 本身不可用；即使授权，也得把真实指针 warp 到游戏窗口再点击，光标会明显跳动，不适合作为坐标映射方案。因此 `XSendEvent` 仍是首选。

风险边界：

- 事件驱动的 2D 游戏/视觉小说：可行，参考项目 [linux-rt-upscaler](https://github.com/baronsmv/linux-rt-upscaler) 的默认模式就是“覆盖窗口 + XSendEvent 转发鼠标”，键盘从不转发；
- 使用 raw input / 鼠标视角 / 直接读 `GetCursorPos` 的 3D 游戏：合成核心事件可能不被识别，M0 不应承诺；
- 光标：overlay 接收鼠标后，系统光标会显示在用户实际位置；游戏内部光标由转发坐标驱动。需要确认目标游戏是否显示系统光标、是否需要隐藏/自绘；
- 点击 overlay 是否会让它获得焦点，需要一次真人点击验证（map 已验证不抢焦点）。

这条不是“实现细节”，而是 M0 范围的一部分；架构图里的 `InputBackend` 本来就是对的，是 §6.3 的结论写错了。

### 3.3 M0 范围过大，且混合管线与设计文档自相矛盾

文档位置：`docs/dev/roadmap.md` M0 范围与“混合管线决策点”（13–42 行）；`docs/dev/architecture.md` §4.7（220–240 行）、功能表 F11（M2）。

矛盾点：

- design 的功能表把“混合 OpenGL/Vulkan 管线”列为 **M2/F11**；
- roadmap 却让 M0 “尝试实现”，还给了 2 周时间盒；
- README 又把“可混用 OpenGL 与 Vulkan 后端”写成已有特性；
- design §6.4 说 M0 内置 FSR1 EASU+RCAS，roadmap 的 M0 只写 Anime4K 2× + Lanczos；
- 当前机器是混合显卡：XWayland GLX 在 AMD、Vulkan 在 NVIDIA，跨 API 的 DMA-BUF 交接会变成跨 GPU 交接，不是文档里 0.127 ms 那种同设备开销。

建议（已按你的决定调整）：

- **M0 保留混合管线尝试**，但作为独立的时间盒实验，不作为 M0 验收的阻断项；失败即回退全 Vulkan，接口保留 `backend` 字段。
- 关键前提：GL 必须跑在与 Vulkan 同一块 NVIDIA 上。不要用 XWayland GLX（本机 GLX 在 AMD），改用 **headless EGL surfaceless context**（已验证，见 §10.2）。
- M0 不同时做 FSR 和 Anime4K；先选一个（建议先用 Lanczos/FSR 打通链路，再上 Anime4K）。
- 混合管线的时间盒建议 2 周，放在 M0a 闭环跑通之后；M0 总工期另算。

按现有范围（捕获 + Vulkan 4K 呈现 + 输入 + 弹窗 + Avalonia UI + 混合管线 + Anime4K + 测试），单人不可能是 2 周；更现实的是 4–6 周全力投入，混合管线再加 2–4 周。roadmap 里的“2 周”只覆盖混合管线一项，不是 M0 整体。

---

## 4. 其他需要修正或确认的点

### 4.1 环境假设

- **混合显卡**：M0 表格写“NVIDIA 独显”，又把“核显与混合显卡”排除；但参考测试平台就是这台 4060 Laptop（带 AMD 610M）。需要明确：M0 是“在这台混合显卡笔记本上可用”，还是“只在纯 NVIDIA 台式机上可用”。前者需要接受 XWayland GLX 在 AMD、Vulkan 在 NVIDIA 的跨 GPU 呈现。
- **HDR**：输出当前 HDR 已开启；文档假设 SDR。建议 M0 测试时关闭 HDR，并在 README 写前置条件；否则色彩/tone mapping 的差异会干扰验收。
- **150%**：当前 KWin 是“XWayland 应用自己缩放”的模式；文档的 2560×1440 方案只在“系统缩放 XWayland”模式下成立。需要把模式写进前置条件，或做运行时探测。
- **100%**：本次没有改桌面缩放去测；从机制看，用 root 物理尺寸即可同时覆盖 100%/150%，但验收时要实际跑一遍。

### 4.2 捕获

- 实测 `XShmGetImage` 直接读被遮挡窗口就能拿到窗口内容；`XCompositeRedirectWindow` 在当前 KWin 下不是必需。文档 §6.1 的“必须重定向”应改为“在无合成器的 X11 会话下需要重定向；KWin 合成下直接读窗口即可，若 RedirectWindow 返回 BadAccess 就跳过”。
- 窗口被最小化时读到的可能是陈旧帧；用 `map_state` / `_NET_WM_STATE_HIDDEN` 检测并暂停。
- “用 `_NET_FRAME_EXTENTS` 排除边框”表述不准：直接捕获 client window 本身就不含边框；`_NET_FRAME_EXTENTS` 只在需要把 client 坐标换算到 frame 坐标时用。
- XDamage 在本机对任何局部改动都报全窗口；M1 的脏矩形不能只看 Damage 矩形，需要 **分块哈希**（参考项目就是这么做的），或者接受“全帧重算”。另外，很多游戏每帧都 present，即使画面静止也会触发 Damage；M1 的“静止 GPU <2%”需要实测，不能想当然。

### 4.3 呈现

- 文档 §6.2 说 override-redirect 窗口设置 `_NET_WM_STATE_ABOVE`；override-redirect 窗口不受 WM 管理，这个属性没有意义。实测不设置也能盖住全屏窗口；保留与否不影响，但别把它当机制。
- `XFixesHideCursor` 是 **整个 X server 级别** 的隐藏，不是只隐藏 overlay 上的光标；如果 M0 需要光标，别用这个。更合适的是给 overlay 设置空白 cursor，或让系统光标正常显示。
- “Vulkan swapchain 还是直接 present”仍未决；实测 NVIDIA + XCB + swapchain 能跑（vkcube），建议 M0 用 swapchain，present mode 先用 MAILBOX/FIFO 做对比。
- 全屏 overlay 会盖住任务栏/面板，这是预期；但退出/崩溃时必须销毁。X 连接断开时窗口会自动销毁，崩溃安全基本成立；进程挂死仍需 watchdog。

### 4.4 输入

- 如上，点击穿透不够；必须转发。
- XTest 受 KWin EIS 授权门控，本次未授权；即使开启 KWin 的 `XwaylandEisNoPrompt` 或批准弹窗，它也会移动真实指针，不适合作为常规坐标映射。M0 用 XSendEvent；XTest 最多作为 raw input 的后续实验。
- XGrabKey 可用，但如果游戏自己 grab 了同一个组合键会冲突；需要失败回退（UI 按钮/托盘）。
- 键盘焦点：map 不抢焦点已验证；点击是否抢焦点需要真人测试。若会抢，需设置 `WM_HINTS.input=False` 或点击后把焦点还给游戏。

### 4.5 弹窗与窗口跟踪

- 用 `_NET_WM_PID` 过滤不够：vkcube 这种窗口根本没有该属性；应使用 **XRes**（`XResQueryClientIds` / `XResGetClientPid`），本机已支持并实测可拿到 PID。
- Wine 的多个窗口通常同 PID；但对话框也可能来自子进程，建议结合 PID 树、`WM_TRANSIENT_FOR`、`WM_CLASS`。
- 弹窗让位逻辑没问题；恢复延迟 200ms 要防止“弹窗还在但被误判关闭”的抖动。
- 焦点变化隐藏 overlay 时，要区分“焦点到了弹窗”（应隐藏）和“点击 overlay 导致焦点变化”（不应隐藏/或应转发键盘）；需要真人测试。

### 4.6 超分与着色器

- Anime4K 的 GLSL 是 mpv 的 hook 格式（`//!HOOK`、`//!BIND`、`//!SAVE`、`//!WIDTH` 等），不能直接交给 glslang 编译；需要写一个小的预处理器/多 pass 展开器，或者改用已经展开的 SPIR-V/GLSL 版本。这是一块被低估的工作量。
- `Upscale_CNN_x2_M` 是多 pass CNN，4K 输出下的开销需要实测；文档 §7.4 的 2–5ms 只是估计。
- Anime4K 对 2D/动漫风格效果好，对 3D 游戏可能产生伪影；README 说“游戏”过于宽泛，建议明确“视觉小说/2D 优先”。
- FSR1 EASU+RCAS 是单 pass，移植简单，适合先打通链路；但 roadmap 的 M0 没写它，design 写了，需要统一。
- 运行时自定义 GLSL（M1）与“运行时不依赖 glslang”（§5）矛盾：用户着色器必须运行时编译，要么带 glslang/shaderc，要么只接受预编译 SPIR-V。M1 需要重新定义。

### 4.7 性能与延迟

- 文档 §7 的 0.2ms 是“1280×720 → 3840×2160 bilinear compute”，不含捕获回读、CPU→GPU 上传、呈现、合成器；不能用来证明端到端 <16ms。
- 端到端会多出：XShm 回读（720p 3.7MB）、上传、effect、swapchain、KWin 合成。60fps 预算 16.7ms 下，“<16ms”很容易变成“多一帧”。
- 建议把验收拆成：
  - 处理延迟（capture 读到 present 提交）< 8ms；
  - 显示端到端新增 ≤ 1 个合成器帧；
  - 用帧计数器/高速相机或 `VK_KHR_present_wait` 时间戳测，不靠感觉。
- 4K 输出下 60fps 写带宽约 2GB/s，4060 没问题；但 Anime4K 多 pass + 捕获 + 合成叠加后要实测。
- 144Hz 屏幕、VRR Never；present mode 与 KWin 的 vsync 行为需要实验。

### 4.8 工程结构与文档

- 当前仓库是 `dotnet new avalonia.app` 的单项目模板：没有 `.slnx`、没有 `src/`、没有 `tests/`、没有 `shaders/`、没有 `LICENSE`、没有 `NOTICE`。
- README 链接 `docs/dev/architecture.md`、`docs/dev/roadmap.md`，实际文件在 `docs/`；链接是断的。
- README 写 .NET 9 / CMake 3.20+；实际是 .NET 10，且纯 P/Invoke 方案不需要 CMake（cmake 也没装）。
- README 说 Avalonia “无 Wayland 时自动回退”；当前项目没引 `Avalonia.Wayland`，`UsePlatformDetect` 走的是 X11。要按 design §5.1 加包并条件 `UseWayland()`（FileRecovery 已有现成写法）。
- README 说 NativeAOT；FileRecovery 用的是 self-contained single-file，不是 AOT。Avalonia + Silk.NET + 运行时着色器的 AOT 需要单独 spike，M0 不建议承诺。
- 名称：目录/命名空间是 `GemPrismUpscaler`，README/文档叫 `Kaleido`；`Kaleido` 与区块链项目 Kaleido 重名，发布前要定。改名越早越好，否则 solution/命名空间/文档全要动。
- 许可证：README 写 GPL-3.0-or-later，但没有 LICENSE 文件；Anime4K 是 MIT、FSR 是 MIT、Magpie 是 GPL-3.0，GPL 应用兼容，但需要 NOTICE。若以后想改 MIT，需现在就考虑贡献者授权（CLA），不能等 1.0 再单方面改。
- 测试：文档没有测试计划。建议 Core 纯函数（几何/坐标/配置/管线调度）单测；App 用 Avalonia.Headless；X11 捕获在 Xvfb 下做集成测试；GPU/着色器用 golden image + 容差，按环境变量开关。
- 跨平台：README 明确“面向 Linux”，但你的目标是“跨平台”。需要确认是“Linux 内跨 X11/Wayland/发行版”，还是 Windows/macOS 也要。如果是后者，Core 的 `ICaptureBackend`/`IPresentBackend`/`IInputBackend` 现在就要按平台无关设计，M0 的 Linux 实现放 `Platform/Linux`。

---

## 5. 建议的 M0 重划（可验证的最小闭环）

### M0a · 窗口管线（先证明“能看能点”）

- 选择窗口：X11 window list（`_NET_CLIENT_LIST` + XRes PID + WM_CLASS/标题）
- 捕获：`XShmGetImage` 直接读 client window；Damage/定时轮询；最小化暂停
- 呈现：NVIDIA Vulkan（显式选 DiscreteGpu）+ XCB swapchain + 3840×2160 override-redirect 全屏窗口
- 效果：先用 Lanczos/bilinear（或 FSR EASU 单 pass）
- 输入：坐标映射 + `XSendEvent` 鼠标转发；键盘不转发；`XGrabKey` 热键
- 弹窗：SubstructureNotify + XRes PID + `WM_TRANSIENT_FOR`；发现新窗口隐藏 overlay，关闭后恢复
- UI：Avalonia 窗口列表 / 开关 / 退出；加 `Avalonia.Wayland`
- 生命周期：单实例、watchdog、崩溃/退出清理、日志
- 验收：全屏比例正确；鼠标在超分画面任意位置可操作；键盘正常；弹窗可操作；退出无残留；100%/150%；处理延迟可测

### M0b · 超分效果（再证明“值得用”）

- Anime4K 2×（先 S 或 M 一个变体）+ Lanczos 收尾；SPIR-V 离线编译
- 几何模式 fit/fill/crop/absolute 至少 fit/fill
- 质量/性能基准；4K 输出下 GPU 占用
- 验收：画面质量、帧率、延迟达标；不同宽高比正确

### 明确移出 M0

- 混合 GL/Vulkan 管线（M2）
- FSR 全套（如果 M0b 已选 Anime4K）
- 零拷贝 DRI3/DMA-BUF（M1）
- 脏矩形分块优化（M1）
- 动画、Profile 自动匹配、托盘（M1）
- 原生 Wayland 捕获/呈现（M2）
- NativeAOT（M3 前再评估）

---

## 6. 建议的工程结构（对齐 FileRecovery）

```
GemPrismUpscaler.slnx
src/
  GemPrismUpscaler.Core/          # 无 Avalonia；几何、配置、管线抽象、X11/Vulkan 引擎、平台实现
  GemPrismUpscaler/               # Avalonia App：Views/ViewModels/组合根
tests/
  GemPrismUpscaler.Tests/         # xunit + Avalonia.Headless；引用 Core 和 App
shaders/                          # glsl + 构建期生成的 spv
docs/                             # 随项目发布
开发机归档/                           # 内部/临时（已 gitignore）
```

- App 的 csproj 加 `Avalonia.Wayland`、`Silk.NET.Vulkan`、`Tomlyn`；Core 加 X11/Vulkan 绑定（Silk.NET）与配置模型。
- 用 `InternalsVisibleTo` 暴露测试接缝；App 用 `Avalonia.Headless` 做 ViewModel/UI 测试。
- 不需要 C++ 原生库；X11 用 P/Invoke + Silk.NET，Vulkan 用 Silk.NET；这能去掉 CMake 依赖。
- 发布用 self-contained single-file（照 FileRecovery），AOT 后议。
- 加 `global.json` 固定 SDK 10.0.114，`nuget.config`，`LICENSE`，`NOTICE`。

---

## 7. 需要你确认的问题

> 第一轮问题清单；已在 §10.6 回答的以 §10.6 为准。

### 必须现在定

1. **跨平台范围**：M0 是否只做 Linux/XWayland？Windows/macOS 是后续里程碑，还是现在就要按多平台后端设计？
   建议：M0 只做 Linux，但 Core 的 Capture/Present/Input 接口按平台无关设计。
2. **最终名称**：`GemPrismUpscaler` 还是 `Kaleido`？
   建议：用 `GemPrismUpscaler`（与目录/命名空间一致；Kaleido 有重名风险）。定名后再重排 solution。
3. **M0 范围**：是否接受“砍混合管线、全 Vulkan、先 Lanczos/FSR 再 Anime4K”的重划？
   建议：接受；混合管线移到 M2，M0a/M0b 分段验收。
4. **输入目标**：M0 是否只保证“2D/视觉小说/事件驱动”的鼠标转发？
   如果要支持 FPS/鼠标视角/raw input，M0 的范围和风险会显著增加；本机 XTest 受 KWin EIS 授权门控，且合成事件对 raw input 不一定有效。
5. **环境基线**：目标就是这台混合显卡笔记本 + 150% + 当前 KWin XWayland 缩放模式吗？
   建议：是；M0 测试时关闭 HDR；把“XWayland 自己缩放”写进前置条件，并在运行时用 root 尺寸校验。

### 可以稍后定

6. **延迟验收**：接受“处理延迟 <8ms + 显示新增 ≤1 帧”的重新定义吗？
   原“端到端 <16ms”需要明确测量方法，否则无法验收。
7. **工期**：M0 目标投入/截止时间？
   按重划范围，单人全力约 4–6 周；原“2 周混合管线时间盒”不再适用。
8. **测试深度**：Core 单测 + Avalonia Headless + Xvfb 集成 + GPU golden image，做到哪一层？
9. **发布形态**：M0 是否只要 self-contained single-file？AOT 是否硬性要求？
10. **文档目录**：继续使用隐藏目录，还是改成可见的 `docs/`？
    建议：改成 `docs/`；发布文档不应放在隐藏目录。

---

## 8. 建议的下一步（确认后）

1. 先做 30–60 分钟的“最小闭环 spike”：一个 1280×720 测试窗口 → XShm 捕获 → NVIDIA Vulkan 渲染纯色/棋盘 → 3840×2160 overlay；同时用 XSendEvent 转发一次点击。
   这能在写正式架构前，把捕获、呈现、输入三条路真正串起来。
2. spike 通过后，再重排 solution（core/app/test），写接口与几何单测。
3. 然后按 M0a/M0b 推进；每个阶段都有可运行的验收 demo。

---

## 9. 探针文件

| 文件 | 用途 |
|---|---|
| `开发机归档/probe/probe.c` / `probe2.c` | XWayland 扩展、捕获、遮挡、Damage、overlay 尺寸、全屏覆盖 |
| `开发机归档/probe/probe3.c` | XTest / 点击穿透控制实验（触发 KWin EIS 授权门控） |
| `开发机归档/probe/cap.c` | 按窗口 ID 取 pixmap 并采样 |
| `开发机归档/probe/shmcap.c` | 被遮挡窗口的 XShmGetImage 直读验证 |
| `开发机归档/probe/dmg2.c` | XDamage 局部/全窗口行为 |
| `开发机归档/probe/fs.c` | 全屏窗口 + override-redirect overlay + 焦点 |
| `开发机归档/probe/grab.c` | XGrabKey 热键 |
| `开发机归档/probe/send.c` | XSendEvent 鼠标转发 |
| `开发机归档/probe/xres.c` | XRes 查 PID |

这些都在 `开发机归档/` 下，已被 `.gitignore` 忽略，不会随项目发布。

---

# 10. 第二轮修订（2026-11-02，用户反馈后）

## 10.1 关于 KDE 授权提示与 XTest 的更正

我之前说“XTest 完全不可用”**说得太绝对**。你看到的 KDE 弹窗是关键线索。对应日志（01:15:56 / 01:17:00 / 01:17:16）是：

```
kwin_wayland_wrapper[2713]: [xwayland ei] Unhandled event EI_EVENT_SYNC (91)
kwin_eis_prompter[12280]: Failed to register with host portal
  QDBusError("org.freedesktop.portal.Error.Failed",
  "Could not register app ID: App info not found for 'org.kde.kwin.eisprompter'")
```

准确结论：

- KWin 6.x 把 XWayland 的 XTest 输入仿真接到 **EIS（Emulated Input Server）**，并默认要求用户授权；你看到的“probe 请求操控输入设备”就是这道门。
- 这次授权没有完成：`kwin_eis_prompter` 向 portal 注册 app ID 失败（KWin 6.8.2 包里没有 `org.kde.kwin.eisprompter.desktop`），所以 XTest 事件被丢弃，指针不动。
- 这不是“XTest API 不可用”。如果授权成功，XTest 可能会动；但这个路径有两个问题：
  1. 会弹权限框，或要求用户打开 KWin 的“允许控制指针和键盘而不再询问”（`XwaylandEisNoPrompt`，默认 false，且会降低系统安全性；KWin 6.2+ 的 XWayland KCM 里有这个选项，改动可能要求重新登录）。
  2. 即使能用，XTest 是**移动真实指针再点击**；游戏窗口被 overlay 盖住，指针会明显跳到小窗口位置，体验不可接受。
- 因此 M0 的结论不变：**鼠标坐标映射 + `XSendEvent` 转发**是首选；XTest 最多作为 raw input 的后续实验，不作为 M0 依赖。

另外，这也说明“空 input shape 点击穿透”并不是我们需要的方案：即使它能穿透，点击坐标落在原游戏窗口之外时仍然到不了游戏。必须由 overlay 接收鼠标并转发。

## 10.2 混合管线：跨 GPU 问题与同 GPU 方案

### 为什么会跨 GPU

本机拓扑：

- HDMI-A-1 接在 **NVIDIA**（card1 / 01:00.0），KWin 合成在 NVIDIA；
- 但 XWayland 的 GLX/EGL 默认用 **AMD**（renderD129，`glxinfo` 报 Radeon 610M）；
- Vulkan 枚举两块设备，默认第一块是 AMD；我们的 app 会显式选 NVIDIA。

所以如果混合管线按“普通 GLX 上下文 + Vulkan”实现，GL 纹理在 AMD、Vulkan 在 NVIDIA，DMA-BUF 交接就变成 **AMD → NVIDIA 跨设备**：要走 PCIe/PRIME，格式/修饰符和同步都更复杂，不能拿文档里 0.127 ms 的同设备数据来估算。

### 不需要“独显直连”也能同 GPU

实测 `eglinfo -B` 的 **Surfaceless platform** 直接就是 NVIDIA：

```
Surfaceless platform:
EGL vendor string: NVIDIA
OpenGL renderer: NVIDIA GeForce RTX 4060 Laptop GPU/PCIe/SSE2
```

我写了一个无窗口 EGL 测试（`开发机归档/probe/egltest.c`）：

```
EGL vendor=NVIDIA version=1.5
has export=1 import=1 surfaceless=1 gltex2d=1
GL renderer=NVIDIA GeForce RTX 4060 Laptop GPU/PCIe/SSE2 version=4.6.0 NVIDIA 615.78.08
DMA-BUF export OK fd=36 stride=1024 offset=0
```

也就是说：

- 用 **headless EGL surfaceless context** 可以在 NVIDIA 上创建 GL 上下文，完全不碰 XWayland GLX/AMD；
- GL 纹理 → EGLImage → `eglExportDMABUFImageMESA` 成功导出 DMA-BUF（线性布局，stride 1024，offset 0）；
- NVIDIA EGL 同时支持 `EGL_EXT_image_dma_buf_import`、`EGL_EXT_image_dma_buf_import_modifiers`、`EGL_MESA_image_dma_buf_export`、`EGL_KHR_gl_texture_2D_image`、`EGL_KHR_surfaceless_context`；
- NVIDIA GL 支持 `GL_EXT_memory_object_fd` / `GL_EXT_semaphore_fd`，所以反方向（Vulkan 导出 → GL 导入）也可行。

因此混合管线可以做到 **GL 和 Vulkan 都在 NVIDIA**，不需要 MUX/独显直连。代价是要自己管理 EGL 上下文和 DMA-BUF 同步（先 `glFinish` 保守同步，再上 fence fd），以及 Vulkan 侧按线性 modifier 导入。这个方案值得在 M0 里做时间盒验证。

### “独显直连”能不能解决

可能有用，但不是可靠前提：

- 独显直连/MUX 主要改**显示输出路由**；有些机型能在 BIOS 里彻底禁用 iGPU，有些只是把内屏接到 dGPU，AMD render node 仍然存在，XWayland GLX 仍可能选 AMD。
- 即使禁用 iGPU，也只是这一台机器/这个 BIOS 配置下成立，不能作为产品要求。
- 所以：**可以用它做对照实验，但不要把 M0 的混合管线建立在“用户开独显直连”上**；headless EGL 是更可控的方案。

### M0 混合管线的时间盒与验收

建议放在 M0a（捕获/呈现/输入闭环）跑通之后，时间盒 2 周：

1. EGL surfaceless GL（NVIDIA）渲染一个 pass → 导出 DMA-BUF → Vulkan（NVIDIA）导入并采样，画面正确；
2. 加 fence/semaphore，确认无撕裂、无同步挂起；
3. 连续跑 10 分钟 720p→4K，60fps，无泄漏/无 device lost；
4. 与全 Vulkan 对比，跨 API 净开销 <1ms；
5. 任一不达标 → 回退全 Vulkan，保留 `backend` 字段。

注意：目前只验证了“导出成功”，**Vulkan 侧导入这个 fd 还没验证**；这是 M0 混合管线的第一个 spike。线性布局可能带来性能损失，但 720p/1440p 规模可接受；如果要用 tiled modifier，需要 `EGL_EXT_image_dma_buf_import_modifiers` + `VK_EXT_image_drm_format_modifier` 的格式协商，复杂度更高。

## 10.3 跨平台架构：M0 只做 Linux，但接口不写死 X11

你的要求是“M0 只做 Linux & X11/XWayland，Windows 后续支持，但架构保留可能性”。这可以落到 3 个项目里，不需要现在拆平台项目：

```
src/GemPrismUpscaler.Core/
  Abstractions/
    ICaptureBackend        // 枚举窗口、开始/停止捕获、返回 Frame
    IPresentBackend        // 创建/销毁 overlay、提交帧、设置几何
    IInputBackend          // 鼠标移动/按键/滚轮、热键注册
    IEffectBackend         // Vulkan / GL pass 执行
    IFrameSource / IFrameSink
  Geometry/                // 纯函数：ScalingType、fit/fill/crop、坐标映射
  Config/                  // TOML、Profile 匹配
  Pipeline/                // pass 图、尺寸规则、后端切换
  Platform/Linux/          // X11 捕获/呈现/输入、Vulkan、EGL
  (未来 Platform/Windows/) // WGC/DXGI、SendInput、D3D11/12 或 Vulkan
src/GemPrismUpscaler/      // Avalonia App + ViewModels + 组合根
tests/GemPrismUpscaler.Tests/
```

关键约束：

- Core 的公开接口里**不要出现** `Window`（X11）、`HWND`、`XImage`、`Display*` 这类平台类型；用 `WindowHandle`（opaque `nint`）、`ProcessId`、`Title`、`ClassName`、`Bounds`、`DisplayInfo`。
- `Frame` 抽象同时容纳 CPU 帧（XShm）和 GPU 帧（D3D11/D3D12/Vulkan texture + 同步信息）；平台实现负责上传/导入。
- `IEffectBackend` 不假设 GL 一定存在；Vulkan 是全平台可用的主路径，GL 是可选后端。
- `DisplayInfo` 提供物理像素 bounds、逻辑 scale、refresh；不要让 Core 假设“150% 就是 2560×1440”。
- Windows 未来：WGC/DXGI 捕获、`SendInput` 转发、分层窗口/DComp 呈现；Vulkan effect 可复用，但 WGC 的 D3D 纹理到 Vulkan 需要 external memory 或先走 CPU。接口现在留好，M0 不实现。

## 10.4 视觉小说、FPS、鼠标视角与 raw input 的区别

| 类型 | 游戏怎么读鼠标 | 我们发 XSendEvent 绝对坐标 | M0 |
|---|---|---|---|
| 视觉小说 / 2D | 普通窗口事件：`ButtonPress`、`MotionNotify`，用绝对坐标做选项/点击 | 游戏能收到，坐标映射正确 | ✅ |
| 普通 3D（非 raw） | 窗口事件，但可能用相对位移算视角 | 可以发 motion，但方向/灵敏度可能不对 | ⚠️ 不承诺 |
| FPS / 鼠标视角 | 通常 grab 指针、隐藏光标，读相对位移（dx,dy）；很多用 raw input | 合成绝对事件可能被忽略；游戏也收不到真实相对位移 | ❌ |
| raw input | 绕过窗口消息，直接读设备（Windows Raw Input；Linux XInput2 raw events / evdev） | 注入不到这一层 | ❌ |

- **raw input** 不是“更快的鼠标事件”，而是游戏直接向操作系统/设备要原始数据，不经过普通窗口消息队列。合成 X11 事件到不了这一层。
- **鼠标视角** 需要相对位移和指针锁定；我们的 overlay 接收真实鼠标后，游戏拿不到真实的相对运动。XTest 即使授权，也要 warp 真实指针，光标会跳。
- **手柄** 不经过鼠标/键盘，overlay 不会拦截，通常不受影响（M0 不处理手柄映射）。
- **键盘** 焦点留在游戏窗口即可，不需要转发。
- 结论：M0 只承诺视觉小说/2D 事件驱动；FPS/鼠标视角/raw input 明确列为非目标，后续若要支持，需要虚拟输入设备（uinput/RemoteDesktop portal）或 compositor 集成，是另一个架构。

## 10.5 “延迟验收”到底指什么

一条帧的时间线：

```
T0 游戏 present 一帧
  → XWayland/KWin 持有窗口内容
T1 我们开始 XShm 读回
T2 读回完成，上传 GPU
T3 effect 完成
T4 present 提交
T5 KWin 合成
T6 屏幕扫描输出（光子）
```

- **处理延迟**：T4 - T1（或 T2 - T1 + GPU 时间），只算我们的捕获/上传/effect/提交，不含 KWin 合成和屏幕扫描。这个可以用 `Stopwatch` + Vulkan timestamp query 测，M0 应把它压到 **<8ms**。
- **显示端到端新增延迟**：T6 - T0，减去“游戏直接显示”的基线；包含 KWin 合成和扫描。60Hz 下一帧 16.7ms，capture+present 至少多一帧，所以“<16ms 端到端”几乎等于“不能多一帧”，非常紧。
- 建议 M0 验收拆成：
  1. 处理延迟 p95 < 8ms；
  2. 显示新增 ≤ 1 个合成器帧（60Hz 下 ≤16.7ms，144Hz 下 ≤6.9ms）；
  3. 用测试窗口黑白交替 + 240fps 手机慢动作/光电传感器数帧，或用 `VK_KHR_present_wait` 时间戳做辅助；不要只报 GPU compute 的 0.2ms。
- 文档 §7 的 0.2ms 只是“bilinear compute”一项，不能代表端到端；这个数字要保留但标注范围。
- **2026-11-02 用户确认**：M0 验收只要求**平均帧率稳定 60fps**；延迟不作为硬门槛，但仍建议记录处理延迟用于诊断。

## 10.6 已确认与仍待确认

已确认：

1. M0 只做 Linux & X11/XWayland；Windows 后续，但 Core 接口按多平台设计。
2. 项目名 `GemPrismUpscaler`，中文名“宝石棱镜”；README/解决方案/命名空间统一。
3. M0 保留混合管线尝试，作为时间盒实验；失败回退全 Vulkan。时间盒放在 M0a 之后、2 周，OK。
4. M0 只支持视觉小说类游戏；FPS/鼠标视角/raw input 非目标。
5. 目标环境就是当前这台混合显卡笔记本的当前状态；HDR 由你手动关闭。
6. **M0 验收 = 平均帧率稳定 60fps**；延迟不作为硬门槛。
7. 测试矩阵 1280×720、1024×768、1920×1080 足够。
8. 文档目录保持 `docs/`，README 链接改到 `docs/`。

仍待确认：

1. M0 总工期/今晚无人值守能做到哪一步（见对话中的说明）。
2. 是否现在就开始，以及是否允许用 `systemd-inhibit` 保持会话不休眠以便夜间自动验证。
