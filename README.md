# 宝石棱镜 · GemPrismUpscaler

> Linux 优先的通用窗口超分工具 · 独立进程 · 不注入游戏

宝石棱镜把低分辨率的游戏窗口实时超分到全屏显示。它支持任意分辨率与任意宽高比的组合，目标是通过可配置的 effect 管线组合 Anime4K、FSR 等算法。它不修改游戏、不注入 DLL、不依赖 gamescope，因此对 32/64 位、D3D9/11/12/OpenGL/Vulkan 游戏一视同仁。

当前版本是 **M0a 纵向切片**：捕获 → 全屏呈现 → 鼠标/键盘 → 弹窗让位 → 最小 UI 已经跑通；超分效果目前是 Vulkan 线性重采样，Anime4K/FSR 管线（M0b）尚未接入。

## 当前状态（2026-11-02）

| 项 | 状态 |
|---|---|
| X11/XWayland 窗口枚举 | ✅ |
| XShm 直接捕获（被遮挡窗口也可读） | ✅ |
| 3840×2160 override-redirect 全屏 overlay | ✅ |
| NVIDIA Vulkan 呈现（vkCmdBlitImage） | ✅ |
| 鼠标坐标映射 + `XSendEvent` 转发 | ✅ |
| 键盘焦点保持在游戏 | ✅ |
| `Alt+Shift+A` 显示/隐藏 | ✅ |
| 弹窗检测与让位（同 PID 顶层窗口） | ✅ |
| Avalonia 最小 UI（窗口选择/开始/停止/退出） | ✅ |
| 画面变化时 60fps（1280×720→3840×2160 合成测试） | ✅ |
| 静止画面不重复捕获/呈现 | ✅ |
| Anime4K / FSR / 混合 GL+Vulkan 管线 | ⏳ M0b |
| 真实游戏验收（腐姬 / 秽翼的尤斯蒂娅） | ⏳ 待用户验证 |

## 目标环境

| 项 | M0 范围 |
|---|---|
| 桌面环境 | KDE Plasma 6（Wayland 会话） |
| 发行版 | Arch Linux 系 |
| 窗口类型 | X11 / XWayland 顶层窗口 |
| 显示 | 单显示器，SDR，3840×2160，150% |
| GPU | 混合显卡：AMD iGPU + NVIDIA RTX 4060 Laptop；effect 使用 NVIDIA |
| 游戏 | 视觉小说/2D（事件驱动输入） |

X11 原生会话、原生 Wayland 窗口、多显示器、HDR、FPS/鼠标视角/raw input 游戏均在 M0 范围之外。Windows 为后续目标，Core 的捕获/呈现/输入接口按平台无关设计，M0 只实现 Linux 后端。

## 系统要求

- Linux，Vulkan 1.2 及以上，XWayland
- .NET 10 SDK（构建）
- 可选：`VK_EXT_external_memory_dma_buf` 与 `VK_EXT_image_drm_format_modifier`（后续零拷贝路径）
- 不再需要 CMake/C++ 原生库：X11 使用 P/Invoke，Vulkan 使用 Silk.NET

## 构建与测试

```bash
dotnet build GemPrismUpscaler.slnx -c Release
dotnet test GemPrismUpscaler.slnx
```

## 使用

启动配置界面：

```bash
dotnet run --project src/GemPrismUpscaler -c Release
# 或
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler
```

在界面里刷新窗口、选择游戏窗口、点击“开始超分”。覆盖层出现后，`Alt+Shift+A` 显示/隐藏；停止/退出可在界面或热键后操作。

命令行模式（便于脚本/验证）：

```bash
# 列出可捕获窗口
./GemPrismUpscaler --list

# 直接对指定窗口启动（十六进制或十进制窗口 ID）
./GemPrismUpscaler --window 0x1200001
./GemPrismUpscaler --window 0x1200001 --stretch
```

默认保持源宽高比（fit，4:3 源会有左右黑边）；`--stretch` 拉伸填满。

日志：设置 `GEMPRISM_LOG=/path/app.log` 可指定日志文件；否则写入用户本地应用数据目录。

## 实现要点

- **捕获**：`XShmGetImage` 直接读 client window；KWin 已做合成重定向，被遮挡窗口仍能读到自己的画面。XDamage 用于“帧级跳过”：没有变化就不捕获、不提交，静止画面 GPU 接近空闲。
- **呈现**：NVIDIA Vulkan + Xlib swapchain，`vkCmdBlitImage` 线性缩放到 3840×2160；overlay 是 override-redirect 全屏窗口，不抢焦点。
- **输入**：overlay 接收真实鼠标，按输出内容矩形反算到源窗口坐标，再用 `XSendEvent` 发给游戏；不使用点击穿透（点击会落到原窗口之外的桌面），也不依赖 XTest（KWin EIS 会弹授权且要移动真实指针）。
- **弹窗**：轮询 `_NET_CLIENT_LIST`，发现源进程的额外顶层窗口就隐藏 overlay；关闭后恢复。当前依赖 `_NET_WM_PID`，后续可补 XRes。
- **跨平台**：`ICaptureBackend` / `IPresentBackend` / `IInputBackend` / `IEffectBackend` 的边界已按平台无关设计；Linux 实现集中在 `Core/Platform/Linux`。

## 文档

- [设计文档](.doc/design.md) —— 需求、架构、技术选型、实现细节
- [路线图](.doc/roadmap.md) —— 阶段划分与验收标准

## 致谢

本项目在架构上参考了 [Magpie](https://github.com/Blinue/Magpie) 的 effect 管线设计，超分算法来自 [Anime4K](https://github.com/bloc97/Anime4K)，几何模型借鉴了 Magpie 的 `ScalingType` 方案。

## 许可证

GPL-3.0-or-later。见 [LICENSE](LICENSE)。