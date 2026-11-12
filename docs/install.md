# 安装

## 1. 系统要求

| 项 | 要求 |
|---|---|
| 桌面 | **KDE Plasma 6**，**Wayland 会话**，并启用 **XWayland** |
| 系统 | 64 位 Linux；v1.0 在 Arch Linux 系 + KDE Plasma 6 上验证 |
| GPU | 支持 Vulkan 1.2 及以上的显卡与驱动；NVIDIA 专有驱动已验证 |
| 磁盘 | 预留约 300 MB（含私有 sway 运行时、着色器和 .NET 运行时） |
| 可选 | Lutris（用于嵌套启动）、`vulkaninfo`/`sway`（排障） |

v1.0 **不支持** KDE X11 会话、GNOME、Hyprland 等其他桌面。其他环境可能可以运行，但不在支持范围内。

检查当前会话：

```bash
echo "$XDG_SESSION_TYPE"      # 应为 wayland
echo "$XDG_CURRENT_DESKTOP"   # 应包含 KDE
echo "$DISPLAY"               # 应非空，表示 XWayland 可用
```

## 2. 解压

发布包是自包含的压缩包，不需要安装 .NET SDK，也不需要 root 权限。

```bash
tar -xzf GemPrismUpscaler-v1.0.0-linux-x64.tar.gz
cd GemPrismUpscaler-v1.0.0-linux-x64
```

解压后目录是扁平布局，主要文件：

```text
GemPrismUpscaler        主程序（双击启动）
gemprism.png            图标
runtime/sway/           私有嵌套合成器运行时
shaders/                内置超分着色器
docs/                   文档与示例
LICENSE / NOTICE        许可证与第三方说明
```

> 如果压缩包在传输过程中丢失了可执行权限，运行一次
> `chmod +x GemPrismUpscaler runtime/sway/sway runtime/sway/swaymsg`。

## 3. 首次运行

先做环境检查：

```bash
./GemPrismUpscaler --doctor
```

`[FAIL]` 表示存在阻塞项；`[WARN]` 通常可以继续。检查项包括 KDE/Wayland、XWayland、sway、装饰 shim、虚拟键盘、Vulkan、配置和日志目录。

然后启动界面：

```bash
./GemPrismUpscaler
```

也可以在文件管理器里双击 `GemPrismUpscaler`。首次启动使用内置默认值。保存设置或开始嵌套会话后才会写入配置文件；日志目录在启动时创建：

- 配置：`~/.config/GemPrismUpscaler/config.toml`
- 日志：`~/.local/share/GemPrismUpscaler/app.log`

如果双击没有反应，请从终端启动，终端会显示错误信息。

## 4. 可选：Lutris

嵌套启动可以直接运行 Lutris 命令。使用前请**完全退出 Lutris（包括托盘图标）**，否则 Lutris 会把游戏启动到已有实例里，而不是嵌套会话中。

示例：

```bash
./GemPrismUpscaler --nested --monitor HDMI-A-1 -- \
  env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/<id>
```

图形界面里也可以在“嵌套启动”命令框中填同样的命令。详见[快速开始](quickstart.md)和[使用说明](usage.md)。

## 5. 更新与卸载

- **更新**：用新版本目录替换旧目录即可。配置和日志在用户目录，不会随程序目录删除。
- **卸载**：删除解压出来的程序目录。若要一并清除个人数据，再删除
  `~/.config/GemPrismUpscaler` 和 `~/.local/share/GemPrismUpscaler`。

v1.0 不提供系统级安装、`.desktop` 文件、Flatpak 或 AUR 包。