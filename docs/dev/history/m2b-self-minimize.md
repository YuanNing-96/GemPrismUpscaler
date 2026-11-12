> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# 游戏自我最小化：X11 已镜像，Wayland 归档（不解决）

> 更新日期：2026-11-11（原 04:0x 的“不解决”结论只适用于 Wayland）
> X11 结论：**已修复**，按 KDE 直连的行为把最小化请求镜像到宿主窗口。
> Wayland 结论：**不处理**。KDE 直连示例视觉小说 B （Wine Wayland）时最小化同样异常，属于游戏/上游行为，
> 且 sway 不记录 Wayland 视图的最小化状态，没有可靠信号。
> 曾经的几何启发式（`b3c7893`，已由 `5b2bc21` 回退）**不要再用**。

## 1. 现象

- **Wine Wayland（示例视觉小说 B）**：按 Esc / 点 CSD 最小化，游戏把窗口变成一条很小的 toplevel
  （`sway get_tree` 里 `rect=160x31+1517+764`，标题不变），随后不再出帧；宿主窗口保持
  原来的 1286×752，显示最后一帧（看起来像黑边/残影），CSD 消失。KDE 直连启动同样最小化异常。
- **Wine X11/XWayland（示例视觉小说 B x11 模式）**：按 Esc 后宿主窗口“黑闪一下”，窗口不最小化，游戏停笔。
  `sway get_tree` 里 rect 仍是 1280×720、`visible=true`、`focused=true`，`WM_STATE` 仍是 Normal。

## 2. X11：真正的信号是 ICCCM 的 WM_CHANGE_STATE

Wine 按 ICCCM 向嵌套 XWayland 的 root 发 `WM_CHANGE_STATE(IconicState)`（`data.l[0]=3`，
`window` 是游戏窗口）。实测（2026-11-11 04:32，`DISPLAY=:2` 上监听 root 的
`SubstructureNotifyMask`）：

```
04:32:09.520 ClientMessage win=0x2400001 type=WM_CHANGE_STATE fmt=32 d=3,0,0,0,0
04:32:09.524 frame-trace ... mean=0 dark%=99      ← 客户端自己画的全黑帧
04:32:09.532 frame-trace ... mean=49              ← 又画回标题画面，然后停笔
```

- **KDE 直连**（Lutris + Proton，`PROTON_ENABLE_WAYLAND: '0'`）：KWin 收到请求就最小化窗口，
  用户看到的是“窗口进任务栏”，黑帧被最小化动画盖掉。
- **嵌套模式**：sway 没有最小化概念，忽略请求。`WM_STATE` 一直是 Normal、surface 保持 mapped，
  所以旧实现的 `NestedWindowProbe.IsIconified()`（读 WM_STATE / `_NET_WM_STATE_HIDDEN`）
  **一次都读不到**，X11 的最小化镜像从没触发过；“闪一下黑”就是宿主把客户端那帧全黑呈现出去了。

## 3. X11：最终做法（只吞黑帧，宿主不跟随最小化）

1. **监听请求**：`NestedWindowProbe` 在 root 上 `XSelectInput(SubstructureNotifyMask)`，
   `DrainEvents()` 里挑出 `WM_CHANGE_STATE(IconicState)` 的 ClientMessage，
   `TryTakeMinimizeRequest()` 逐个交给会话。**不能选 `SubstructureRedirectMask`**：
   那是 sway 的 WM 选择，抢过来会让 sway 收不到映射/最小化请求。
2. **只抑制那一帧全黑**：`MirrorMinimize` 设 `SuppressBlackUntil = now + 600ms`，
   `RenderState` 在窗口期内丢弃“整帧全黑”的候选帧（采样网格 mean≤3 且 95% 以上像素亮度<8），
   宿主保持上一帧、不闪。**不 unmap 游戏窗口、不最小化宿主**，游戏本身照常运行（和改之前一致）。

## 4. 为什么没有做“宿主跟着最小化”（试过，放弃）

- 第一版：收到请求 → 最小化宿主；从任务栏恢复时用 unmap+map 唤醒游戏。
  结果：Wine 认为窗口还是最小化，恢复后 **~40ms 又发一次最小化请求**，用户“一点开就瞬间被自动最小化”；
  重建的 toplevel 没有 buffer，capture 卡在 1×1，画面/悬浮全停。
- 第二版：改成真 WM 序列（iconify = `WM_STATE=Iconic` + unmap；restore = `WM_STATE=Normal` + map）。
  结果：仍然是恢复后 ~25ms 又被最小化。
- 第三版：在 KDE 直连上用 `x11-watch` 抓 KWin 的真实顺序，完全照搬：
  ```
  最小化：_NET_WM_STATE 加 HIDDEN → Unmap → WM_STATE=Iconic
  恢复：  _NET_WM_STATE 去 HIDDEN → Map   → WM_STATE=Normal
  ```
  （KDE 直连恢复后客户端没有再发 WM_CHANGE_STATE。）
  嵌套里照搬后仍然在 map 之后 ~25ms 收到同一条最小化请求：
  ```
  05:15:30.481 Map win=0x2400001
  05:15:30.508 ClientMessage win=0x2400001 type=WM_CHANGE_STATE d=3
  ```
  结论：Wine 的 Win32 最小化状态（WS_MINIMIZE）不会被客户端的 map 清除，游戏恢复后自己又调用
  了一次最小化；KDE 直连能恢复是因为……（未查明，可能和 KWin 的 X11 激活/合成路径有关，需要更深的
  Wine 侧分析或 sway WM 补丁）。
- 因此产品上选择：宿主不跟随最小化，只把“那一帧全黑”吞掉。游戏行为与改之前完全一致
  （你原来的验收记录就是“不影响游戏”）。

## 4.1 验证（最终版）

```
05:17:04.302 asked to minimize; holding the black frame (host keeps the last frame)
05:17:04.306 frame-trace mean=0 dark%=99 → nested black frame held back
05:17:04.312 frame-trace mean=49        → 标题画面，宿主不闪
KWin: min=false（宿主没有被最小化）
```

## 5. 为什么 Wayland 不处理

1. KDE 直连（游戏直接是 KWin 客户端）时，示例视觉小说 B Wayland 的最小化本身就不正常——这是游戏
   （Wine/引擎）或上游合成器行为，不是嵌套模式的回归。
2. xdg-shell 没有“最小化”状态，也没有取消最小化的请求：`xdg_toplevel.set_minimized()` 一旦
   发出无法撤销，客户端只能重建窗口来“恢复”；sway 1.12 也不记录 Wayland 视图的最小化状态
   （`get_tree` 完全看不出来）。
3. 强行用几何/无帧等启发式判定会误伤：静态对话框本来就不出帧，正常小窗口和“最小化条”
   尺寸接近（示例视觉小说 A 确认框 197×82，示例视觉小说 B 最小化条 160×31）。

## 6. 作废的几何启发式（`b3c7893`，已在 `5b2bc21` 回退）

- 判定 `rect.Width <= 200 && rect.Height <= 80` 视为最小化；命中后 `Host.Iconify()`；
- 尺寸恢复时用 `RebuildHost(state, WindowedWidth, WindowedHeight)` 重建宿主；
- 探针验证是通的（160×31 → 宿主最小化；回 400×300 → 宿主重建、焦点回来），
  但因为第 5 节第 1 条，产品上不需要 Wayland 的这个行为，回退。
- **教训**：几何阈值离真实对话框太近，且对“最小化”这种状态用尺寸推断本质上是错的。

## 7. 已知边界

- Wayland 游戏仍不镜像（没有信号）。
- 宿主被最小化后必须从任务栏恢复：xdg-shell 没有“客户端撤销最小化”的请求。
- 用户自己从任务栏最小化宿主（不是游戏发起）不处理：游戏继续跑，恢复后画面继续。
- 游戏在最小化状态下退出：宿主随窗口消失一起移除。
- X11 的镜像只对 `X11Window != 0` 的窗口生效；纯 Wayland 游戏窗口不走这条路径。

## 8. 相关记录

- 分组文档：`开发机归档/M2b-新路线-验收问题分组-20261111.md` G2
- 设计文档：`docs/dev/architecture.md` §6.9.6
- 后续修复方案与成本评估：[minimize.md](minimize.md)
- 更新记录：`docs/dev/history/changelog.md`（v0.5.0 “X11 游戏的最小化镜像到宿主窗口”）
- 会话日志证据：`/tmp/gp-min2.log`（04:38 两轮最小化/恢复）、`/tmp/x11-watch*.log`
- 回退提交：`5b2bc21`（回退 `b3c7893`）
