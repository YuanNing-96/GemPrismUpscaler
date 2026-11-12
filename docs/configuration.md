# 配置

## 1. 文件位置

默认配置：

```text
~/.config/GemPrismUpscaler/config.toml
```

可用 `GEMPRISM_CONFIG` 指向另一个文件；effects 目录会跟着配置目录走：

```bash
GEMPRISM_CONFIG=/path/to/config.toml ./GemPrismUpscaler
```

首次启动使用内置默认值。**保存设置**、开始嵌套会话或勾选“记住上次选择”时才会写入文件。文件不存在时 `--config-check` 会显示 `existed: no`，这是正常的。

配置文件是 TOML。GUI 只暴露常用项，其余高级项可以直接编辑；改完可运行：

```bash
./GemPrismUpscaler --config-check
```

它只加载配置，打印路径、schema、警告和错误，不启动会话。

## 2. 最小示例

```toml
schema = 1

[effect]
default = "anime4k-s"
scaling = "fit"

[display]
monitor = "auto"

[nested]
command = "env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/<id>"
```

完整的、可复制的配置见[examples/config.toml](examples/config.toml)。

## 3. 字段参考

### `[general]`

| 键 | 默认 | 说明 |
|---|---|---|
| `second_instance` | `"notify"` | 再次打开 GUI 时：`notify` 弹窗提示，`activate` 激活已有窗口。 |
| `close_action` | `"exit"` | 关闭主窗口：`exit` 直接退出，`tray` 最小化到托盘。 |
| `follow_focus` | `true` | 是否在游戏失去焦点时收起覆盖层。 |
| `remember_last` | `true` | 记住上次成功使用的算法、缩放和显示器。 |
| `auto_refresh_windows` | `false` | 窗口列表自动刷新；GUI 每次启动会强制开启。 |
| `refresh_interval_ms` | `1000` | 自动刷新间隔，限制为 250–5000。 |

### `[hotkey]`

| 键 | 默认 | 说明 |
|---|---|---|
| `toggle_overlay` | `"Alt+Shift+A"` | v1.0 固定值，GUI 会归一化回默认；暂不支持修改。 |

### `[logging]`

| 键 | 默认 | 说明 |
|---|---|---|
| `path` | `""` | 空表示 `~/.local/share/GemPrismUpscaler/app.log`。 |
| `level` | `"info"` | `debug` / `info` / `warn` / `error` / `off`。`debug` 日志量很大。 |

### `[display]`

| 键 | 默认 | 说明 |
|---|---|---|
| `monitor` | `"auto"` | 输出显示器名称（来自 `--list`）或 `auto`。 |

### `[effect]`

| 键 | 默认 | 说明 |
|---|---|---|
| `default` | `"anime4k-s"` | `anime4k-s/m/l/vl`、`bilinear` 或自定义 effect id。 |
| `scaling` | `"fit"` | `fit` 保持比例留黑边，`stretch` 拉伸铺满。 |
| `dirty_rect` | `true` | 分块脏矩形优化；一般保持开启。 |

### `[capture]`（高级）

| 键 | 默认 | 说明 |
|---|---|---|
| `backend` | `"auto"` | `auto` / `x11` / `wayland`，呈现后端。 |
| `mode` | `"auto"` | `auto` / `x11` / `portal`，捕获方式。 |

v1.0 正常使用保持 `auto`。这些开关主要用于兼容旧路径和排障。

### `[tray]`

| 键 | 默认 | 说明 |
|---|---|---|
| `enabled` | `true` | 是否创建托盘图标。 |

### `[input]`（高级）

| 键 | 默认 | 说明 |
|---|---|---|
| `mode` | `"2d"` | v1.0 只支持 `2d`；`fps`/raw input 未实现。 |
| `pointer_mode` | `"auto"` | `auto` / `passthrough` / `forward`，Wayland 指针处理方式。 |

### `[diagnostics]`（开发）

| 键 | 默认 | 说明 |
|---|---|---|
| `developer_mode` | `false` | 强制 debug 日志和更详细的错误。普通用户保持关闭。 |

### `[nested]`

| 键 | 默认 | 说明 |
|---|---|---|
| `command` | `""` | GUI “嵌套启动”使用的命令，按 argv 解析、不经过 shell。建议先用 GUI 填写并保存一次。 |

### `[last]`（内部）

```toml
[last]
effect = "anime4k-s"
scaling = "fit"
monitor = "HDMI-A-1"
```

由“记住上次选择”自动维护，一般不要手改。

## 4. Profile 的现状

`config.toml` 仍可以包含 `[[profile]]`，加载和校验也会保留它们，但 **v1.0 不会根据窗口自动套用 Profile**。请直接在“超分”页手动选择算法/缩放/显示器。

Profile 的字段和匹配规则保留给后续版本：

```toml
[[profile]]
name = "visual-novel"
class = ["game-class*"]
exe = ["game.exe"]
effect = "anime4k-m"
scaling = "fit"
monitor = "auto"
follow_focus = true
dirty_rect = true
hotkey = "Alt+Shift+A"
```

匹配字段：

- `class`：WM_CLASS，支持 shell 风格 `*` / `?`。
- `exe`：`/proc/<pid>/exe` 或 Wine 命令行里的 `.exe` 名。

多段 Profile 按文件顺序先匹配先赢。示例见[visual-novel.toml](examples/visual-novel.toml)。

## 5. 环境变量

普通用户通常只需要前三个。

| 变量 | 用途 |
|---|---|
| `GEMPRISM_CONFIG` | 覆盖配置文件路径（同时改变 effects 目录）。 |
| `GEMPRISM_LOG` | 覆盖日志文件路径。 |
| `GEMPRISM_LOG_LEVEL` | 覆盖日志级别。 |
| `GEMPRISM_STATS` | 每秒写一行指标 CSV，供性能分析。 |
| `GEMPRISM_TOPLEVEL_CAPTURE` | 嵌套抓取方式；设为 `0` 回退到旧的输出裁剪（排障用）。 |
| `GEMPRISM_SWAY` / `GEMPRISM_SWAYMSG` | 覆盖私有 sway / swaymsg 路径。 |
| `GEMPRISM_DECORATION_SHIM` | 覆盖装饰 shim 路径。 |
| `GEMPRISM_VKEYBOARD` | 覆盖虚拟键盘 helper 路径。 |
| `GEMPRISM_WLR_RENDER_DRM_DEVICE` | 指定嵌套合成器使用的 DRM 设备（多 GPU 排障）。 |
| `GEMPRISM_WLROOTS_LIB_DIR` | 指定嵌套 sway 使用的 wlroots 目录；设为 `off` 强制使用系统库。 |
| `GEMPRISM_HOST_X11` | 设为 `1` 使用旧 X11 宿主窗口（排障/A-B）。 |
| `GEMPRISM_DEV` / `GEMPRISM_VALIDATION` | 开发模式 / Vulkan 校验层。 |

## 6. 损坏与恢复

- TOML 语法错误：原文件备份为 `config.toml.bad-<时间>`，并写入默认配置。
- 单个值无效或未知键：保留可读字段，备份原文件并写回修正后的配置，同时生成警告。
- 恢复备份：退出程序，把 `config.toml.bad-*` 改回 `config.toml`，再用 `--config-check` 检查。

## 7. 自定义 effect（高级）

自定义 effect 是 TOML + 预编译 SPIR-V，放在配置目录的 `effects/` 下。v1.0 GUI 不展示效果管理页，也不在 CLI 帮助中宣传；ABI 和限制见[开发文档](dev/effects.md)，样例见[examples/effects/sharpen-2x](examples/effects/sharpen-2x/README.md)。