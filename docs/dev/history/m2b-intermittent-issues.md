> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M2b 偶现问题记录

记录时间：2026-11-09 13:35（Asia/Shanghai）
范围：当前 M2b 工作树（未提交的 fullscreen 简化 + 既有 X11 host 修复）。
原则：以 Lutris+Proton 直连为基准；直连也出现的问题不修，直连正常而 nested 异常的才修。

## 1. “确认结束”弹窗出现约 1 秒后消失，游戏卡死

状态：**已复现，暂不修复（用户要求先记录）**。

### 现象

- 示例视觉小说 A在主菜单点“结束游戏”后，nested 创建 `确认结束` 弹窗：
  - `id=10` / `id=12`，尺寸 `197×82`
  - 被移动到 spare output `HEADLESS-2`
  - 创建独立 KDE host（197×82）
- host 出现约 1 秒后消失；游戏侧弹窗也从 sway 树消失，游戏逻辑随后卡住。
- 不是 host 主动关闭：日志没有 `host window closed`；主窗口之后仍以约 30–35fps 被抓取。

### 证据

`~/.local/share/GemPrismUpscaler/app.log`：

```text
13:20:40.880 nested window id=10 '确认结束' 197x82
              output=HEADLESS-2 origin=3840x2160+3840+0
13:20:40.885 nested outputs: created HEADLESS-3 spare
13:20:42.003 nested outputs: unplugged HEADLESS-3
13:20:42.003 nested window removed; windows=1
```

`MissingWindowGraceMs = 1000`，说明游戏侧窗口在约 13:20:41.0 从 sway 树消失。

旧会话也出现过同一弹窗：

```text
03:09:38.911 id=83 '确认结束' 197x82
03:09:41.875 removed

03:09:51.197 id=86 '确认结束'
03:09:53.600 removed

03:10:03.555 id=89 '确认结束'
03:10:05.869 removed
```

### 当前最可疑点

dialog 被当成独立窗口处理：

1. `FloatWindow`
2. `MoveToOutput` 到另一个 4K headless output
3. 创建独立 host
4. `Host.Show()`（内部会 `Activate`）
5. `Host.Focus()`（再次激活）
6. `_tracker.FocusWindow(dialog)`（把 sway 焦点切到弹窗）

Wine 模态对话框可能把“焦点离开游戏主窗口”或“被移动到另一个 output 的 ConfigureNotify”当成取消/关闭条件，随后游戏等待一个不会返回的模态结果。

### 后续验证方向

- 对 dialog 不自动 `Focus/Activate`，只 map host；
- 或 dialog 不移动 output，留在父窗口 output 并按父 output 抓取；
- 或把 dialog 的 missing grace 从 1s 提高到 3s，排除短暂 unmap；
- 先对比直连 Lutris+Proton 同一“确认结束”弹窗是否也会消失/卡住。

## 2. 游戏画面卡住，但渲染线程仍在跑

状态：**现场诊断，原因指向游戏/Wine 音频线程；未修改产品代码**。

### 现场

- 2026-11-09 13:27:32 启动的示例视觉小说 A（PID 109471）停在主菜单。
- nested host 为 800×600；游戏窗口 800×600；KDE host 截图 2 秒内完全一致。
- `stats` 仍约 25–32fps，capture ≈0.5ms，upload ≈0.3ms，没有 capture/present 异常。
- 因此不是 GemPrism 渲染线程卡死；是游戏进程不再更新画面/处理输入。

### 关键线程

```text
PID 109471, TID 109882, R, ~97% CPU
gdb:
  read()
  virtual_locked_read()
  NtReadFile()
  __wine_syscall_dispatcher()
```

`strace` 显示它在一个固定偏移上反复 seek + read：

```text
_llseek(49, 1141616, [1141616], SEEK_SET) = 0
read(49, "OggS...", 64000) = 64000
_llseek(49, 1141616, [1141616], SEEK_SET) = 0
read(49, "OggS...", 64000) = 64000
...
```

fd 49：

```text
~/Games/<game>/wav.xfl
普通文件 17,442,340 bytes
```

主线程：`futex_wait`。
音频输出正常存在：

```text
pactl sink-input #2848
application.name = "codeX RScript"
application.process.id = "109471"
corked = no
```

### 结论倾向

- 游戏自己的音频/BGM 线程在 `wav.xfl` 的 Ogg 数据上反复 seek/read，占满一个核；
- 主线程等待，游戏不处理输入；
- 显示链路正常，nested 仍在抓最后一帧，所以看起来“画面卡住”；
- 这不是本次 fullscreen 简化引入的（当前窗口一直是 800×600，日志没有 `promoted`）。

### 后续验证方向

- 用 Lutris+Proton **直连**同一游戏、同一菜单，观察是否也有 `wav.xfl` 自旋；直连也有则不修。
- 打开 `WINEDEBUG=+mmdevapi,+dsound,+timestamp,+tid` 跑一次，看音频线程是否报错。
- 对比不同音频后端（winepulse / ALSA / 禁用音频）是否还复现。
- 若直连正常、仅 nested 异常，再查 nested 输入/焦点是否触发了游戏音频线程的状态机。

## 3. 性能（旁注）

- 当前 800×600：约 25–35fps。
- 4K 全屏：约 30fps，capture ≈2.2ms，upload ≈2.0ms，CPU ≈29%。
- 这是 nested 4K 抓帧 + 上传 + host 呈现的固定开销，不是本次 fullscreen 修改导致；按用户要求暂不作为阻塞项。

## 4. 当前进程状态

- 旧 `--nested-session`（PID 94629）仍在运行但 idle。
- 新 `--nested`（PID 109038）运行示例视觉小说 A（PID 109471），游戏停在主菜单/卡住。
- 两个会话都写同一个 `app.log`，日志会交错；分析时应先区分会话。
## 5. 示例视觉小说 A Wine Wayland 全屏异常（2026-11-09 13:45–13:54）

状态：**已复现，未修复；本轮尝试的 app 侧裁剪回退已撤销**。

### 现象

- Wayland 模式启动：窗口 `806×632`，host `806×632`，正常。
- 游戏内切全屏后：
  - 日志先出现 `rect=3840×2160`、`mode=original client=3840×2160 fullscreen=True`
  - 随后窗口变回 `800×600+0+0`，host 仍为 `3840×2160`
  - 画面表现为 800×600 内容在左上角/黑屏，或带蓝色 CSD 标题栏的黑屏
- 用 `swaymsg [con_id=6] fullscreen enable` 强制复现：
  - `rect=3840×2160`，但位置仍是 `+1517+764`
  - `fullscreen_mode=0`
  - host 变成 `2323×1396`，截图是蓝色 CSD 标题栏 + 全黑内容
- `swaymsg fullscreen disable` 后 rect 一度变成 `7680×4320`，再变 4K，说明 native Wayland surface 尺寸/位置状态不稳定。

### 结论倾向

- 不是单纯“抓错区域”：
  - 尝试按逻辑尺寸 806×632 在 output 原点裁剪、host 4K 全屏；
  - 结果 host 4K 但内容仍黑；
  - 说明 Wine Wayland 在 sway 强制 fullscreen/resize 后 surface 没有正常呈现。
- 这更像 **Wine Wayland + sway native fullscreen/resize** 的 surface/CSD/缩放问题，单靠 GemPrism 的 crop 修不好。
- X11/XWayland 的 800×600 左上角问题则是另一条路径（KWin 会缩放 XWayland surface，sway 不会）。

### 后续可选方向

1. 不在 nested 里强制 fullscreen；保持 806×632 窗口 + host 4K fit（视觉全屏，但游戏仍认为窗口化）。
2. Wine Wayland 全屏走 host KWin/portal，不用 nested。
3. 补 Wine Wayland 的 surface resize/CSD/缩放补丁（成本最高）。

## 6. WINEDEBUG 没有生效

用户的 Lutris 配置里加入了 `WINEDEBUG`，但实测游戏进程环境仍是：

```text
WINEDEBUG=-all
```

原因是 Proton 在 `proton` 脚本里会覆盖/默认 `-all`。要抓 Wine 日志应使用：

```text
PROTON_LOG=1
PROTON_LOG_DIR=/tmp
```

`PROTON_LOG=1` 会让 Proton 生成 `steam-*.log`，并把 WINEDEBUG 设为详细值；单纯在 Lutris 环境里写 WINEDEBUG 会被 Proton 覆盖。
