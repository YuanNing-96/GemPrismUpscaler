# 宝石棱镜 · GemPrismUpscaler

> Linux 优先的通用窗口超分工具 · 独立进程 · 不注入游戏

宝石棱镜把低分辨率的游戏窗口实时超分到全屏显示。它支持任意分辨率与任意宽高比的组合，目标是通过可配置的 effect 管线组合 Anime4K、FSR 等算法。它不修改游戏、不注入 DLL、不依赖 gamescope，因此对 32/64 位、D3D9/11/12/OpenGL/Vulkan 游戏一视同仁。

当前版本是 **v0.2.0（M1a，已验收）**：在 M0 的捕获/呈现/输入/弹窗/生命周期之上，加入单实例、退出审计、多显示器基础（自动/手动选屏）和更完整的窗口选择。M1a 仍以 **Anime4K S + Lanczos-2** 和双线性回退为主；Anime4K M/L 计划在 M1b。

## 支持范围

| 维度 | 状态 |
|---|---|
| 操作系统 | **Linux：部分支持**（仅 X11/XWayland）；**Windows：暂不支持**；**macOS：暂不支持** |
| 桌面/会话 | **KDE Plasma 6 Wayland + XWayland：已验证**；KDE X11：理论可用、未验证；GNOME / wlroots（sway、Hyprland、niri 等）+ XWayland：未验证；**原生 Wayland 窗口：不支持**（M2） |
| 窗口类型 | X11 / XWayland 顶层窗口：支持；原生 Wayland 窗口：不支持 |
| 显示器 | 单显示器：支持；**多显示器：M1a 基础支持**（自动跟随游戏窗口 / 手动选择输出屏）；热插拔、每屏 DPI 等完整支持在 M2 |
| HDR | SDR 直通：支持（HDR 桌面下可正常显示 SDR 内容）；HDR 色彩管理：未做（M2） |
| 输入 | 视觉小说/2D 的鼠标点击、滚轮、键盘：支持；**FPS/鼠标视角/raw input：暂不支持** |
| GPU | NVIDIA 专有驱动：已验证；AMD/Intel Vulkan：理论可用、未验证；多 GPU/混合显卡：未验证 |
| 算法 | **Anime4K S 2× + Lanczos-2：支持**；双线性回退：支持；Anime4K M/L：计划 M1b；FSR1、NIS、Lanczos-3、Bicubic：未排期 |

更细的兼容性、已测试组合和已知限制见 [.doc/compatibility.md](.doc/compatibility.md)。

## 当前开发/验证环境

| 项 | 配置 |
|---|---|
| 发行版 | Arch Linux 系 |
| 桌面 | KDE Plasma 6，Wayland 会话 + XWayland |
| 显示 | HDMI-A-1 3840×2160@144Hz（HDR，150%，主屏）+ DP-1 1800×2880 竖屏（SDR，100%） |
| GPU | AMD iGPU + NVIDIA RTX 4060 Laptop 8 GB；effect 在 NVIDIA |
| .NET | 10.0.114 SDK |
| Vulkan | 1.4，NVIDIA 专有驱动 |
| 测试游戏 | 腐姬、秽翼的尤斯蒂娅（M0 验收） |

## 系统要求

- Linux，X11 或 XWayland；Vulkan 1.2 及以上
- .NET 10 SDK（仅构建需要；运行使用构建产物）
- 4K 输出建议 ≥2 GB 显存；M0/M1a 实测 4K 输出约 515 MiB
- 可选：`glslangValidator`、`spirv-val`（仅重新生成着色器时需要）
- 不再需要 CMake/C++ 原生库：X11 使用 P/Invoke，Vulkan 使用 Silk.NET

## 构建与测试

```bash
dotnet build GemPrismUpscaler.slnx -c Release
dotnet test  GemPrismUpscaler.slnx -c Release
```

重新生成着色器（需要 `glslangValidator` 与 `spirv-val`）：

```bash
python3 tools/build_shaders.py
```

运行程序不需要这两个工具：`.spv` 已预编译并嵌入 `GemPrismUpscaler.Core`。

## 使用

启动配置界面：

```bash
dotnet run --project src/GemPrismUpscaler -c Release
# 或
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler
```

界面里可以：

- 刷新窗口列表；列表会显示 PID、类名、分辨率、窗口 ID 和所在显示器；
- 选择“输出显示器”：**自动（跟随游戏窗口）** 或指定某块屏；
- 选择“超分算法”：Anime4K 2× + Lanczos 或双线性；
- 开始/停止超分；“跟随焦点”默认开启。

**单实例行为（M1a）**：同一时间只允许一个实例。第二个 GUI 实例会弹出“已在运行”的错误提示并退出，不会激活已有窗口；第二个 `--window` CLI 会向 stderr 报错并返回非 0。后续 M1c 设置界面会提供“第二实例报错时是否激活第一个窗口”的开关。

命令行模式：

```bash
# 列出显示器和可捕获窗口
./GemPrismUpscaler --list

# 查询当前是否已有实例在运行
./GemPrismUpscaler --status

# 自动跟随游戏窗口所在显示器
./GemPrismUpscaler --window 0x1200001 --effect anime4k

# 固定输出到某块显示器（名称来自 --list）
./GemPrismUpscaler --window 0x1200001 --monitor HDMI-A-1

# 其他
./GemPrismUpscaler --window 0x1200001 --stretch
./GemPrismUpscaler --window 0x1200001 --no-follow-focus
./GemPrismUpscaler --version
```

默认保持源宽高比（fit，4:3 源会有黑边）；`--stretch` 拉伸填满。`--monitor` 省略时自动选择窗口所在显示器；窗口跨屏移动时 overlay 会跟随重建。

日志：设置 `GEMPRISM_LOG=/path/app.log` 可指定日志文件；否则写入用户本地应用数据目录。日志第一行应包含：

```text
engine started version=0.2.0 effect=anime4k ... monitor=HDMI-A-1 ...
```

## 实现要点

- **捕获**：`XShmGetImage` 直接读 client window；KWin 已做合成重定向，被遮挡窗口仍能读到自己的画面。XDamage 用于“帧级跳过”：没有变化就不捕获、不提交，静止画面 GPU 接近空闲。
- **多显示器**：用 XRandR 1.5 枚举物理像素矩形；overlay 和 Vulkan swapchain 只覆盖选中的一块屏，`DestRect` 按该屏尺寸计算，输入坐标按该屏原点换算。自动模式下窗口跨屏移动会触发 overlay/swapchain 重建。
- **呈现**：NVIDIA Vulkan + Xlib swapchain；overlay 是 override-redirect 窗口。`--effect bilinear` 走单次 `vkCmdBlitImage`；默认 Anime4K 走 5 个 compute pass，最后用 graphics pass 做 Lanczos-2 并直接写入 swapchain（黑边由 render pass clear）。
- **输入**：overlay 接收真实鼠标，按输出内容矩形反算到源窗口坐标，再用 `XSendEvent` 发给游戏，并补发进入/离开源窗口的 crossing 事件；不使用点击穿透，也不依赖 XTest。
- **弹窗**：轮询 `_NET_CLIENT_LIST`，发现源进程额外的 viewable 顶层窗口就隐藏 overlay；关闭后恢复并强制重绘一帧。
- **生命周期**：源窗口最小化/unmap 时收起，恢复时重新捕获；源窗口被销毁时引擎自行停止。单实例使用 `$XDG_RUNTIME_DIR` 下的 Unix socket；退出路径覆盖窗口关闭、`SIGTERM`/`SIGINT`、游戏关闭和引擎异常。
- **跨平台**：`ICaptureBackend` / `IPresentBackend` / `IInputBackend` / `IEffectBackend` 的边界按平台无关设计；Linux 实现集中在 `Core/Platform/Linux`。

## 文档

- [设计文档](.doc/design.md) —— 需求、架构、技术选型、实现细节
- [路线图](.doc/roadmap.md) —— M0/M1/M2 阶段划分与验收标准
- [兼容性](.doc/compatibility.md) —— 支持矩阵、已测试环境、已知限制
- [功能 backlog](.doc/backlog.md) —— 未排期功能与设置界面需求
- [更新记录](.doc/changelog.md) —— 各版本修复、功能变更与已知限制
- [着色器说明](shaders/README.md) —— Anime4K 来源、MIT 许可与重新生成方式

## 致谢

本项目在架构上参考了 [Magpie](https://github.com/Blinue/Magpie) 的 effect 管线设计，超分算法来自 [Anime4K](https://github.com/bloc97/Anime4K)，几何模型借鉴了 Magpie 的 `ScalingType` 方案。

## 许可证

GNU Affero General Public License v3.0 or later（AGPL-3.0-or-later）。见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。
