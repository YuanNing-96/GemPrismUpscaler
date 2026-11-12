# 宝石棱镜 · GemPrismUpscaler

> Linux 窗口超分工具 · 独立进程 · 不注入游戏 · v1.0

宝石棱镜把低分辨率的游戏窗口实时超分到全屏。它不修改游戏、不注入 DLL、不依赖 gamescope；对 32/64 位、D3D9/11/12/OpenGL/Vulkan 游戏一视同仁。

v1.0 的主路径是**嵌套启动**：在私有虚拟桌面里启动游戏，按窗口抓取，再以原生 Wayland 宿主窗口放大显示。适合窗口化运行的视觉小说和 2D 游戏。

## 支持范围

| 维度 | v1.0 |
|---|---|
| 桌面 | **KDE Plasma 6 Wayland + XWayland（唯一明确支持）** |
| 启动方式 | 嵌套启动（推荐）、已有 X11/XWayland 窗口、已有原生 Wayland 全屏窗口 |
| 输入 | 视觉小说/2D 键鼠；FPS/鼠标视角/raw input **不支持** |
| 显示器 | 单屏、多屏独立输出；不做跨屏拼接 |
| HDR | HDR 桌面下 SDR 直通；完整 HDR 捕获/10-bit 输出不做 |
| GPU | NVIDIA 专有驱动已验证；AMD/Intel 未验证 |
| 算法 | Anime4K S/M/L/VL 2× + Lanczos-2；双线性回退 |
| 分发 | 解压即用压缩包；不提供 Flatpak、AUR、安装脚本或 `.desktop` |

完整矩阵见 [docs/compatibility.md](docs/compatibility.md)，已知限制见 [docs/known-issues.md](docs/known-issues.md)。

## 下载与运行

发布包为自包含压缩包，不需要安装 .NET SDK，也不需要 root 权限。

```bash
tar -xzf GemPrismUpscaler-v1.0.0-linux-x64.tar.gz
cd GemPrismUpscaler-v1.0.0-linux-x64
./GemPrismUpscaler --doctor
./GemPrismUpscaler
```

也可以在文件管理器里双击 `GemPrismUpscaler`。如果双击没有反应，请从终端启动查看错误。

## 快速开始

1. 启动后保持默认的“嵌套启动”。
2. 完全退出 Lutris（包括托盘）。
3. 在命令框填入：

   ```text
   env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/3
   ```

4. 点“开始超分”。
5. 用“暂停超分/继续超分”切回原窗口或放大画面；用“停止超分”结束会话。

已有窗口：把启动方式改为“已有游戏窗口”，选 X11 窗口，或选“Wayland 全屏窗口”后用系统选择器选择。

三分钟版本见 [docs/quickstart.md](docs/quickstart.md)，详细操作见 [docs/usage.md](docs/usage.md)。

## 从源码构建

需要 .NET 10 SDK、Vulkan 1.2+；重新生成着色器还需要 `glslangValidator`/`spirv-val`。

```bash
dotnet build GemPrismUpscaler.slnx -c Release
dotnet test  GemPrismUpscaler.slnx -c Release
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler
```

当前基线：0 warning / 0 error，测试 150/150。

## 文档

| 文档 | 内容 |
|---|---|
| [安装](docs/install.md) | 解压、权限、首次运行、卸载 |
| [快速开始](docs/quickstart.md) | 三分钟跑通一次超分 |
| [使用说明](docs/usage.md) | 界面、启动方式、暂停/停止、托盘、热键 |
| [命令行](docs/cli.md) | 参数、退出码、脚本示例 |
| [配置](docs/configuration.md) | `config.toml`、环境变量、Profile 现状 |
| [兼容性](docs/compatibility.md) | 支持矩阵、已验证环境、性能参考 |
| [常见问题](docs/troubleshooting.md) | 黑屏、启动失败、输入异常、诊断 |
| [已知问题](docs/known-issues.md) | v1.0 不解决的问题与规避 |
| [更新记录](docs/release-notes.md) | v1.0.0 与历史版本摘要 |
| [示例](docs/examples/README.md) | 配置、Lutris 脚本、自定义 effect |
| [开发文档](docs/dev/README.md) | 架构、路线图、诊断、测试、发布清单 |
| [历史资料](docs/dev/history/README.md) | M0–M3 技术记录与归档 |

## 实现要点

- **嵌套合成器**：私有 sway + headless 单输出，按目标显示器物理分辨率/刷新率创建；`ext-image-copy-capture` 按 toplevel 抓帧。
- **宿主窗口**：原生 Wayland `xdg_toplevel`，装饰、viewport、分数缩放；对话框独立宿主；关闭请求转发给游戏窗口。
- **超分**：Vulkan compute；Anime4K S/M/L/VL 由 manifest 驱动 N-pass，Lanczos-2 收尾；双线性单次 blit。
- **脏矩形**：64px 分块哈希 + CNN halo，静止画面不重复捕获/呈现。
- **输入**：嵌套内虚拟指针/键盘；X11/XWayland 窗口用坐标反算 + `XSendEvent`；原生 Wayland 全屏用输入穿透。
- **诊断**：session header、5 MB × 3 日志轮转、脱敏诊断包、崩溃快照。

## 许可证

GNU Affero General Public License v3.0 or later（AGPL-3.0-or-later）。见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。

第三方组件与参考项目见 [docs/dev/third-party.md](docs/dev/third-party.md)。架构参考 [Magpie](https://github.com/Blinue/Magpie)，超分算法来自 [Anime4K](https://github.com/bloc97/Anime4K)。