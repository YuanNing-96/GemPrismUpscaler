# GemPrismUpscaler CLI 使用说明

- 适用版本：`0.5.0`（M2b 主路径）；`--backend/--capture/--input/--portal-window/--nested` 已实现，具体以实际 `--help` 为准。
- 图形界面与 CLI 共用同一份配置：`~/.config/GemPrismUpscaler/config.toml`（`GEMPRISM_CONFIG` 可覆盖）。
- 本文是发布文档；M2 计划见 [m2-plan.md](m2-plan.md)。

---

## 1. 启动方式

```bash
# 配置界面
GemPrismUpscaler

# 列出可捕获窗口与显示器
GemPrismUpscaler --list

# 直接对某个窗口启动超分
GemPrismUpscaler --window 0x1200001

# 查询运行中的实例
GemPrismUpscaler --status

# 环境检查 / 配置检查
GemPrismUpscaler --doctor
GemPrismUpscaler --config-check

# 版本/帮助
GemPrismUpscaler --version
GemPrismUpscaler --help
```

安装后二进制通常在项目输出目录：

```bash
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler
```

GUI 与 CLI 共用单实例约束：默认第二个 GUI 只提示“已在运行”；第二个 CLI 返回非 0。设置里可把第二实例行为改为“激活已有窗口”。

---

## 2. 全局参数

| 参数 | 说明 |
|---|---|
| `--dev` | 强制开发模式：详细日志、非功能级错误提示、Vulkan 校验层（按配置） |
| `--doctor` | 检查 KDE/Wayland/XWayland、sway/swaymsg、shim、vkeyboard、Vulkan、配置/日志；有阻塞项返回 3 |
| `--config-check` | 只加载配置并打印路径/schema/警告/错误，不启动会话 |
| `--help` | 打印用法 |
| `--version` | 打印版本 |

环境变量：

| 变量 | 说明 |
|---|---|
| `GEMPRISM_CONFIG` | 覆盖配置文件路径 |
| `GEMPRISM_LOG` | 覆盖日志路径 |
| `GEMPRISM_DEV=1` | 等价 `--dev` |
| `GEMPRISM_VALIDATION=1` | 强制开启 Vulkan 校验层 |
| `GEMPRISM_STATS=/path/stats.csv` | 输出逐帧指标 CSV |
| `GEMPRISM_TOPLEVEL_CAPTURE=0` | 关闭按 toplevel 抓取，回退旧输出裁剪路径（默认开启） |

---

## 3. `--window` 选项

```bash
GemPrismUpscaler --window <id> [options]
```

`<id>` 来自 `--list`，支持十进制或 `0x` 十六进制。

| 参数 | 默认 | 说明 |
|---|---|---|
| `--monitor <name>` | `auto` | 输出显示器；`auto` 跟随游戏窗口；名称来自 `--list` 的 `monitors:` |
| `--effect <id>` | 配置/`anime4k-s` | `anime4k-s`、`anime4k-m`、`anime4k-l`、`anime4k-vl`、`bilinear`、自定义 effect id；`anime4k` 是 `anime4k-s` 别名 |
| `--stretch` | fit | 拉伸铺满，不保持源宽高比 |
| `--no-follow-focus` | 跟随 | 切到其他程序时保持 overlay（默认失焦收起） |
| `--no-dirty-rect` | 开启 | 关闭 64px 脏矩形，整帧路径（A/B/回退） |
| `--output-size <WxH>` | 显示器尺寸 | 调试/基准输出尺寸覆盖，例如 `3840x2160` |

示例：

```bash
# 默认 Anime4K S + Lanczos，fit
GemPrismUpscaler --window 0x1200001

# 4K 输出 + Anime4K L
GemPrismUpscaler --window 0x1200001 --effect anime4k-l --monitor HDMI-A-1

# 4:3 视觉小说拉伸铺满
GemPrismUpscaler --window 0x1200001 --stretch

# A/B：关闭脏矩形
GemPrismUpscaler --window 0x1200001 --no-dirty-rect

# 固定输出尺寸（不要求显示器存在）
GemPrismUpscaler --window 0x1200001 --output-size 3840x2160
```

---

## 4. 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功/正常结束 |
| 1 | 致命错误；`--status` 未运行 |
| 2 | 参数错误/用法错误 |
| 3 | 指定窗口未找到 |
| 4 | 已有实例在运行，第二个 CLI 被拒绝 |
| 5 | 指定显示器不存在 |

---

## 5. 配置相关

配置文件：`~/.config/GemPrismUpscaler/config.toml`；损坏时自动备份为 `config.toml.bad-<时间>` 并回退默认。

常用段：

```toml
[general]
second_instance = "notify"   # notify | activate
close_action = "exit"        # exit | tray
follow_focus = true
remember_last = true
auto_refresh_windows = false
refresh_interval_ms = 1000

[hotkey]
toggle_overlay = "Alt+Shift+A"

[logging]
path = ""
level = "info"

[display]
monitor = "auto"

[effect]
default = "anime4k-s"
scaling = "fit"              # fit | stretch
dirty_rect = true

[tray]
enabled = true

[diagnostics]
developer_mode = false

[[profile]]
name = "galgame"
class = ["game.exe"]
exe = ["*/game.exe"]
effect = "anime4k-l"
scaling = "fit"
monitor = "auto"
```

优先级：CLI 显式参数 > Profile 匹配 > `[last]`（`remember_last`）> 设置默认 > 内置默认。

---

## 6. M2a/M2b 参数（0.5.0）

> 以下参数已实现；具体以 `--help` 为准。

| 参数 | 取值 | 说明 |
|---|---|---|
| `--backend <mode>` | `auto`/`x11`/`wayland` | 选择呈现/捕获平台；`auto` 按会话和源类型选择 |
| `--capture <mode>` | `auto`/`x11`/`portal` | `x11` = XShm；`portal` = ScreenCast + PipeWire（原生 Wayland；`--window` 路径仍回退 X11，原生窗口用 `--portal-window`） |
| `--input <mode>` | `auto`/`passthrough`/`forward` | `passthrough` = 空 input region；`forward` = 接收后 `XSendEvent` |
| `--portal-window` | 无值 | 原生 Wayland 窗口：**每次启动都打开系统选择器** |
| `--portal-monitor` | 无值 | 原生 Wayland 显示器捕获 |
| `--portal-reuse-token` | 无值 | 复用 `portal.token` 中的上次窗口（默认不复用，避免跳过选择器） |

配置对应：

```toml
[capture]
backend = "auto"   # auto | x11 | wayland
mode = "auto"      # auto | x11 | portal

[input]
mode = "auto"      # auto | passthrough | forward
```

`auto` 规则（M2a）：

- X11 会话 → X11 捕获 + X11 overlay。
- Wayland 会话 + XWayland 窗口 → X11 捕获 + Wayland overlay + `forward` 指针转发。
- Wayland 会话 + 原生 Wayland 全屏窗口 → portal 捕获 + 空 input region 穿透。
- 原生 Wayland 窗口化：M2a 只显示，输入映射由 M2b 嵌套解决；UI 会提示。
- 原生 Wayland **跟随焦点**（切到其他程序自动收起）不在 M2a：portal 不提供源窗口身份/焦点；M2b 嵌套合成器解决。M2a 用 `Alt+Shift+A` 或托盘手动收起。
- 原生 Wayland 热键：首次会话会弹 `GlobalShortcuts` 授权；KDE 若把快捷键存成 `none`，需在“系统设置 → 快捷键 → GemPrismUpscaler”里把“显示/隐藏…”设为 `Alt+Shift+A`。

```bash
# M2a 原生 Wayland：默认每次打开系统选择器
GemPrismUpscaler --portal-window --monitor HDMI-A-1
GemPrismUpscaler --portal-monitor --monitor HDMI-A-1

# 固定复用上次选择（跳过选择器；仅在明确知道目标窗口时使用）
GemPrismUpscaler --portal-window --monitor HDMI-A-1 --portal-reuse-token
```

---

## 7. M2b 嵌套命令（已实现，单输出路线）

> `--monitor` 必填；嵌套桌面按该显示器的物理分辨率/刷新率快照。`--nested-size` 已废弃，
> 传入会被忽略。按 toplevel 抓取是默认主路径；`GEMPRISM_TOPLEVEL_CAPTURE=0` 可回退旧输出裁剪。

```bash
# 启动一个游戏/启动器到私有嵌套合成器
GemPrismUpscaler --nested --monitor HDMI-A-1 -- <command...>

# 只启动嵌套会话，写出 WAYLAND_DISPLAY/DISPLAY/token 供 Lutris/Steam 使用
GemPrismUpscaler --nested-session --monitor HDMI-A-1

# 探针：只起嵌套会话并打印输出/窗口信息
GemPrismUpscaler --nested-probe --monitor HDMI-A-1

# wrapper：把最终游戏进程送进已有嵌套会话
gemprism-nested-run -- <command...>
```

Lutris 推荐用 per-game 环境变量/wrapper，把 `WAYLAND_DISPLAY`/`DISPLAY`/`GEMPRISM_NESTED_TOKEN` 传给最终游戏进程；Steam/Heroic 用启动选项 wrapper 做 best-effort。`--sway <path>` 可覆盖内置 sway。

---

## 8. 常见问题

- **`--list` 看不到原生 Wayland 窗口**：Wayland 没有通用窗口枚举；M2a 用 portal 系统选择器（`--portal-window`）。`--list` 只列 X11/XWayland 窗口。
- **热键无反应**：XWayland 焦点用 `XGrabKey`；原生 Wayland 用 portal GlobalShortcuts。首次会弹授权；若 KDE 日志显示 `trigger='none'`，到“系统设置 → 快捷键 → GemPrismUpscaler”手动分配 `Alt+Shift+A`。
- **画面静止时 fps 为 0**：预期行为；脏矩形/静止画面不重复 capture/present。
- **4K 输出 fps 低**：先确认源窗口是否 GPU 更新（XShm 捕获可能慢）、是否开启 HDR/高刷、是否使用 VL 等重 effect；用 `--output-size` 和 `GEMPRISM_STATS` 排查。
- **第二实例**：默认只提示错误；设置可改为激活已有 GUI；第二个 CLI 仍返回 4。

---

## 9. 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功（`--help`/`--version`/`--doctor` 无阻塞/`--config-check` 无错误/会话正常退出） |
| 1 | 运行或配置失败 |
| 2 | 参数/用法错误 |
| 3 | 依赖或环境检查失败（`--doctor`） |
| 4 | 已有实例在运行（第二实例） |
