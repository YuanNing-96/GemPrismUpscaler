# 指针跳转（游戏驱动的光标移动）

游戏自己把鼠标移到某个按钮上（视觉小说弹出“确认退出”时高亮默认按钮）时，用户看到的光标应该跟着跳。KDE 直连（游戏直接是 KWin 的 Wayland 客户端）本来就会跳；嵌套模式需要把嵌套合成器内部的指针位置转成对宿主窗口的一次 `wp_pointer_warp_v1` 请求。

**状态：v1.0 已实现并通过实机验证。** 宿主窗口改为原生 Wayland `xdg_toplevel`，由它自己持有 `wl_surface`/`wl_pointer`，因此可以直接调用 warp 协议。X11 宿主窗口仍保留为回退（`GEMPRISM_HOST_X11=1`），在 XWayland 下它只能移动影子指针，看不到效果。

## 1. 现象与两层指针

嵌套模式下有两层指针：

- 嵌套合成器（私有 sway）内部的指针，被游戏用
  `zwp_locked_pointer_v1.set_cursor_position_hint`（Wine/Proton 的旧路径）或
  `wp_pointer_warp_v1`（较新的 Proton）挪走；
- 用户看到的 KWin 指针，位于宿主窗口上。

两者没有天然联系，必须由应用显式中继。

## 2. 协议：wp_pointer_warp_v1

`/usr/share/wayland-protocols/staging/pointer-warp/pointer-warp-v1.xml`，KWin 6.8 已实现（registry 里有 `wp_pointer_warp_v1` v1）。

```
warp_pointer(wl_surface surface, wl_pointer pointer,
             fixed x, fixed y, uint serial)
```

协议要求：

- `surface` 必须**持有指针焦点**（包括隐式 grab）；
- `serial` 必须是有效的 **enter serial**；
- `(x, y)` 是 surface-local 坐标，必须在 surface 内部。

只有拥有该 `wl_surface` 的 Wayland 客户端能调用，所以宿主窗口必须是原生 Wayland 窗口。

## 3. 实现

### 3.1 类

- `Platform/Linux/Nested/IHostWindow.cs`：宿主窗口抽象（尺寸、事件、全屏、warp、创建 blitter）。X11 与 Wayland 两个后端。
- `Platform/Linux/Nested/WaylandHostWindow.cs`：`xdg_toplevel` + `zxdg_decoration_manager_v1`（服务端装饰）+ `wp_viewporter`/`wp_fractional_scale_manager_v1`（分数缩放）+ `wp_pointer_warp_v1`。
- `Platform/Linux/Nested/NestedHostWindow.cs`：X11/EWMH 后端（回退）。
- `Platform/Linux/Nested/HostSurfaceGeometry.cs`：逻辑像素 ↔ 缓冲像素换算（有单测）。
- `Platform/Linux/Nested/NestedUpscaleSession.cs`：`CreateHostWindow` 选后端，`state.Host.CreateBlitter` 统一创建交换链。

### 3.2 数据流

```
Wine: zwp_locked_pointer_v1.set_cursor_position_hint(x, y)
  → sway（打补丁）：记录到 seat 的 cursor_hint_seq/x/y
  → 应用：swaymsg -t get_seats 轮询（16 ms，cursor-sync 线程）
  → NestedUpscaleSession.MirrorClientCursorWarp
       游戏坐标 → 宿主角色的 dest 矩形 → 宿主缓冲像素 (hostX, hostY)
  → IHostWindow.WarpPointer(hostX, hostY)
       Wayland：排队，由渲染线程下一次 Pump 发出 warp_pointer
       X11：XWarpPointer（只动影子指针，返回 false）
  → KWin：移动可见光标，并回发 wl_pointer.motion
```

补丁与构建脚本见 §5；位置来源短期内仍是这条 sway IPC，因为 sway 1.12 没有实现
`wp_pointer_warp_v1`（Wine 用约束提示，等效信息已经拿到）。将来 sway 支持新协议后，
应用仍可从 sway 读取位置，改造点只在尾巴。

### 3.3 坐标模型（关键）

Wayland 宿主窗口有两个坐标空间，混淆会按 output scale 偏移所有点击：

| 空间 | 用途 | 例子（HDMI-A-1，scale 1.5） |
|---|---|---|
| 逻辑 / surface-local | xdg configure、`wl_pointer` 事件、`warp_pointer` | 全屏 2560×1440 |
| 缓冲像素 | Vulkan swapchain、会话里的 dest 矩形、本接口的 Width/Height | 全屏 3840×2160 |

- 缓冲区大小 = `round(logical × scale)`，窗口化 1286×752 ↔ 逻辑 857×501。
- `wp_viewport.set_destination(logical)` 把缓冲映射回逻辑尺寸，表面上仍是逻辑坐标，
  所以 **一个 3840×2160 的缓冲在 1.5 屏上正好铺满 2560×1440 逻辑空间 = 物理 1:1**，
  不浪费分辨率（对比：`set_buffer_scale(2)` 会渲染 5120×2880 再被合成器缩到 1.5×）。
- 指针事件（逻辑）→ `HostSurfaceGeometry.LocalToPixel` 乘 scale 后进入会话；
  warp（缓冲像素）→ `PixelToLocal` 除 scale 并 clamp 到 surface 内。
- 显示器没有 `wp_fractional_scale_manager_v1` 时退回 `wl_output.scale`（整数）。

### 3.4 serial 与线程

- enter serial 在 `wl_pointer.enter` 时记录，warp 时一直复用它；serial 过期或指针不在
  窗口上时合成器会直接忽略，**不做本地拦截**——KWin 有时在 warp 之后立刻发一次
  `leave`（光标其实还在窗口上），按 Enter/Leave 状态拦截会漏掉后续 warp。
- `WarpPointer` 可以从 cursor-sync 线程调用：只写一个带锁的待办，真正的
  `warp_pointer` 由渲染线程的 `Pump` 发出（wl_display 不是线程安全的）。
- 排队期间窗口尺寸可能变化，实际发送时再做一次 clamp。

### 3.5 开关

- 默认：`WAYLAND_DISPLAY` 存在就用原生 Wayland 宿主窗口。
- `GEMPRISM_HOST_X11=1`：强制旧的 X11 宿主窗口（A/B 对比、没有 Wayland 的会话）。
- 原生窗口创建失败时自动回退 X11，并记录一条 `nested wayland host unavailable ...`。

## 4. 验证

- **独立探针**（未提交，`.agent/host-probe`）：创建宿主窗口、Vulkan 循环呈现、
  按 `wp_pointer_warp_v1` 依次把光标送到中心/左上/右下。观测到 warp 请求的
  `buffer=450,300` 下一次 motion 回报正好是 `450,300`，说明物理像素、逻辑像素、
  返回事件三者闭环一致；截图测得窗口内容在屏幕上正好是 1286×752 物理像素。
- **冒烟**：zenity 窗口 → 宿主窗口 344×270；Alt+Shift+A 进入超分 → configure
  `logical=2560x1440 buffer=3840x2160 fullscreen=True`，交换链按 3840×2160 重建；
  再按一次回到 `logical=229x180 buffer=344x270`，尺寸与位置恢复。
- **实机游戏**（秽翼的尤斯蒂娅，Proton + `PROTON_ENABLE_WAYLAND=1`）：
  悬停、点击、对话框光标跳转、超分进出全部正常。日志里一次退出确认框产生
  25 次 `wayland host warp buffer=… logical=…`，与 sway 的 hint 位置一一对应。
- `GEMPRISM_HOST_X11=1` 冒烟：旧 X11 宿主窗口仍能正常创建并呈现首帧。

## 5. 当前的位置来源补丁

- 补丁：`tools/patches/sway-1.12-cursor-hint-ipc.patch`
- 构建：`tools/build-nested-sway.sh`（sway 1.12 + 补丁 → `runtime/sway/sway`）
- 接口：`swaymsg -t get_seats` 增加 `cursor_hint_seq` / `cursor_hint_x` / `cursor_hint_y`
- 未打补丁时（系统 sway）没有这些字段：应用不再有任何“自动重锚”兜底，悬停需要
  “晃一下”，但点击前仍会补一次绝对定位，所以点击位置是准的。
- 早期为绕开“光标不跳”加过的每秒强制绝对定位已经移除（见 §7）：有了真实的
  `wp_pointer_warp_v1` 之后，它只会和 sway 回报的 warp 参考点互相拉扯。

## 6. 已知限制

- **窗口位置**：xdg-shell 没有“把窗口放到某个坐标”的请求，窗口化位置由 KWin 决定；
  只有全屏可以指定输出（`set_fullscreen(wl_output)`，按 monitor 名匹配）。
- **最小化不可逆**：`xdg_toplevel.set_minimized()` 没有对应的取消请求。游戏自我最小化
  时宿主跟着最小化；游戏自己恢复时应用无法把宿主拉回来（这是协议限制），
  需要用户从任务栏恢复；反过来用户从任务栏恢复宿主会正常去还原游戏窗口。
- **没有焦点窃取**：`Focus()` / `Activate()` / `Raise()` 在 Wayland 下是空操作；
  `Show()` 只置标志并触发一次重绘。
- **`Hide()` 不支持**：toplevel 只能靠销毁 surface 来 unmap，会丢掉全屏/装饰/位置状态，
  嵌套流程目前不需要它。
- **键盘**：只把 evdev code 直传（`wl_keyboard.key` 就是 evdev），不使用合成器 keymap；
  modifiers 用 xkb 掩码（与 X11 的 ShiftMask/Mod1Mask 位序一致），所以 Alt+Shift+A/Q 不变。
- **全屏尺寸**：取 configure 的逻辑尺寸 × scale，正好等于显示器物理分辨率；
  超分后的画面因此是 1:1 像素，不再被合成器二次放大。

## 7. 已移除的临时处理

`3c90e83` 曾在 `NestedInput.SendPointerPosition` 里每秒强制发一次 `MotionAbsolute`，用来让
相对位移参考点“跟上”游戏自己挪走的光标。当时可见光标还不能跳，这只能在 1 秒内把嵌套指针
拉回宿主位置。现在 `wp_pointer_warp_v1` 生效、sway 一报 `cursor_hint_*` 就重设参考点，
这个定时器既多余又会和参考点打架，已经删掉；归档说明见
`.agent/M2b-归档-指针周期绝对刷新.md`。点击前的绝对定位（`forceAbsolute`）保留：
XWayland 游戏用 `XWarpPointer` 移动内部指针时没有 hint，点击时补一次绝对定位仍然必要。

## 8. 参考

- 协议：`/usr/share/wayland-protocols/staging/pointer-warp/pointer-warp-v1.xml`
- KWin 全局：`wp_pointer_warp_v1`（2026-11 的 6.8.2 已验证存在）
- 分数缩放：`wp_fractional_scale_manager_v1` + `wp_viewporter`
- libei / liboeffis：`/usr/include/libei-1.0/`（作为替代方案已排除，见下）

## 9. 已排除的替代方案（留档）

| 方案 | 结果 |
|---|---|
| `XWarpPointer`（X11 宿主） | 只改 XWayland 影子指针，动不了 KWin 可见光标（实测两次） |
| 直接调 KWin `org.kde.KWin.EIS.RemoteDesktop.connectToEIS` + libei | 能建指针设备、能发事件，但设备 region/尺寸为 0，绝对坐标空间未定义（`0.5` 截断成 `0,0`，大数值被钳到边缘） |
| xdg-desktop-portal `RemoteDesktop` + `liboeffis` + libei | 授权框正常、能拿到 EIS fd，但同样没有 region；补 `ScreenCast` 屏幕流才能定义坐标，会触发屏幕共享授权和常驻指示器 |
| libei 相对位移 + 按键 | 可用（本次自动化测试就用它发 Alt+Shift+A / 按键），但不能定位到绝对坐标 |