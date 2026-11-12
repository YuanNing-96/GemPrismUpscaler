# 诊断与日志

## 1. 日志位置

默认：

```text
~/.local/share/GemPrismUpscaler/app.log
```

覆盖顺序：

1. `GEMPRISM_LOG` 环境变量（最高优先级）；
2. 配置 `[logging] path`；
3. 默认路径。

日志级别由 `GEMPRISM_LOG_LEVEL` 或 `[logging] level` 控制：`debug` / `info` / `warn` / `error` / `off`。`debug` 会记录输入/抓帧等高频事件，日志量很大。

## 2. 轮转与失败回退

- 单文件上限 5 MB，保留 3 个历史文件：`app.log.1`、`app.log.2`、`app.log.3`。
- 达到上限时在进程内轮转，不依赖 `logrotate`。
- 日志目录不可写时回退到临时目录，并在控制台/诊断包中记录回退路径；日志失败不能影响渲染。

## 3. Session header

GUI/CLI/诊断启动时写一段 header：

```text
=== GemPrismUpscaler session ===
version=1.0.0 mode=gui args=... session=...
config=/home/user/.config/GemPrismUpscaler/config.toml schema=1
developer=false validation=false
```

用途：把一份日志与一次启动对应起来；排查“哪个版本、什么参数、什么会话类型”。

## 4. 诊断包

```bash
./GemPrismUpscaler --diagnose
```

输出路径（默认配置目录下）：

```text
~/.config/GemPrismUpscaler/diagnostics/gemprism-diagnostics-<时间>.zip
```

GUI：“日志”页 →“导出诊断包”。

包含：

- 环境（桌面、会话、`DISPLAY`/`WAYLAND_DISPLAY`、版本、GPU/Vulkan 摘要）；
- 配置副本（**脱敏**）；
- 日志尾部（最近若干行，含 session header）；
- 最近错误（ErrorBus）；
- 嵌套状态文件、私有 sway stderr 尾部、stale runtime 目录列表；
- 崩溃快照（如果存在）。

### 脱敏

替换家目录、用户名、主机名。**但日志本身可能包含窗口标题、命令、游戏名**，分享前请打开 zip 检查。

## 5. 崩溃快照

未处理异常会写：

```text
~/.local/share/GemPrismUpscaler/crash-<时间>.txt
```

内容包括异常类型/消息/堆栈、版本、会话模式、最近日志尾部。提交问题时同时附 `--diagnose` 包和 `crash-*.txt`。

## 6. 环境检查

```bash
./GemPrismUpscaler --doctor
```

`[ OK ]` / `[WARN]` / `[FAIL]`；有 `[FAIL]` 返回 3。检查：

- `XDG_CURRENT_DESKTOP` 含 KDE、`XDG_SESSION_TYPE=wayland`；
- `DISPLAY` 非空（XWayland）；
- 配置可读、日志目录可写；
- sway / swaymsg、装饰 shim、虚拟键盘 helper 可执行；
- Vulkan 设备可用。

## 7. 常见日志线索

| 日志 | 含义 |
|---|---|
| `nested: launching command '...'` | 命令已启动 |
| `nested window ...` | 发现游戏窗口 |
| `command exited code=...` | 启动命令退出；检查 Lutris 是否已关闭、命令/id 是否正确 |
| `nested toggle requested id=...` | 暂停/继续切换宿主窗口 |
| `nested: stopping command pid=...` | 停止会话并结束命令进程树 |
| `tray created (...)` | 托盘可用 |
| `tray disabled: ...` | 托盘不可用，回退直接退出 |

## 8. 当前限制

- 日志仍是同步追加；日志量小，v1.0 不阻塞。
- 没有日志级别过滤 UI；用配置/环境变量。
- 诊断包不包含完整帧/截图；需要视觉证据时请用户手动截图。
- stale runtime 目录只列出，不在诊断时强制删除。