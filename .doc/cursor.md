# 光标形状与 X11 光标跳转（G5）

**状态（2026-11-12）**

- 方案 A 已实现：Wayland 宿主的命名光标形状转发 + X11 游戏的嵌套指针跳转轮询。
- 探针发现 **Wine 实际用的是图像光标**（`cursor_shape` 为空），所以 A 只能覆盖使用
  `wp_cursor_shape_v1` 的客户端；腐姬/秽翼按钮上的“手”仍然显示默认箭头。
- 完整修复需要方案 B（把光标图像从 sway 转发到宿主），成本约 1–1.5 人日，列为
  v1.0 发布后的优化项；是否提前到 v1.0 由用户决定（见 §5）。

---

## 1. 现象

- **光标形状未转发**：游戏按钮上应该是手形（或文本、等待），宿主窗口上一直是普通箭头。
- **X11 模式的光标跳转**（A2-5）：游戏弹出“退出确认”时，Wine 会把指针移到默认按钮上；
  KDE 直连会跟着跳，嵌套模式下宿主的光标不动（点击和游玩本身正常）。

## 2. 根因

嵌套模式有两层指针：

- 游戏的光标在私有 sway 里，由 `wl_pointer.set_cursor`（图像）或 `wp_cursor_shape_v1`
  （命名形状）设置；
- KDE 看到的是宿主窗口的光标，只能由宿主自己用 `wp_cursor_shape_v1` 或
  `wl_pointer.set_cursor` 设置。

sway 知道当前光标（`sway_cursor.image` 是命名形状，`image_surface` 是图像），但没有通过
IPC 暴露，所以宿主无从得知。

X11 跳转是另一条路径：Wine 的 X11 驱动用 `XWarpPointer` 移动内部指针，不产生
`zwp_locked_pointer_v1.set_cursor_position_hint`，我们现有的 sway hint 补丁收不到。

## 3. 方案 A（已实现）

### 3.1 Wayland 命名形状

- sway 补丁 `tools/patches/sway-1.12-cursor-hint-ipc.patch` 在 `get_seats` 里增加
  `cursor_shape`：客户端用 `wp_cursor_shape_v1` 时是标准形状名（`default`/`pointer`/`text`…），
  用图像光标时为空串。
- 应用侧 `NestedUpscaleSession.MirrorClientCursorWarp` 每次读取该字段，调用
  `IHostWindow.SetCursorShape`；Wayland 宿主绑定 `wp_cursor_shape_manager_v1`，
  在渲染线程用 `wp_cursor_shape_device_v1.set_shape(enter_serial, shape)` 设置 KDE 光标。
- 好处：使用 KDE 原生光标，大小/主题/缩放一致，不需要传像素。

### 3.2 X11 指针跳转

- `NestedWindowProbe.TryGetPointerPosition` 在嵌套 XWayland（`DISPLAY=:2`）上调用
  `XQueryPointer`。
- `NestedInput` 在 XWayland 路径每次 `XWarpPointer` 后记录参考点；会话的 16ms
  光标同步发现实际位置偏离参考点超过 2px，就认为游戏自己挪了指针，按现有坐标映射
  `WarpPointer` 到宿主，并更新参考点。
- 不产生额外 sway 补丁；对视觉小说类（指针事件不密集）足够。

### 3.3 宿主窗口只保留最小化/关闭

- Wayland 宿主在窗口化时设置 `xdg_toplevel.set_min_size/logical` 和
  `set_max_size/logical` 为同一个值（`ApplyWindowSizeLimits`）：KWin 因此隐藏最大化按钮、
  禁止用户拖拽缩放；进入全屏前清掉限制，退出全屏后恢复。窗口尺寸仍由游戏窗口驱动
  （游戏自己改尺寸时会话重建宿主）。
- 实测：KWin 报 `resizeable=false / maximizable=false`，标题栏只剩最小化和关闭。
- X11 宿主（`GEMPRISM_HOST_X11=1`）仍是服务端装饰的旧行为；它只是回退路径。

## 4. 探针结果（2026-11-12 04:0x）

腐姬（Proton/Wine Wayland）在嵌套会话里，把指针移到设置界面的按钮/滑轨/文字上，
`swaymsg -t get_seats` 的 `cursor_shape` 从 `default` 变成 **空串**，说明 Wine 给的是
`wl_pointer.set_cursor` 的图像光标，不是 `wp_cursor_shape_v1`。

结论：A 的命名路径对 Wine 游戏无效；要显示真正的“手”，必须转发图像（方案 B）。

## 5. 方案 B（图像光标，v1.0 之后）

1. sway 补丁：在 `cursor_set_image_surface` 设置/提交光标 surface 时，读取
   `surface->current.buffer`（shm），快照 ARGB 像素 + 宽高 + hotspot + 序号；
   通过一个新的 IPC 请求（或 `get_seats` 的可选字段）返回，客户端只在序号变化时拉取。
2. 宿主：绑定 `wl_shm`，创建光标 `wl_surface`，把 ARGB 上传为 buffer，调用
   `wl_pointer.set_cursor(enter_serial, surface, hotspot_x, hotspot_y)`；图像尺寸按
   游戏像素直接用（KDE 直连也是同样大小），清空时设 null 恢复箭头。
3. X11 游戏可以复用同一套宿主上传：用 `XFixesGetCursorImage` 在嵌套 X 服务器上取图像，
   不需要 sway 补丁。
4. 风险：shm/dmabuf 兼容、光标 surface 生命周期、缩放；需要实机看效果。

## 6. 已知限制

- 图像光标（Wine/自定义美术）在 v1.0 仍显示默认箭头，除非做方案 B。
- 命名形状只覆盖 `wp_cursor_shape_v1` 客户端；sway 自己的默认光标是 `default`。
- X11 宿主回退路径不转发形状，也不做固定尺寸（仅 Wayland 宿主实现了无最大化）。

## 7. 验收建议

- Wayland 游戏（秽翼/腐姬）：鼠标移到按钮、文本输入框、等待光标上，观察形状是否跟随。
  当前预期：按钮仍是箭头（B 未做）。
- X11 游戏（腐姬 `PROTON_ENABLE_WAYLAND=0`）：触发“退出确认”弹窗，观察宿主光标是否
  自动移到默认按钮；`GEMPRISM_TRACE_INPUT=1` 时日志应出现
  `nested cursor warp source=x11-pointer ...`。
- 宿主窗口：标题栏只有最小化和关闭，拖拽边框不能改变大小，双击标题栏不能最大化。

## 8. 代码与补丁

| 位置 | 作用 |
| --- | --- |
| `tools/patches/sway-1.12-cursor-hint-ipc.patch` | `cursor_hint_*` + `cursor_shape` |
| `tools/build-nested-sway.sh` | 重建并安装带补丁的 sway |
| `src/.../Nested/WaylandHostWindow.cs` | `wp_cursor_shape_v1`、固定窗口尺寸 |
| `src/.../Nested/NestedWindowProbe.cs` | `XQueryPointer` |
| `src/.../Nested/NestedInput.cs` | XWayland 注入后更新参考点 |
| `src/.../Engine/NestedUpscaleSession.cs` | 读取形状、镜像 X11 指针跳转 |

改动 sway 补丁后必须重新运行 `tools/build-nested-sway.sh`（需要 meson/ninja，脚本说明里有
venv 用法），否则运行时用的还是旧二进制。窗口图标列为 v1.0 之后的优化项（见
[backlog.md](backlog.md)）。