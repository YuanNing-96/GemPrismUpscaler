> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# 归档：指针的“每秒强制绝对定位”兜底

> 归档日期：2026-11-11
> 相关提交：`3c90e83`（引入）、`2af625a`（原生 Wayland 宿主 + 真正的 warp）
> 结论：**已移除**。有了 `wp_pointer_warp_v1` 之后这个定时器多余，而且会和 sway 回报的
> warp 参考点互相拉扯。代码不再保留，恢复方式见文末。

## 1. 当时解决的问题

`3c90e83` 时宿主窗口还是 X11（XWayland），`XWarpPointer` 动不了 KWin 的可见光标。
游戏（Wine）用 `zwp_locked_pointer_v1.set_cursor_position_hint` 把嵌套合成器里的指针
挪到对话框默认按钮上，于是出现两件事：

- 用户看到的光标不动（这是后来才解决的）；
- 我们发给嵌套合成器的相对位移还以旧位置为基准，**悬停要点一下/晃一下才准，点击会落在旧位置**。

当时的兜底（`NestedInput.SendPointerPosition`）：

```csharp
bool refreshAbsolute = Environment.TickCount64 - _lastAbsoluteAt >= 1000;
if (forceAbsolute || !_havePointerPosition || refreshAbsolute)
{
    _virtualPointer.MotionAbsolute(time, x, y, layoutWidth, layoutHeight);
    _lastAbsoluteAt = Environment.TickCount64;
}
else
{
    _virtualPointer.Motion(time, new WlFixed(dx), new WlFixed(dy));
}
```

再加上点一下按钮前也强制一次绝对定位（`ButtonPress/ButtonRelease` 的 `forceAbsolute: true`），
以及 sway 补丁通过 IPC 报告 `cursor_hint_*` 后调用 `SetPointerReference`。

## 2. 为什么删掉定时器

- `wp_pointer_warp_v1` 生效后，KWin 的光标真的会跳到游戏想要的位置；sway 一报 hint，
  `MirrorClientCursorWarp` 就把相对位移参考点重设到同一个位置，参考点与真实位置一致。
- 每秒一次的 `MotionAbsolute` 反而会把嵌套指针硬拉到“宿主演算出来的位置”。在参考点
  已经正确的情况下，这是一次多余的位置跳变；如果宿主演算和合成器的落点有 1px 差异，
  还会在悬停时产生抖动。
- 它只在“光标没跳”的旧模式下才有意义；那个模式现在只剩 `GEMPRISM_HOST_X11=1` 回退，
  且回退时也没有可见跳转可言，定时器仍然帮不上忙。
- 记录删除前的一次实机日志：`nested input trace: ABSOLUTE pos=… force=False` 每约 1 秒
  出现一次，与 `RELATIVE dx=…` 交错，说明它在相对流中间插入了绝对定位。

## 3. 保留了什么

- `SetPointerReference(x, y)`：sway 报告 warp 后重设参考点（这是主路径）。
- 点击/进入时的一次绝对定位（`forceAbsolute: true`）：XWayland 游戏用 `XWarpPointer`
  移动内部指针时没有 hint，点击前补一次绝对定位仍然必要。
- 没有 sway 补丁时（系统 sway）：没有任何自动兜底，悬停需要“晃一下”，点击仍然准确
  （点击时的绝对定位仍在）。这一点已写进 `docs/dev/history/pointer-warp.md` §5。

## 4. 如何恢复（如果以后又需要）

在 `NestedInput` 里加回：

```csharp
private long _lastAbsoluteAt;
...
bool refreshAbsolute = Environment.TickCount64 - _lastAbsoluteAt >= 1000;
if (forceAbsolute || !_havePointerPosition || refreshAbsolute)
{
    _virtualPointer.MotionAbsolute(time, (uint)Math.Max(0, x), (uint)Math.Max(0, y),
        (uint)Math.Max(_outputWidth, _layoutWidth),
        (uint)Math.Max(_outputHeight, _layoutHeight));
    _lastAbsoluteAt = Environment.TickCount64;
}
```

恢复前先确认：当前宿主后端是 X11 回退、或 sway 没有打 hint 补丁、或实际观察到
“悬停要晃一下”。如果只是 `GEMPRISM_HOST_X11=1` 下没有可见跳转，不需要它。