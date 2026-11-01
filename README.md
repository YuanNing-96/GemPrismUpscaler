# Kaleido

> 面向 Linux 的通用窗口超分工具 · 独立进程 · 不注入游戏

Kaleido 把低分辨率的游戏窗口实时超分到全屏显示。它支持任意分辨率与任意宽高比的组合，内置 Anime4K、FSR 等算法，并允许用户用配置文件自由组合着色器管线。

它不修改游戏、不注入 DLL、不依赖 gamescope，因此对 32/64 位、D3D9/11/12/OpenGL/Vulkan 一视同仁，游戏更新也不会让它失效。

> 工作名，最终名称待定。

## 特性

- **独立进程捕获** —— 通过窗口系统抓取画面，不触碰目标进程
- **任意分辨率与比例** —— 源窗口和屏幕的宽高比可以完全不同，支持 fit / fill / crop / 绝对尺寸
- **可插拔 effect 管线** —— TOML 描述，支持 GLSL 与预编译 SPIR-V，可混用 OpenGL 与 Vulkan 后端
- **弹窗感知** —— 检测到游戏弹出对话框时自动让位，不会挡住存档或退出确认
- **进出动画** —— 画面从窗口位置平滑放大到全屏，退出时缩回原位（M1）
- **Wayland 优先的界面** —— Avalonia 12.1，无 Wayland 时自动回退 X11

## 状态

早期开发中，M0 尚未完成。当前目标平台：

| 项 | M0 范围 |
|---|---|
| 桌面环境 | KDE Plasma 6（Wayland 会话） |
| 发行版 | Arch Linux 系 |
| 窗口类型 | X11 / XWayland 顶层窗口 |
| 显示 | 单显示器，SDR，3840×2160 |
| 系统缩放 | 100% 与 150% |
| GPU | NVIDIA 独显，RTX 4060 Laptop 级及以上 |

X11 原生会话、原生 Wayland 窗口、多显示器、HDR、核显与混合显卡，均在 M0 范围之外，见[路线图](docs/roadmap.md)。

## 系统要求

- Linux，Vulkan 1.2 及以上
- 可选：`VK_EXT_external_memory_dma_buf` 与 `VK_EXT_image_drm_format_modifier`，用于零拷贝路径
- 构建：.NET 9 SDK、CMake 3.20+、支持 C++17 的编译器

## 构建

```bash
git clone <repo-url> kaleido
cd kaleido
dotnet build -c Release
```

原生辅助库（X11 捕获与 Vulkan 引导）：

```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
```

## 使用

```bash
# 启动界面
./kaleido

# 直接对指定窗口启用超分
./kaleido --window <window-id> --profile anime4k-2x
```

默认热键 `Alt+Shift+A` 切换超分开关，可在配置中修改。

## 文档

- [设计文档](docs/design.md) —— 需求、架构、技术选型、实现细节
- [路线图](docs/roadmap.md) —— 阶段划分与验收标准

## 致谢

本项目在架构上参考了 [Magpie](https://github.com/Blinue/Magpie) 的 effect 管线设计，超分算法来自 [Anime4K](https://github.com/bloc97/Anime4K)，几何模型借鉴了 Magpie 的 `ScalingType` 方案。

## 许可证

GPL-3.0-or-later。作为面向最终用户的应用，选择 copyleft 是为了保证任何衍生版本都回馈社区。若后续希望放宽，可在 1.0 前改为 MIT。
