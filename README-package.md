# 宝石棱镜 GemPrismUpscaler v1.0.0

> 独立窗口超分工具 · KDE Plasma 6 Wayland + XWayland

宝石棱镜把低分辨率的游戏窗口实时超分到全屏。它不修改游戏、不注入游戏进程。v1.0 推荐用**嵌套启动**运行窗口化的视觉小说/2D 游戏。

## 运行

```bash
./GemPrismUpscaler --doctor
./GemPrismUpscaler
```

也可以在文件管理器里双击 `GemPrismUpscaler`。首次启动使用内置默认值；保存设置或开始嵌套会话后才会写入 `~/.config/GemPrismUpscaler/config.toml`。

## 快速开始

1. 启动后保持“嵌套启动”。
2. 完全退出 Lutris（包括托盘）。
3. 在命令框填入：

   ```text
   env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/<id>
   ```

4. 点“开始超分”；用“暂停超分/继续超分”切换原窗口，用“停止超分”结束会话。

已有窗口：把启动方式改为“已有游戏窗口”，选 X11 窗口，或选“Wayland 全屏窗口”后用系统选择器选择。

## 说明

- 只明确支持 KDE Plasma 6 Wayland + XWayland。
- 视觉小说/2D 键鼠支持；FPS/鼠标视角/raw input 不支持。
- HDR 桌面下 SDR 直通；不做完整 HDR 捕获。
- 发布包自带 .NET 运行时、私有 sway、wlroots、装饰 shim 和虚拟键盘 helper。
- 程序目录内的 `runtime/` 不要删除。

## 文档

- [文档索引](docs/README.md)
- [安装与卸载](docs/install.md)
- [快速开始](docs/quickstart.md)
- [使用说明](docs/usage.md)
- [命令行](docs/cli.md)
- [配置](docs/configuration.md)
- [兼容性](docs/compatibility.md)
- [常见问题](docs/troubleshooting.md)
- [已知问题](docs/known-issues.md)
- [示例配置与 Lutris 脚本](docs/examples/)

## 许可证

GNU Affero General Public License v3.0 or later。见 `LICENSE`、`NOTICE`、`SOURCE.txt` 和 `licenses/`。