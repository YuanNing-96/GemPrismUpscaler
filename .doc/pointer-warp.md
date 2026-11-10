# 指针跳转（游戏驱动的光标移动）

本文记录“游戏自己把鼠标移到某个按钮上”这类行为在嵌套模式下的现状、可行方案和后续实现路线。结论：**v1.0 不支持**，但保留了一条清晰的落地路径（原生 Wayland 宿主窗口 + `wp_pointer_warp_v1`）。

## 1. 现象

视觉小说/游戏在弹出“确认退出”之类的窗口时，会把指针自动移动到默认按钮上，并高亮该按钮。KDE 直连（游戏直接作为 KWin 的 Wayland 客户端）时可以正常看到光标跳过去；嵌套模式下光标不动。

原因是两层指针：

- 嵌套合成器（私有 sway）内部的指针被游戏用
  `zwp_locked_pointer_v1.set_cursor_position_hint` 挪走了；
- 用户看到的光标是 KWin 的指针，位于 X11 宿主窗口上，两者没有同步。

## 2. 协议：wp_pointer_warp_v1

`wayland-protocols/staging/pointer-warp/pointer-warp-v1.xml`，KWin 6.8 已实现（宿主 registry 里的 global `wp_pointer_warp_v1` v1）。

请求：

```
warp_pointer(wl_surface surface, wl_pointer pointer,
             fixed x, fixed y, uint serial)
```

协议要求：

- `surface` 必须**持有指针焦点**（包括隐式 grab）；
- `serial` 必须是有效的 **enter serial**；
- `(x, y)` 是 surface-local 坐标，必须在 surface 内部。

也就是说：**只有拥有该 `wl_surface` 的 Wayland 客户端才能调用**。

## 3. 为什么现在用不了

嵌套模式的宿主窗口是 **X11（XWayland）**窗口（`NestedHostWindow`），XWayland 才是 KWin 的 Wayland 客户端；本应用拿不到该 surface 的 `wl_pointer` 和 enter serial。

已排除的替代方案：

| 方案 | 结果 |
|---|---|
| `XWarpPointer`（宿主） | 只改 XWayland 影子指针，**动不了 KWin 可见光标**（实测两次） |
| 直接调 KWin `org.kde.KWin.EIS.RemoteDesktop.connectToEIS` + libei | 能建指针设备、能发事件，但设备 **region/尺寸为 0**，绝对坐标空间未定义（`0.5` 被截断成 `0,0`；大数值被钳到边缘），不可靠 |
| xdg-desktop-portal `RemoteDesktop` + `liboeffis` + libei | 授权框正常、能拿到 EIS fd，但同样没有 region；补 `ScreenCast` 屏幕流才能定义坐标，会触发屏幕共享授权和常驻指示器，体验不可接受 |

补充：portal 的 `NotifyPointerMotionAbsolute` 需要 `stream` 参数，只有选取了屏幕流才有意义；所以 portal 路线的代价是“屏幕共享”。

## 4. 后续实现路线（原生 Wayland 宿主窗口）

把 `NestedHostWindow` 从 X11 改成原生 Wayland 的 `xdg_toplevel`，即可直接使用 `wp_pointer_warp_v1`：

1. **窗口**：`wl_surface` + `xdg_surface`/`xdg_toplevel`，处理 configure/ack、`set_title`、`set_app_id`、`set_fullscreen`、`set_minimized`；装饰用 `zxdg_decoration_manager_v1` 请求服务端装饰（KWin 支持）。
2. **输入**：`wl_seat` 的 `wl_pointer`（enter/motion/button，**保存 enter serial**）、`wl_keyboard`。
3. **呈现**：`VulkanBlitter` 已经支持 `VK_KHR_wayland_surface`（叠层模式在用），只需传入 `wl_display` + `wl_surface`。
4. **warp**：游戏请求移动时，把目标点换算成宿主窗口内坐标，调用
   `warp_pointer(host_surface, wl_pointer, x, y, enter_serial)`。
   - 位置越界或 serial 过期会被合成器拒绝，直接忽略即可；
   - 全屏/窗口化都适用，坐标来自游戏在嵌套桌面里的位置。
5. **来源**：短期内仍可用下面的 sway hint IPC 取得游戏 warp 的位置；长期看新 Proton 会在嵌套 sway 内直接使用 `wp_pointer_warp_v1`，届时需要 sway 侧也实现该协议（当前 sway 1.12 未实现），应用再从 sway 取位置。

验证点：KDE 直连 A/B、窗口/全屏、多显示器、serial 过期。

## 5. 当前替代（已实现，v1.0 保留）

给私有 sway 打一个小补丁，把游戏请求的 warp 位置通过 IPC 暴露出来：

- 补丁：`tools/patches/sway-1.12-cursor-hint-ipc.patch`
- 构建：`tools/build-nested-sway.sh`
- 接口：`swaymsg -t get_seats` 增加 `cursor_hint_seq` / `cursor_hint_x` / `cursor_hint_y`
- 应用：`NestedUpscaleSession` 轮询该序号，变化时重设 `NestedInput` 的相对位移参考点（点击前也会发一次绝对定位）

效果：悬停不再需要“晃一下”、点击位置准确；**但 KWin 可见光标仍然不跳**——这正是需要 `wp_pointer_warp_v1` 的部分。

## 6. 参考

- 协议：`/usr/share/wayland-protocols/staging/pointer-warp/pointer-warp-v1.xml`
- KWin 全局：`wp_pointer_warp_v1`（2026-11 的 6.8.2 已验证存在）
- libei / liboeffis：`/usr/include/libei-1.0/`
