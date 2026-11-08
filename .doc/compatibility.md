# 兼容性与已知限制

本文记录 GemPrismUpscaler 的支持范围、已测试环境和已知限制。里程碑进度见 [roadmap.md](roadmap.md)，M2 详细计划见 [m2-plan.md](m2-plan.md)，未排期功能见 [backlog.md](backlog.md)。

> **M2 状态**：M2a（Wayland 平台核心 + portal 捕获 + portal GlobalShortcuts）已实现并冒烟；原生焦点自动跟随、窗口化原生装饰裁剪、正式手测待补。M2b（嵌套合成器）与 M2c（HDR/零拷贝/混合管线）为计划。

## 1. 支持矩阵

| 维度 | 支持情况 | 说明 |
|---|---|---|
| Linux | 部分支持 | X11/XWayland 后端；M2a 增加 Wayland 呈现/捕获 |
| Windows | 不支持 | Core 接口按平台无关设计，但没有 Windows 后端 |
| macOS | 不支持 | 同上 |
| KDE Plasma 6 + Wayland | 已验证（XWayland） | 当前主要开发/测试环境；M2a 增加 layer-shell overlay / portal 捕获 |
| KDE Plasma X11 | 未验证 | 理论可用；需要先确认窗口捕获/overlay 行为 |
| GNOME + Wayland | 未验证 | XWayland 窗口理论可捕获；overlay/焦点行为未测试；M2 不做完整支持 |
| wlroots（sway/Hyprland/niri 等）+ XWayland | 未验证 | M2 只做协议级验证；真实桌面验收推后 |
| 原生 Wayland 窗口 | M2a 已实现（窗口化输入/焦点跟随 M2b） | portal ScreenCast + PipeWire；全屏输入穿透；捕获含标题栏/阴影，正式验收以全屏/无边框为准；切到其他程序不会自动收起（M2a 用热键/托盘） |
| X11 顶层窗口 | 支持 | XShm + XDamage |
| XWayland 顶层窗口 | 支持 | 与 X11 同一捕获路径；窗口坐标为物理像素；M2a 可走 Wayland overlay |
| 单显示器 | 支持 | |
| 多显示器 | M1a 基础 + M2a | M2a：Wayland 输出枚举、基础热插拔、每屏缩放；overlay 只覆盖选中屏；portal 源与 overlay 建议选同一块屏 |
| 多显示器热插拔 | M2a 已实现（基础） | M1a 会重新枚举并在选屏失效时回退；M2a 监听 Wayland global_remove 并回退到另一块屏 |
| HDR 桌面 | SDR 直通 | HDR 开启时可正常显示 SDR 内容；HDR/linear 色彩在 M2c |
| 视觉小说/2D 输入 | 支持 | X11/XWayland：`XSendEvent`；M2a 全屏原生：输入穿透；窗口化原生/自动跟随焦点：M2b |
| 原生 Wayland 热键 | 支持（KDE） | portal GlobalShortcuts；首次授权，KDE 若存成 `none` 需在系统设置手动分配 |
| FPS/鼠标视角/raw input | M2b/M2c 计划 | 全屏/嵌套模式可输入穿透；窗口化原生用嵌套合成器；纯 X11 方案 M2c |
| NVIDIA 专有驱动 | 已验证 | RTX 4060 Laptop，Vulkan 1.4 |
| AMD/Intel Vulkan | 未验证 | 代码按标准 Vulkan 编写，未实机测试 |
| 混合显卡 | 部分支持 | 捕获走 CPU；effect 选择 DiscreteGpu/NVIDIA；跨 GPU 未验证 |
| Anime4K S/M/L/VL 2× + Lanczos-2 | 支持 | manifest 驱动 N-pass；默认 S，M/L 为质量档，VL 为实验最高画质档（18 pass/19 纹理/176B push constant） |
| 双线性 | 支持 | 回退/对比 |
| 自定义 effect | 支持 | TOML + 预编译 SPIR-V；ABI v1；运行时不需要 glslang；只支持 compute pass + 内置 Lanczos-2 收尾 |
| 设置/配置 | 支持 | TOML 持久化；热键/日志/第二实例/默认显示器/默认效果/缩放/托盘/开发模式 |
| Profile | 支持 | WM_CLASS / `/proc/<pid>/exe` / Wine cmdline；先匹配先赢 |
| 托盘 | 可选 | KDE/XFCE/Cinnamon/MATE/Ubuntu GNOME（AppIndicator）等；GNOME 无扩展时不创建，应用仍正常 |
| 分块脏矩形 | 支持 | 64px 哈希 + CNN halo + 持久合成图；`--no-dirty-rect` 可关闭；自定义 effect 可 `requires_full_frame` |
| 分段指标 / CSV | 支持 | capture/diff/upload/effect/present、GPU timestamp、NVML、CPU、p95 |
| 零拷贝（DRI3/DMA-BUF） | spike 完成 | 线性 1-fd GPU 窗口可导入；2-fd/DCC 需回退；未进默认链路 |
| FSR1 / NIS / Lanczos-3 / Bicubic | 未排期 | 有需求再评估 |

## 2. 当前验证环境

| 项 | 配置 |
|---|---|
| 发行版 | Arch Linux 系 |
| 桌面 | KDE Plasma 6，Wayland 会话 + XWayland |
| 显示器 | HDMI-A-1 3840×2160@144Hz，HDR，150%，主屏，位于 root `+1800+489`；DP-1 1800×2880 竖屏，SDR，100%，位于 `0,0`。M1b 已在此双屏环境完成真实 4K/60 验收 |
| XWayland root | 双屏 5640×2880 |
| GPU | AMD iGPU + NVIDIA RTX 4060 Laptop 8 GB；effect 在 NVIDIA |
| .NET | 10.0.114 SDK |
| Vulkan | 1.4 |
| 游戏 | 腐姬、秽翼的尤斯蒂娅（M0 验收） |

## 3. 已验证的行为

- 1280×720 → 3840×2160：软件 testpattern 动态约 37–38 fps（capture≈23 ms）；真实 Vulkan 游戏（终末地 720p@60）+ Anime4K L 稳定 59.5–60.7 fps（capture 3.3–4.6 ms）。
- 1280×720 → 1800×2880 竖屏 fit：内容 1800×1012，上下黑边，约 46–50 fps（合成测试窗口）。
- 静止画面：首次捕获/呈现后 0 capture / 0 present；每帧重绘同像素时 captured≈60、presented=0，整卡 GPU 0%。
- 4:3 fit（1024×768 → 2880×2160）：左右黑边像素均值 0，内容居中。
- 弹窗、最小化、焦点跟随、热键、关闭游戏、单实例、跨屏自动跟随。
- HDR 桌面下 SDR 内容可正常显示，不发灰；未做 HDR 色彩管理。
- M1b 脏矩形 one-shot：与 `--no-dirty-rect` 最终截图 `AE=0`；4K 小脏块动画 A/B 差 0.79%（软件 testpattern），真实游戏 L 59.5–60.7 fps。
- M1b 真实 4K@144：终末地 720p60 + L，fps 59.5–60.7、gpuEffect 1.1–2.0ms；S/M/L 全帧 gpuEffect 0.36/0.79/1.11ms（testpattern），整卡显存 782/812/820MiB（空闲桌面）。
- 双线性、弹窗让位/恢复、退出路径在 M1b 回归通过。

## 4. 已知限制

1. **原生 Wayland**：M2a 已实现 portal/PipeWire + layer-shell overlay；窗口化原生输入映射与**焦点自动跟随**由 M2b 嵌套合成器解决；M2a 只做全屏输入穿透；窗口捕获含标题栏/阴影；KDE 窗口流按输出 scale 合成，720p 全屏在 4K 输出上会得到 4K 流，M2a 会自动禁用 2×（无真实超分收益），低分辨率原生源需 M2b 嵌套或 XWayland 捕获。
2. **多显示器**：M1a 有基础版；M2a 增加 Wayland 输出枚举/热插拔/每屏缩放；跨屏连续拼接在 M2c（如需）。
3. **FPS/raw input**：M2a 全屏源可输入穿透；窗口化原生 Wayland 由 M2b 嵌套合成器解决；纯 X11 会话的 raw input 在 M2c 评估。
4. **HDR**：SDR 直通；HDR/linear 色彩在 M2c。
5. **4K 源**：2× 后是 8K，显存/带宽显著上升；M1b 仍只承诺常见 VN 分辨率（720p/1024×768/1080p）。脏矩形哈希 720p 约 1ms/帧，4K 源约 4 倍。
6. **黑边输入**：非 stretch（fit）时左右/上下黑边不转发鼠标点击和滚轮，这是预期行为，因为黑边不是源窗口像素；stretch 后原黑边区域被画面覆盖，会正常转发。
7. **swapchain resize**：M1a 在显示器变化时重建 overlay/blitter；源窗口 resize 会重建 capture，但显示模式本身的 swapchain recreation 仍不完整。
8. **单实例**：默认第二实例只提示错误；设置可改为激活已有 GUI；第二个 CLI 仍报错。KDE Wayland 下激活使用 KWin 脚本兜底。
9. **设置界面**：M1c 已实现；设置持久化在 `~/.config/GemPrismUpscaler/config.toml`，`GEMPRISM_CONFIG` 可覆盖。
10. **自定义 shader**：M1c 支持 TOML + 预编译 SPIR-V 的 compute pass 链；不支持自定义 graphics 最终 pass；纹理上限 19、单 pass 输入 16、push constant 256B（设备可能更低）。
11. **VL**：实验最高画质档，18 pass/19 纹理；720p 源/4K 输出下首帧 effect CPU 约 466ms（含 pipeline 创建），VRAM 约 880MiB（整卡）；帧率不作为验收门槛。
12. **GPU 窗口捕获撕裂**：X11/XWayland 下 `XShmGetImage`/`XGetImage`/XComposite 读取 GPU 更新的窗口时可能拿到半帧，overlay 会旧帧回退/撕裂；源窗口本身可能正常。M1c 未解决；M2a 的 XWayland 混合仍用 XShm，撕裂最终方案在 M2c（portal 合成帧 / DRI3+fence）。临时规避：源程序开 VSync。
13. **ComboBox 拖动问题**：KDE Wayland + 4K/150% 下，打开下拉框后拖动窗口，选择列表停留在旧位置；DP 100% 正常。Avalonia/Wayland popup 限制，M1 不处理，记录在 backlog §7。
11. **多屏焦点**：当前“跟随焦点”在操作另一块屏上的程序时会收起 overlay；这是 M1a 的预期行为，更多策略见 backlog。
12. **`.agent` 不入库**：交接、截图、探针只在本机；`.doc`/README 是发布文档。
13. **零拷贝**：只在 vkcube 线性 1-fd 上验证导入；软件窗口 2-fd/DCC 回退 XShm；未做 fence 同步/真实游戏/跨 GPU，未进默认链路。
14. **present 计时**：包含 `QueuePresent` 的 vblank/队列等待，不是纯 CPU 工作；软件 testpattern 4K 约 37–38fps，真实游戏 4K L 59.5–60.7fps。
15. **4K 外接屏**：已在 HDMI-A-1 3840×2160@144 + 终末地 1280×720@60 + Anime4K L 上实测 59.5–60.7fps；4K 源（2×→8K）仍未测。

## 5. 平台差异

- **XWayland 是 1:1 物理像素**：150% 缩放下 root 仍是 3840×2160；overlay 必须用物理矩形，不能按逻辑 2560×1440。
- **XRandR monitor 矩形**已经包含每块屏的缩放和旋转；多屏布局用 `XRRGetMonitors` 读取，不用窗口管理器逻辑坐标。
- **输入焦点**：KWin 下只调 `XSetInputFocus` 可能被焦点窃取防护忽略；必须配合 EWMH `_NET_ACTIVE_WINDOW`。
- **XTest 不可用**：KWin EIS 授权门控且会移动真实指针；当前用 `XSendEvent`。
- **快捷键冲突**：第二个实例不能再抢 `Alt+Shift+A`，所以单实例是硬约束。
