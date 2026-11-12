# 宝石棱镜 GemPrismUpscaler 文档

> v1.0 只明确支持 **KDE Plasma 6 Wayland + XWayland**。

宝石棱镜是一个独立运行的窗口超分工具：它把低分辨率的游戏窗口实时放大到全屏，不修改游戏、不注入游戏进程。v1.0 的主路径是**嵌套启动**：在一个私有的虚拟桌面里启动游戏，再由宝石棱镜捕获、超分并显示。

## 新手入口

| 文档 | 内容 |
|---|---|
| [安装](install.md) | 解压、权限、首次运行、卸载 |
| [快速开始](quickstart.md) | 三分钟跑通一次超分 |
| [使用说明](usage.md) | 界面、三种启动方式、暂停/停止、托盘、热键 |
| [常见问题](troubleshooting.md) | 黑屏、启动失败、输入异常、日志与诊断包 |

## 参考

| 文档 | 内容 |
|---|---|
| [命令行](cli.md) | 参数、退出码、脚本示例 |
| [配置](configuration.md) | `config.toml` 全部字段、环境变量、Profile 说明 |
| [兼容性](compatibility.md) | 支持矩阵、已验证环境、输入/显示限制 |
| [已知问题](known-issues.md) | v1.0 不解决的问题与规避方式 |
| [更新记录](release-notes.md) | v1.0.0 与历史版本摘要 |
| [示例](examples/README.md) | 配置、Lutris 启动脚本、自定义 effect |

## 开发与历史资料

| 文档 | 内容 |
|---|---|
| [开发文档索引](dev/README.md) | 架构、路线图、M2 计划、诊断、测试、发布清单 |
| [架构设计](dev/architecture.md) | 设计目标、数据流、后端抽象、嵌套合成器 |
| [诊断与日志](dev/diagnostics.md) | 日志格式、轮转、诊断包、脱敏 |
| [开发与测试](dev/testing.md) | 构建、测试、着色器、嵌套运行时 |
| [历史资料](dev/history/README.md) | M0–M3 的技术记录与归档 |

## 支持范围速查

- **桌面**：KDE Plasma 6，Wayland 会话 + XWayland。
- **启动方式**：嵌套启动（推荐）、已有 X11/XWayland 窗口、已有原生 Wayland 全屏窗口。
- **输入**：视觉小说/2D 键鼠；FPS/鼠标视角/raw input 不支持。
- **显示**：单屏、多屏独立输出；不做跨屏拼接。HDR 桌面下 SDR 内容可正常显示，不做完整 HDR 捕获。
- **GPU**：NVIDIA 专有驱动已验证；AMD/Intel 未验证。

完整限制见[兼容性](compatibility.md)和[已知问题](known-issues.md)。