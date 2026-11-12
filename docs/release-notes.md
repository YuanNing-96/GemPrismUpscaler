# 更新记录

## v1.0.0 · 首个正式版本

v1.0 面向 **KDE Plasma 6 Wayland + XWayland**，主路径是“私有嵌套桌面 + 按窗口捕获”的视觉小说/2D 游戏超分。发布物是可解压即用的压缩包，不需要安装器、root 权限或 .NET SDK。

### 界面

- 主窗口收敛为 **超分 / 设置 / 日志** 三个页签；隐藏 Profile/效果管理页。
- “超分”页统一为：算法、输出显示器、启动方式（嵌套启动 / 已有游戏窗口）、窗口类型（X11 / Wayland 全屏）。
- 按钮改为 **开始超分 / 停止超分 / 暂停超分**；嵌套模式下暂停即切回游戏原窗口。
- 默认启动方式为“嵌套启动”，命令框支持引号、不经过 shell。
- 设置页只保留常用项：重复打开行为、关闭行为、托盘、刷新间隔、日志位置；热键只读显示，v1.0 不支持修改。
- 托盘图标使用 `gemprism.png`；关闭窗口可退出或最小化到托盘。

### 命令行

- 新增 `--doctor`：检查 KDE/Wayland/XWayland、sway/swaymsg、装饰 shim、虚拟键盘、Vulkan、配置和日志目录。
- 新增 `--config-check`：只加载配置并打印路径、schema、警告和错误。
- 新增 `--diagnose`：导出脱敏诊断 zip。
- 新增 `--wayland-window` / `--wayland-monitor`；旧 `--portal-*` 仍兼容但不再宣传。
- `--help` 重整为 v1.0 用户参数，退出码文档化（0/1/2/3/4）。
- `--nested` 命令参数按 argv 解析，支持引号；`--nested-size` 已废弃并忽略。
- 嵌套抓取默认使用 `ext-image-copy-capture` 按 toplevel 抓帧；`GEMPRISM_TOPLEVEL_CAPTURE=0` 可回退旧输出裁剪。

### 诊断与稳定性

- GUI/CLI 启动写 session header：版本、模式、参数、配置路径/schema、会话类型。
- 日志按 5 MB × 3 轮转；写入失败回退临时日志。
- 诊断包包含环境、脱敏配置、日志尾部、最近错误、嵌套 sway stderr/状态和 stale runtime 目录。
- 未处理异常写 `crash-*.txt` 快照。
- 修复 GUI 嵌套启动未真正执行命令、暂停按钮找不到宿主窗口、正常游戏退出被误报为启动失败。

### 文档

- 文档从隐藏目录迁移到可见的 `docs/`，分为用户文档、开发文档和历史资料。
- 新增安装、快速开始、使用、配置、兼容性、排障、已知问题文档。
- 新增完整配置、Lutris 嵌套脚本和自定义 effect 示例。
- 统一清理了历史文档中的测试游戏名和个人路径。

### 兼容性

- 只明确支持 KDE Plasma 6 Wayland + XWayland。
- 不支持 FPS/鼠标视角/raw input、完整 HDR 捕获、跨屏拼接、DMA-BUF 零拷贝、混合 GL+Vulkan。
- AMD/Intel GPU 未实机验证；NVIDIA 专有驱动已验证。

完整限制见[已知问题](known-issues.md)和[兼容性](compatibility.md)。

历史版本的逐项修复记录见[历史更新记录](dev/history/changelog.md)。

---

## 历史版本摘要

| 版本 | 阶段 | 主要内容 |
|---|---|---|
| 0.5.0 | M2b | 单输出嵌套、按 toplevel 抓取、原生 Wayland 宿主窗口、光标跳转 |
| 0.4.0 | M2a | Wayland layer-shell、XWayland 混合、多屏、portal 捕获 |
| 0.3.0 | M1c | 设置、托盘、Profile、自定义 effect、Anime4K VL |
| 0.2.1 | M1b | 指标、Anime4K S/M/L、脏矩形、零拷贝 spike |
| 0.2.0 | M1a | 单实例、退出审计、多显示器基础 |
| 0.1.2 | M0b | Anime4K S 2× + Lanczos-2 收尾 |
| 0.1.x | M0a | 捕获、4K overlay、输入转发、弹窗让位、生命周期、UI |