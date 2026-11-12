# 常见问题

反馈问题前，先运行：

```bash
./GemPrismUpscaler --doctor
./GemPrismUpscaler --diagnose
```

`--diagnose` 会在配置目录的 `diagnostics/` 下生成 zip，包含环境、脱敏配置、日志尾部、最近错误和嵌套状态。日志里仍可能包含窗口标题，分享前请打开检查。

## 1. 打不开 / 双击没反应

从终端启动看错误：

```bash
./GemPrismUpscaler
```

检查：

- 文件是否有执行权限：`chmod +x GemPrismUpscaler`。
- 是否为 KDE Plasma 6 Wayland 会话：`echo $XDG_SESSION_TYPE` 应为 `wayland`。
- XWayland 是否可用：`echo $DISPLAY` 应非空。
- 运行 `./GemPrismUpscaler --doctor`，先解决所有 `[FAIL]`。

## 2. 嵌套启动后没有画面 / 立刻结束

### 2.1 先看日志

“日志”页或 `~/.local/share/GemPrismUpscaler/app.log`。重点看：

- `nested: launching command ...` 是否出现；
- `nested window ...` 是否出现；
- 是否有 `command exited code=...`。

### 2.2 Lutris 仍在运行

Lutris 默认单实例。如果 Lutris 还开着（包括托盘），游戏会被启动到已有 Lutris 实例里，而不是嵌套会话。**完全退出 Lutris** 后再开始。

### 2.3 游戏 id 或命令错误

先用终端直接验证命令：

```bash
env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/<id>
```

`lutris --list-games` 可以查看 id。命令按参数启动、不经过 shell，路径含空格要加引号。

### 2.4 游戏切了独占全屏

嵌套模式按窗口抓取。请把游戏设为**窗口化**或**无边框窗口**；独占全屏/游戏内全屏可能黑屏或点击错位。

### 2.5 仍然启动异常

临时回退到旧的输出裁剪方案：

```bash
GEMPRISM_TOPLEVEL_CAPTURE=0 ./GemPrismUpscaler
```

这是排障开关，不是日常用法。回退后窗口移动/缩放的兼容性会变差。

## 3. 有画面但点击位置不对

- 确认“输出显示器”和游戏所在屏幕一致。
- 嵌套模式下，游戏运行中改分辨率/移动窗口后如果错位，停止再重新开始会话。
- 游戏内全屏的已知偏移问题见[已知问题](known-issues.md)。
- 黑边区域不转发点击是预期行为（fit 模式）；需要整屏可点可改用 `stretch`。

## 4. 画面黑屏 / 卡住

- 先按 `Alt+Shift+A` 或“暂停/继续”切一次，确认是否只是隐藏状态。
- 游戏窗口最小化后，X11 会收起覆盖层；从任务栏恢复游戏。
- 查看日志是否有 Vulkan/swapchain 错误。
- 更新显卡驱动；v1.0 只在 NVIDIA 专有驱动上验证。
- 多 GPU 机器可尝试指定 DRM 设备：

  ```bash
  GEMPRISM_WLR_RENDER_DRM_DEVICE=/dev/dri/renderD128 ./GemPrismUpscaler
  ```

## 5. 热键没反应

v1.0 固定使用 `Alt+Shift+A`，不能修改。若 KDE 或其他程序占用了该组合，请到“系统设置 → 快捷键”释放冲突。原生 Wayland 窗口的热键依赖 KDE 的 GlobalShortcuts 支持。

## 6. 托盘不显示

- 确认“设置 → 托盘”已启用并保存。
- KDE 需要 StatusNotifierItem/AppIndicator 支持；托盘不可用时程序仍可正常使用。
- 托盘不可用时，“关闭窗口行为”会自动回退为直接退出。

## 7. “已有实例在运行”

默认只允许一个实例。先退出已有窗口；或在“设置 → 重复打开时的行为”改为“激活已有窗口”。第二个 CLI 实例始终返回退出码 4。

## 8. 崩溃

未处理异常会在日志目录写 `crash-*.txt`。请提供：

- `--diagnose` 生成的 zip；
- `crash-*.txt`；
- 复现步骤（启动方式、命令、游戏窗口模式、显示器缩放）。

## 9. 帧率低

- 换 `Anime4K S` 或 `双线性` 对比。
- VL 开销最大，不建议作为日常档。
- 4K 源再 2× 会非常重；v1.0 只承诺常见 720p/768p/1080p 源。
- 查看日志里的 `stats` 行，确认瓶颈在 capture、effect 还是 present。
- 静止画面 fps 为 0 是正常行为：没有新帧就不会重复呈现。

## 10. 彻底清理

正常“停止超分”会结束会话并清理私有 sway/XWayland。若程序异常退出：

```bash
pgrep -af 'GemPrismUpscaler|sway'
```

确认没有残留后重新启动。不要直接 `killall sway`，以免影响系统里其他 sway 实例。诊断包会列出 stale runtime 目录。

## 11. 配置文件损坏

启动时会自动备份为 `config.toml.bad-<时间>` 并写回可用配置。也可以手动检查：

```bash
./GemPrismUpscaler --config-check
```

需要恢复旧配置时，退出程序，把备份改名回 `config.toml`，再运行 `--config-check`。