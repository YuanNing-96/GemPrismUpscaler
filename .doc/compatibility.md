# 兼容性与已知限制

本文记录 GemPrismUpscaler 的支持范围、已测试环境和已知限制。里程碑进度见 [roadmap.md](roadmap.md)，未排期功能见 [backlog.md](backlog.md)。

## 1. 支持矩阵

| 维度 | 支持情况 | 说明 |
|---|---|---|
| Linux | 部分支持 | 只实现了 X11/XWayland 后端 |
| Windows | 不支持 | Core 接口按平台无关设计，但没有 Windows 后端 |
| macOS | 不支持 | 同上 |
| KDE Plasma 6 + Wayland | 已验证 | XWayland 窗口；当前主要开发/测试环境 |
| KDE Plasma X11 | 未验证 | 理论可用；需要先确认窗口捕获/overlay 行为 |
| GNOME + Wayland | 未验证 | XWayland 窗口理论可捕获；overlay/焦点行为未测试 |
| wlroots（sway/Hyprland/niri 等）+ XWayland | 未验证 | override-redirect overlay 的堆叠/焦点行为未测试 |
| 原生 Wayland 窗口 | 不支持 | 需要 portal + PipeWire，M2 |
| X11 顶层窗口 | 支持 | XShm + XDamage |
| XWayland 顶层窗口 | 支持 | 与 X11 同一捕获路径；窗口坐标为物理像素 |
| 单显示器 | 支持 | |
| 多显示器 | M1a 基础支持 | 自动跟随游戏窗口或手动选屏；overlay 只覆盖选中屏 |
| 多显示器热插拔 | 部分支持 | M1a 会重新枚举并在选屏失效时回退；完整热插拔/每屏 DPI 在 M2 |
| HDR 桌面 | SDR 直通 | HDR 开启时可正常显示 SDR 内容；不做 HDR 色彩管理 |
| 视觉小说/2D 输入 | 支持 | 鼠标点击/滚轮/键盘；`XSendEvent` 转发 |
| FPS/鼠标视角/raw input | 不支持 | 需要虚拟输入设备或合成器方案，放 M2 之后 |
| NVIDIA 专有驱动 | 已验证 | RTX 4060 Laptop，Vulkan 1.4 |
| AMD/Intel Vulkan | 未验证 | 代码按标准 Vulkan 编写，未实机测试 |
| 混合显卡 | 部分支持 | 捕获走 CPU；effect 选择 DiscreteGpu/NVIDIA；跨 GPU 未验证 |
| Anime4K S/M/L 2× + Lanczos-2 | 支持 | manifest 驱动 N-pass；默认 S，M/L 为质量档 |
| 双线性 | 支持 | 回退/对比 |
| 分块脏矩形 | 支持 | 64px 哈希 + CNN halo + 持久合成图；`--no-dirty-rect` 可关闭 |
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

1. **仅 X11/XWayland**：原生 Wayland 窗口不能捕获；需要 M2 的 portal/PipeWire 方案。
2. **多显示器基础版**：M1a 支持自动/手动选屏和跨屏重建，但热插拔、每屏 DPI、负坐标、跨屏拼接的完整处理在 M2。
3. **FPS/raw input**：`XSendEvent` 只能更新 X/Wine 消息队列；raw input/DirectInput/`GetCursorPos` 读真实设备，当前方案无效。
4. **HDR**：SDR 直通；HDR 游戏可能发灰/偏色，等 M2 的 linear/HDR 路径。
5. **4K 源**：2× 后是 8K，显存/带宽显著上升；M1b 仍只承诺常见 VN 分辨率（720p/1024×768/1080p）。脏矩形哈希 720p 约 1ms/帧，4K 源约 4 倍。
6. **黑边输入**：非 stretch（fit）时左右/上下黑边不转发鼠标点击和滚轮，这是预期行为，因为黑边不是源窗口像素；stretch 后原黑边区域被画面覆盖，会正常转发。
7. **swapchain resize**：M1a 在显示器变化时重建 overlay/blitter；源窗口 resize 会重建 capture，但显示模式本身的 swapchain recreation 仍不完整。
8. **单实例**：M1a 第二实例只提示错误；激活已有窗口是 M1c 设置项。
9. **设置界面**：M1a 没有完整设置界面；热键、日志路径、默认显示器等见 backlog/M1c。
10. **自定义 shader**：M1b 内置 S/M/L；自定义 shader 的运行时策略在 M1c 决定。
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
