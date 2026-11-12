> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M2b v1.0 全屏策略

记录时间：2026-11-09 14:20（Asia/Shanghai）
决策人：用户
适用版本：GemPrismUpscaler v1.0

## 1. v1.0 范围决策

v1.0 **不承诺兼容全屏模式**。

- 默认用户以**窗口模式**启动游戏，以窗口模式做超分。
- 全屏处理**保留**，但只做 best-effort，不做强制兼容、不做特殊裁剪/提升。
- 全屏下出现的问题，如果 Lutris+Proton 直连也有，按既有原则不修；如果直连没有，也不作为 v1.0 阻塞项。
- 不引入 rootful XWayland、不引入 gamescope 作为 v1.0 必需依赖。
- 不保留“识别伪全屏并裁 800×600”的兼容逻辑。

## 2. 两个方案的准确区别

排除伪全屏裁剪后，两个方案的差异主要在**第 1 层：游戏窗口是否被判定为全屏**。
第 2 层（抓取区域）和 host 全屏请求可以保持一致。

### 方案 A：扩展判定 + 必要时强制 sway fullscreen

- 第 1 层：
  - `fullscreen_mode != 0`，或
  - 大尺寸 4:3 窗口（高度接近 output、宽度明显小于 output），调用 sway `fullscreen enable` 提升。
- 第 2 层：抓实际窗口 rect（不再裁 `WindowedWidth×Height`）。
- 第 3 层：host 仍走 EWMH 全屏（清 maximize、固定显示器、等待 4K）。

效果：

- 显式 `_NET_WM_STATE_FULLSCREEN` 的游戏：正常走全屏。
- 没有 fullscreen 标记、但创建了 2902×2160 之类大窗口的 Wine 游戏：
  - 会被 A 判定为全屏并强制 sway fullscreen；
  - 窗口变成 3840×2160，但 X11/Wine 内容可能仍在 800×600 左上角，出现黑屏/错位；
  - 退出全屏时可能和游戏自己的窗口状态打架。
- 误判风险：真正的大尺寸窗口化 4:3 游戏会被强制全屏，用户无法保持窗口。

### 方案 B：只认真正的 fullscreen 状态

- 第 1 层：只认 `fullscreen_mode != 0`；不做大窗口提升。
- 第 2 层：抓实际窗口 rect。
- 第 3 层：host 仍走 EWMH 全屏。

效果：

- 显式全屏的游戏：和 A 一样走 host 全屏。
- 没有 fullscreen 标记的大窗口：保持窗口模式，host 也保持窗口；
  - 不会出现“强制成 4K 后 800×600 左上角/黑屏”；
  - 游戏可能看起来没有全屏，但这和 v1.0 “不强制兼容全屏”一致。
- 无强制提升带来的误判/退出打架。
- 第 2 层不再有 800×600 裁剪，窗口模式仍可正常超分。

### 第 3 层不能省

如果“简化”把 host EWMH 全屏也删掉：

- host 只做 resize/普通窗口；
- KWin 会给出 `3840×2073` 左右的客户区，或者带标题栏；
- 显式全屏的游戏也无法得到真正的 4K host 全屏。

所以第 3 层不是“全屏兼容 hack”，而是 host 呈现自身的正确性；无论选 A 还是 B 都应保留。

## 3. 已采用

v1.0 已采用 **方案 B + 保留 host EWMH 第 3 层**（2026-11-09 14:30）：

- 默认窗口模式，窗口模式超分；
- 显式全屏时 host 正常全屏；
- 不做伪全屏、不做强制提升、不做 rootful/gamescope 兼容；
- 全屏异常作为 known issue，不阻塞 v1.0。

这样代码更简单，也不会把“窗口化的大 4:3 游戏”误强制成全屏。

## 4. 代码层面的差异（已执行）

B 已移除：

- `ShouldPromoteToFullscreen`；
- `PromotedFullscreen`、`SwayFullscreenRequested`、`PromotionBlocked`、`FullscreenRequestedAt`；
- `_tracker.SetFullscreen`（方法已一并删除）；
- 相关的超时/重试分支。

保留：

- `NestedHostWindow.SetFullscreen` 的 EWMH 请求、清 maximize、固定显示器、等待；
- `ApplyOriginal` 的 host fullscreen/restore 逻辑；
- 窗口模式的 crop/upscale。

## 5. 验收口径

v1.0 全屏相关只验收：

- 显式全屏时 host 不出现 `3840×2073 + 标题栏`；
- 全屏异常不导致 host 崩溃/进程泄漏；
- 默认窗口模式超分正常。

不验收：

- 无 fullscreen 标记的大窗口自动全屏；
- X11 全屏 800×600 左上角/黑屏；
- Wine Wayland 全屏居中/输入映射；
- 全屏下超分。