# GemPrismUpscaler 命令行使用说明

本文面向 v1.0 用户，说明如何在 KDE Plasma 6 Wayland 下用命令行启动、检查和使用宝石棱镜。

> **支持范围**：v1.0 只明确支持 KDE Plasma 6 Wayland + XWayland。其他桌面环境可能可用，但不在支持范围内。

---

## 1. 先看图形界面

大多数用户不需要命令行：直接运行 `GemPrismUpscaler`，在“超分”页选择超分算法、输出显示器和启动方式即可。

命令行主要用于：

- 查看窗口/显示器列表；
- 脚本化启动；
- 环境检查与故障排查。

命令都在程序目录下执行：

```bash
./GemPrismUpscaler --help
```

---

## 2. 常用命令

| 命令 | 用途 |
|---|---|
| `--list` | 列出可捕获的 X11/XWayland 窗口和显示器 |
| `--window <id>` | 直接对某个 X11/XWayland 窗口启动超分 |
| `--wayland-window` | 在系统弹窗中选择一个原生 Wayland 窗口 |
| `--wayland-monitor` | 在系统弹窗中选择一块原生 Wayland 屏幕 |
| `--nested --monitor <name> -- <命令>` | 在私有嵌套合成器里启动游戏/启动器 |
| `--doctor` | 检查运行环境是否有阻塞问题 |
| `--config-check` | 检查配置文件是否可读、是否有警告 |
| `--diagnose` | 导出诊断 zip（日志、配置、环境、最近错误；自动脱敏路径/用户名） |
| `--status` | 查询是否已有实例在运行 |
| `--version` / `--help` | 版本/帮助 |

### 2.1 查看窗口和显示器

```bash
./GemPrismUpscaler --list
```

输出里会列出窗口 id、标题、进程、尺寸和显示器名称。窗口 id 支持十进制或 `0x` 十六进制。

### 2.2 对已有 X11/XWayland 窗口启动

```bash
./GemPrismUpscaler --window 0x1200001 --monitor HDMI-A-1
```

- `--monitor <name>`：输出到哪块屏幕；名称来自 `--list`。
- `--effect`：`anime4k-s`（默认）、`anime4k-m`、`anime4k-l`、`anime4k-vl`、`bilinear`。
- `--stretch`：拉伸铺满；默认保持比例，剩余区域为黑边。

### 2.3 对原生 Wayland 窗口启动

原生 Wayland 没有通用的窗口枚举，必须通过系统选择器：

```bash
./GemPrismUpscaler --wayland-window --monitor HDMI-A-1
```

命令会弹出系统窗口选择器；选择后开始捕获和超分。`--wayland-monitor` 选择整块屏幕。

### 2.4 嵌套启动（推荐给窗口化游戏）

嵌套启动会把游戏放进一个私有的虚拟桌面，再由宝石棱镜捕获和超分。适合窗口化运行的视觉小说/2D 游戏。

```bash
./GemPrismUpscaler --nested --monitor HDMI-A-1 -- <命令...>
```

`--monitor` 必填：虚拟桌面会使用这块屏幕的分辨率和刷新率。

命令直接按参数启动，**不经过 shell**，所以需要引用的路径要自己加引号：

```bash
./GemPrismUpscaler --nested --monitor HDMI-A-1 -- \
  env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/<id>
```

> **Lutris 用户注意**：v1.0 不支持让 Lutris 自己把环境变量/启动参数注入嵌套会话。请先关闭 Lutris，再用上面的命令手动启动。

> **排障**：嵌套模式默认按窗口抓取。若个别游戏因此启动异常，可临时设置
> `GEMPRISM_TOPLEVEL_CAPTURE=0` 回退到旧输出裁剪方案；这是兼容/排障开关，不是默认用法。

---

## 3. 超分算法

| 名称 | 说明 |
|---|---|
| `anime4k-s` | Anime4K S，默认；速度和效果比较均衡 |
| `anime4k-m` | Anime4K M，更高质量 |
| `anime4k-l` | Anime4K L，更高质量，性能开销更大 |
| `anime4k-vl` | Anime4K VL，实验性最高画质档；帧率不作为保证 |
| `bilinear` | 双线性回退/对比 |

不指定时使用配置文件里的默认值；新配置默认 `anime4k-s`。

---

## 4. 输出显示器

`--monitor <name>` 指定超分画面输出到哪块屏幕；名称来自 `--list` 的显示器列表。

- 嵌套启动时必填，虚拟桌面按该屏幕的物理分辨率和刷新率创建。
- 已有窗口模式下，省略 `--monitor` 时默认跟随游戏窗口所在屏幕。

---

## 5. 诊断与日志

### 5.1 环境检查

```bash
./GemPrismUpscaler --doctor
```

检查 KDE/Wayland/XWayland、sway/swaymsg、装饰 shim、虚拟键盘、Vulkan 设备、配置和日志目录。输出 `[ OK ]` / `[WARN]` / `[FAIL]`；有 `[FAIL]` 时返回 3。

### 5.2 导出诊断包

```bash
./GemPrismUpscaler --diagnose
```

会在配置目录的 `diagnostics/` 下生成一个 zip，包含环境信息、脱敏配置、日志尾部、最近错误和嵌套状态。分享给支持人员前请先打开检查：日志里仍可能包含窗口标题。

### 5.3 配置检查

```bash
./GemPrismUpscaler --config-check
```

只加载配置并打印路径、schema、警告和错误，不启动会话。

### 5.4 日志

默认日志文件：

```text
~/.local/share/GemPrismUpscaler/app.log
```

可用环境变量覆盖：

```bash
GEMPRISM_LOG=/path/to/app.log ./GemPrismUpscaler
```

启动失败、黑屏、没有画面时，先看 `--doctor` 和日志；需要反馈问题时，附上日志文件。

---

## 6. 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 运行或配置失败 |
| 2 | 参数/用法错误 |
| 3 | 环境检查失败（`--doctor`） |
| 4 | 已有实例在运行 |

---

## 7. 常见问题

**`--list` 看不到原生 Wayland 窗口**
原生 Wayland 没有通用窗口枚举，请用 `--wayland-window` 让系统选择器选择。

**提示已有实例在运行**
默认只允许一个实例。先退出已有实例，或在设置里把“重复打开时的行为”改为“激活已有窗口”。

**热键没反应**
v1.0 固定使用 `Alt+Shift+A`，暂不支持修改。若 KDE 系统占用了该快捷键，请到系统设置里释放冲突。

**画面静止时 fps 为 0**
这是正常行为：静止画面不会重复捕获/呈现。

**4K 输出帧率低**
先确认源窗口的分辨率和刷新率；高分辨率源 + 高画质算法会明显增加 GPU 开销。可先改用 `anime4k-s` 或 `bilinear` 排查。
