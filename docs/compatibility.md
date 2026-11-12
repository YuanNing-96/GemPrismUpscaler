# 兼容性

> v1.0 只明确支持 **KDE Plasma 6 Wayland + XWayland**。其他桌面环境可能可以运行，但不在支持范围内。已知限制见[已知问题](known-issues.md)。

## 1. 支持矩阵

| 维度 | v1.0 状态 | 说明 |
|---|---|---|
| Linux x86_64 | 支持 | 发布包为 64 位 Linux 构建。 |
| KDE Plasma 6 + Wayland | **支持（主要目标）** | 已验证 Wayland 会话 + XWayland。 |
| KDE Plasma X11 | 不支持 | 不保证可用，v1.0 不做验收。 |
| GNOME / wlroots 桌面（Hyprland、niri、sway 等） | 不支持 | 依赖 KDE 的窗口/呈现行为；未验收。 |
| Windows / macOS | 不支持 | 没有对应后端。 |
| 嵌套启动 | **推荐** | 私有虚拟桌面 + 按窗口捕获；窗口化视觉小说/2D 游戏的主路径。 |
| 已有 X11/XWayland 窗口 | 支持 | 窗口可以窗口化或全屏；输入转发。 |
| 已有原生 Wayland 窗口 | 有限支持 | 通过系统选择器捕获；只支持**全屏**窗口，输入穿透，窗口化原生 Wayland 请用嵌套。 |
| 单显示器 | 支持 | |
| 多显示器 | 支持（独立输出） | 每个会话覆盖一块屏；不做跨屏连续拼接。 |
| 显示器热插拔/分辨率变化 | 部分支持 | 运行中的嵌套会话不跟随；重新开始会话后生效。 |
| HDR 桌面 | SDR 直通 | HDR 桌面上显示 SDR 内容不发灰；不做完整 HDR 游戏捕获/10-bit 输出。 |
| 视觉小说/2D 键鼠 | 支持 | X11/XWayland 转发；嵌套原生 Wayland 绝对/相对指针；虚拟键盘。 |
| FPS/鼠标视角/raw input | **不支持** | XWayland 指针锁/relative/raw 未实现；FPS/3D 游戏请勿按此用途验收。 |
| 热键 | 支持（KDE） | 固定 `Alt+Shift+A`；v1.0 不支持修改。 |
| NVIDIA 专有驱动 | 已验证 | Vulkan 1.4。 |
| AMD / Intel Vulkan | 未验证 | 代码按标准 Vulkan 编写，但没有实机验收。 |
| 混合显卡 | 部分支持 | 捕获走 CPU，effect 选择独立 GPU；跨 GPU 路径未验证。 |
| Anime4K S/M/L/VL 2× + Lanczos-2 | 支持 | VL 为实验最高画质档，帧率不作为保证。 |
| 双线性 | 支持 | 回退/对比档。 |
| 自定义 effect | 支持（高级） | TOML + 预编译 SPIR-V；GUI 不展示管理页。 |
| 分块脏矩形 | 支持 | 默认开启；静止画面不重复捕获/呈现。 |
| 托盘 | 可选 | 依赖 StatusNotifierItem/AppIndicator；不可用时自动降级。 |
| 动画 | 不支持 | 进出超分不做过渡动画。 |
| FSR1 / NIS / Lanczos-3 / Bicubic | 未排期 | v1.0 不包含。 |

## 2. 已验证环境

| 项 | 配置 |
|---|---|
| 发行版 | Arch Linux 系 |
| 桌面 | KDE Plasma 6，Wayland 会话 + XWayland |
| 显示器 | 3840×2160@144Hz（HDR、150%）+ 1800×2880 竖屏（SDR、100%） |
| GPU | AMD iGPU + NVIDIA RTX 4060 Laptop 8 GB；effect 运行在 NVIDIA |
| Vulkan | 1.4，NVIDIA 专有驱动 |
| .NET | 10.0.114 SDK（仅构建需要） |

## 3. 已验证的行为

- 1280×720 → 3840×2160：真实 Vulkan 游戏 + Anime4K L 稳定约 59.5–60.7 fps。
- 4:3 源 fit 到 16:9 屏：内容居中，左右黑边为纯黑；`stretch` 拉伸铺满。
- 静止画面：首次呈现后 0 capture / 0 present；每帧重绘但像素不变时 captured≈60、presented=0。
- 弹窗、关闭弹窗、游戏退出、热键隐藏/恢复、单实例、跨屏跟随。
- 嵌套模式下的鼠标悬停/点击/滚轮/键盘、对话框、窗口尺寸变化、光标跳转。
- HDR 桌面下 SDR 内容可正常显示，不发灰；不做 HDR 色彩管理。
- 4K 输出下 Anime4K S/M/L/VL 均可启动；VL 的首帧和显存开销明显更高。

## 4. 性能参考

| 场景 | 结果 |
|---|---|
| 720p 源 → 4K，Anime4K L，真实游戏 | 约 59.5–60.7 fps |
| 720p 源 → 4K，Anime4K S/M/L 全帧（测试图案） | GPU effect 约 0.36 / 0.79 / 1.11 ms |
| 720p 源 → 4K，Anime4K VL | 约 880 MiB 整卡显存（含桌面），帧率不作为门槛 |
| 静态画面 | 0 capture / 0 present，GPU 接近空闲 |

软件测试图案在 4K 下的 capture 约 23 ms，只有 37–38 fps；这不代表真实游戏的上限。

## 5. 平台行为

- XWayland 使用物理像素：150% 缩放下 root 仍是 3840×2160，overlay 按物理矩形布局。
- 嵌套会话的虚拟桌面取所选显示器的物理分辨率和刷新率。
- 原生 Wayland 没有通用窗口枚举；已有窗口模式必须用系统选择器。
- KDE 下窗口激活不可靠，重复打开时“激活已有窗口”使用 KWin 脚本兜底。
- 热键冲突时（`Alt+Shift+A` 被系统占用），请在 KDE 系统设置里释放冲突。