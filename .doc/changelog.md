# 更新记录

## 2026-11-02 · M0a 验收整改（0.1.x）

对应 `.agent/M0a验收记录.md` 的 5 条实测问题。改动集中在捕获/呈现/输入/窗口生命周期，不涉及 M0b 的 Anime4K/FSR。用户已在腐姬 / 秽翼的尤斯蒂娅上复测通过。

### 修复的问题

| # | 问题 | 根因 | 处理 |
|---|---|---|---|
| 1 | 秽翼启动后，覆盖层上移动鼠标没有悬停反应，必须先点一下 | 启动会话时焦点留在配置界面；overlay 覆盖后源窗口又认为指针已离开 | 启动/点击/热键恢复时通过 EWMH `_NET_ACTIVE_WINDOW` + `XSetInputFocus` 把焦点交还游戏；补发 `EnterNotify` / `LeaveNotify`；显示时同步当前指针位置 |
| 2 | 秽翼按 Esc 最小化后，全屏画面卡死 | 引擎只在有 XDamage 时检查源窗口，最小化后没有新 Damage | 每 200ms 检查 `map_state` / `_NET_WM_STATE_HIDDEN`；不可见时收起 overlay，恢复时强制重绘；窗口销毁则停止引擎 |
| 3 | 腐姬退出确认框关闭后，恢复的全屏画面黑屏，点到有反应的位置才出现 | overlay unmap 后 Vulkan/X11 surface 内容未定义；弹窗关闭只恢复窗口，没有重新呈现 | 任何“隐藏 → 显示”都强制捕获+呈现一帧；弹窗恢复和热键恢复同样处理 |
| 4 | 腐姬退出游戏后，全屏界面黑屏无反应 | 源窗口销毁后，没有 Damage 就永远发现不了 | 同一套 200ms 生命周期轮询；`XGetWindowAttributes` 失败即停止引擎并销毁 overlay |
| 5 | 热键隐藏 overlay 后关闭游戏，UI 仍显示“正在超分” | overlay 隐藏时循环直接 `continue`，完全跳过源窗口检查 | 隐藏时仍轮询；引擎把退出原因写入 `ExitReason`；UI 在 500ms 内复位按钮并显示原因 |

### 新增 / 变更的功能

- **焦点跟随**：游戏失去焦点时自动收起覆盖层，回到游戏时恢复。配置界面新增“跟随焦点（切到其他程序时收起）”开关，默认开启；CLI 新增 `--no-follow-focus`。
- **窗口生命周期**：最小化/切工作区自动收起；恢复后重新捕获；游戏关闭时引擎自行退出，UI 复位。
- **弹窗判定收紧**：只把同 PID、当前 viewable、非 `_NET_WM_STATE_HIDDEN`、尺寸 ≥64×64 的顶层窗口算作弹窗，避免 Wine 的隐藏 helper/IME 窗口把 overlay 永久按下去。
- **输入边界**：拖拽到内容矩形（黑边）之外松手时补发 `ButtonRelease`，避免按键卡住；点击时若游戏没有焦点，先激活游戏。
- **UI 窗口列表**：过滤本进程自己的配置窗口，避免默认选中自己。

### 技术方案

- 新增 `OverlayVisibility` 纯状态机，把“用户隐藏 / 源窗口不可见 / 弹窗 / 失焦”四类原因分开，便于单元测试和日志定位。
- 新增 `X11WindowProbe`：`XGetWindowAttributes` + `_NET_WM_STATE_HIDDEN` 检查窗口是否可见；`XGetInputFocus` + `XQueryTree` 判断焦点是否在源窗口（或其子窗口）；EWMH `_NET_ACTIVE_WINDOW` 激活窗口。
- 覆盖层可见性统一由 `ApplyVisibility()` 处理；每次“隐藏 → 显示”都置 `_forceRedraw`，强制走一次捕获+呈现，解决 unmap 后 surface 内容未定义的问题。
- 输入转发在原有 `XSendEvent` 基础上补合成 `EnterNotify` / `LeaveNotify`，并在显示时用 `XQueryPointer` 同步一次指针位置。
- XDamage 的帧级跳过保持不变：静止画面不重复捕获/呈现。

### 验证

- `dotnet build GemPrismUpscaler.slnx -c Release`：0 warning / 0 error。
- `dotnet test GemPrismUpscaler.slnx -c Release`：15/15 通过（新增 8 个可见性状态机测试）。
- 合成 1280×720：点击 overlay (1920,1080) → 源窗口 (640,360)；静态画面 + 弹窗恢复非黑屏；最小化收起/恢复；关闭游戏退出并销毁 overlay；失焦收起/回焦恢复。
- 帧率：桌面 60Hz 时 58–60 fps。当前测试机处于 HDR / 3840×2160@144Hz 时合成测试约 47–50 fps，基线提交同环境约 49–51 fps，差异来自合成器/显示模式，不是本次改动引入。

### 注意事项

- **轮询 `GetCursorPos` / DirectInput 的引擎**：合成事件不会移动真实指针；这类引擎的悬停位置仍可能不跟随。本次秽翼已通过焦点/消息路径解决，但更底层的输入方案（uinput 虚拟指针、合成器集成）仍不在 M0 范围。
- **焦点跟随依赖 EWMH**：KWin 实测支持；不支持的 WM/合成器下只调用 `XSetInputFocus`，可能被焦点窃取防护忽略。
- **最小化恢复的第一帧**：可能先显示黑/旧帧，源窗口重绘后由 XDamage 更新；真实游戏会在 Expose 后重绘。
- **HDR**：开启 HDR 时仍可运行（SDR 直通），但 M0 不做色彩管理；linear/HDR 直通在 M2。
- **弹窗识别**：仍依赖 `_NET_WM_PID`；缺失 PID 的 Wine 版本需要后续补 XRes 兜底。
