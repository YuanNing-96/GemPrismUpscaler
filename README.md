# 宝石棱镜 · GemPrismUpscaler

> Linux 优先的通用窗口超分工具 · 独立进程 · 不注入游戏

宝石棱镜把低分辨率的游戏窗口实时超分到全屏显示。它支持任意分辨率与任意宽高比的组合，目标是通过可配置的 effect 管线组合 Anime4K、FSR 等算法。它不修改游戏、不注入 DLL、不依赖 gamescope，因此对 32/64 位、D3D9/11/12/OpenGL/Vulkan 游戏一视同仁。

当前版本是 **v0.1.2（M0 完成）**：捕获 → 全屏呈现 → 输入/弹窗/生命周期 → **Anime4K 2× CNN + Lanczos-2 收尾** 已经跑通；超分算法可在 Anime4K 与双线性之间切换。M0a、M0b 和真实游戏验收均已完成；FSR1 与混合 GL+Vulkan 管线不在 M0 范围。

## 当前状态（2026-11-02）

| 项 | 状态 |
|---|---|
| M0 阶段（M0a + M0b） | ✅ 完成（v0.1.2） |
| X11/XWayland 窗口枚举 | ✅ |
| XShm 直接捕获（被遮挡窗口也可读） | ✅ |
| 3840×2160 override-redirect 全屏 overlay | ✅ |
| NVIDIA Vulkan 呈现（compute + graphics pipeline） | ✅ |
| 鼠标坐标映射 + `XSendEvent` 转发 | ✅ |
| 启动/恢复时把键盘焦点交还游戏（EWMH 激活） | ✅ |
| 焦点跟随：切到其他程序自动收起 | ✅ |
| `Alt+Shift+A` 显示/隐藏 | ✅ |
| 弹窗检测与让位（同 PID、viewable 顶层窗口） | ✅ |
| 游戏最小化/关闭时收起覆盖层并重置 UI | ✅ |
| Avalonia 最小 UI（窗口选择/开始/停止/退出） | ✅ |
| 画面变化时 60fps（1280×720→3840×2160 合成测试） | ✅ |
| 静止画面不重复捕获/呈现 | ✅ |
| Anime4K 2× CNN（S）+ Lanczos-2 收尾 | ✅ v0.1.2 |
| 双线性回退（M0a 路径，`--effect bilinear`） | ✅ |
| 离线 GLSL → SPIR-V 构建（`tools/build_shaders.py`） | ✅ |
| FSR1 / 混合 GL+Vulkan 管线 | ⏳ M0 之后 |
| 真实游戏验收（腐姬 / 秽翼的尤斯蒂娅） | ✅ 2026-11-02 复测通过 |

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

重新生成着色器（需要 `glslangValidator` 与 `spirv-val`）：

```bash
python3 tools/build_shaders.py
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
./GemPrismUpscaler --window 0x1200001 --effect anime4k      # 默认
./GemPrismUpscaler --window 0x1200001 --effect bilinear     # M0a 双线性
./GemPrismUpscaler --window 0x1200001 --no-follow-focus
./GemPrismUpscaler --version
```

默认保持源宽高比（fit，4:3 源会有左右黑边）；`--stretch` 拉伸填满。

界面里的“超分算法”默认 Anime4K 2× + Lanczos，也可切回双线性对比。“跟随焦点”默认开启：切到其他程序时覆盖层收起，回到游戏时恢复；`--no-follow-focus` 可关闭。

日志：设置 `GEMPRISM_LOG=/path/app.log` 可指定日志文件；否则写入用户本地应用数据目录。

## 实现要点

- **捕获**：`XShmGetImage` 直接读 client window；KWin 已做合成重定向，被遮挡窗口仍能读到自己的画面。XDamage 用于“帧级跳过”：没有变化就不捕获、不提交，静止画面 GPU 接近空闲。
- **呈现**：NVIDIA Vulkan + Xlib swapchain；overlay 是 override-redirect 全屏窗口。`--effect bilinear` 走单次 `vkCmdBlitImage`；默认 Anime4K 走 5 个 compute pass，最后用 graphics pass 做 Lanczos-2 并直接写入 swapchain（黑边由 render pass clear）。启动、热键恢复、点击时通过 EWMH `_NET_ACTIVE_WINDOW` 把焦点交还游戏；游戏失焦时自动收起（可关闭）。
- **超分**：Anime4K `Upscale_CNN_x2_S` 的 mpv 着色器由 `tools/build_shaders.py` 展开为 5 个独立 compute shader（4 层 3×3 CNN + Depth-to-Space 残差），中间纹理为 `rgba16f`；再用 Lanczos-2（4×4 taps）从 2× 结果重采样到最终内容矩形。SPIR-V 离线编译并作为嵌入资源随程序分发，运行时不依赖 glslang。
- **输入**：overlay 接收真实鼠标，按输出内容矩形反算到源窗口坐标，再用 `XSendEvent` 发给游戏，并补发进入/离开源窗口的 crossing 事件；不使用点击穿透（点击会落到原窗口之外的桌面），也不依赖 XTest（KWin EIS 会弹授权且要移动真实指针）。
- **弹窗**：轮询 `_NET_CLIENT_LIST`，发现源进程额外的 viewable 顶层窗口就隐藏 overlay；关闭后恢复并强制重绘一帧。当前依赖 `_NET_WM_PID`，后续可补 XRes。
- **生命周期**：源窗口最小化/unmap 时收起，恢复时重新捕获；源窗口被销毁时引擎自行停止，UI 会复位按钮并显示原因。
- **跨平台**：`ICaptureBackend` / `IPresentBackend` / `IInputBackend` / `IEffectBackend` 的边界已按平台无关设计；Linux 实现集中在 `Core/Platform/Linux`。

## 文档

- [设计文档](.doc/design.md) —— 需求、架构、技术选型、实现细节
- [路线图](.doc/roadmap.md) —— 阶段划分与验收标准
- [更新记录](.doc/changelog.md) —— 各版本修复、功能变更与已知限制
- [着色器说明](shaders/README.md) —— Anime4K 来源、MIT 许可与重新生成方式

## 致谢

本项目在架构上参考了 [Magpie](https://github.com/Blinue/Magpie) 的 effect 管线设计，超分算法来自 [Anime4K](https://github.com/bloc97/Anime4K)，几何模型借鉴了 Magpie 的 `ScalingType` 方案。

## 许可证

GPL-3.0-or-later。见 [LICENSE](LICENSE)。