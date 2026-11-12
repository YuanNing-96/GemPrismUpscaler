> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M2b 新路线 · 验收问题分组（2026-11-11）

> 来源：`开发机归档/M2b-新路线-用户验收手册.md` §4 结果记录表 + “已知问题记录”。
> 代码基线：`6f83713`（`render: share one Vulkan instance across host windows`，已推送）。
> 用法：每组 1–2 个问题，按本文顺序逐组讨论 → 定方案 → 修复 → 你复验。组内结论直接回填在本文。
>
> 优先级：**P0 阻塞/会丢数据**，**P1 影响日常使用**，**P2 观感/打磨**，**P3 记录即可**。

## 分组总览

| 组 | 包含项 | 一句话现象 | 优先级 | 状态 |
|---|---|---|---|---|
| G1 | D2 关闭弹窗标题栏 | 关掉“确认结束”的标题栏 → 整个游戏一起退出 | P0 | **已解决（用户复验通过）** |
| G2 | A2-1（Wayland）、A2-3（X11） | 游戏自己最小化时，宿主不跟随（Wayland 还会 CSD 消失 + 黑边） | P1 | **X11 已修复（待复验）；Wayland 归档不处理** |
| G3 | A2-2（Wayland）、A2-4（X11） | 游戏内改窗口大小，KDE 窗口尺寸不变，比例一变就有黑边 | P1 | **已解决（用户复验通过）** |
| G4 | B1、E1/E2/E3 | 超分/还原的第一帧被拉伸铺满，过一会儿才归位；游戏内全屏切换现象未记录 | P2 | 待讨论 |
| G5 | 已知项·光标形状、A2-5 | 光标形状不转发；X11 模式游戏弹窗时光标不跳 | P3 | **已通过（X11 跳转已验收；形状无测试场景，记通过+已知问题）** |
| G6 | C4、D4 | 中文输入法没条件验；D4 测试说明不清楚 | 验证缺口 | 待讨论 |

不修/不阻塞项见文末“附录 A”。

---

## G1 · 关闭弹窗的标题栏会把游戏一起关掉（P0）

**现象（你的原话）**
> 比如示例视觉小说 A 的退出游戏确认弹窗，如果我给这个弹窗的标题栏点击关闭按钮，连带着示例视觉小说 A游戏也会被关闭。

**初步定位（已确认）**
`NestedUpscaleSession.HandleHostClose`：

```csharp
if (_waitForCommand) { SetExitReason("host window closed"); _stop = true; return; }
```

`--nested`（一次性模式）下 `_waitForCommand = true`，所以**任何**宿主窗口被关（包括弹窗）都会结束整个会话，
`Stop()` 接着杀掉游戏进程树。会话模式（`--nested-session`）下是另一套行为：只关掉那一个宿主窗口。

**可选方案**
- **A（推荐）** 统一成“关窗口 = 只关这个宿主”，只有最后一个宿主也关了、或者游戏进程退出，才结束会话。
  - 副作用：关掉弹窗宿主后，游戏里的对话框仍然存在但不可见，游戏可能停在“等待确认”。需要在方案里补一步：
    X11 游戏窗口可以给它的对话框发 `WM_DELETE_WINDOW`；Wayland 游戏窗口没有通用“关闭”请求，
    只能把宿主窗口隐藏/移除（游戏侧可能仍卡住）。
- **B** 只有**主窗口**的宿主关闭才结束会话；弹窗宿主的关闭一律转发成 A 的“发 WM_DELETE / 移除”。
- **C** 弹窗宿主干脆不允许关（拦截 `CloseRequested`，或弹窗不显示 KDE 关闭按钮）——最省事，但用户会觉得按钮坏了。

**已实现（2026-11-11 02:37）**
`HandleHostClose` 不再结束会话，而是把关闭请求转发给嵌套窗口：

```csharp
forwarded = _ipc.RunCommand($"[con_id={state.Id}] kill");
```

sway 的 `kill` 对 Wayland 窗口发 `wlr_xdg_toplevel_send_close`（客户端收到 `xdg_toplevel.close`），
对 XWayland 窗口发 `wlr_xwayland_surface_close`（`WM_DELETE_WINDOW`）——和 KDE 直连点标题栏 X
是同一个协议请求，所以弹窗会真的关掉、主窗口随即可用。客户端不理会请求时窗口保持原样
（KDE 直连也是这样）。被关闭的宿主镜像在对应窗口从 sway 树里消失后**立即**移除（不等 1 秒宽限期）。

**已验证（zenity）**：Alt+F4 关宿主 → 日志 `host close requested ... forwarded (ok=True)` →
zenity 进程退出 → 80 ms 后 `nested window removed; windows=0` → 会话仍存活。
**待复验**：示例视觉小说 A“退出确认弹窗点 X，游戏继续”的真实路径（见下）。

**需要你确认**
1. 关掉**主窗口**的标题栏，你期望是“退出整个游戏”还是“只关这个 KDE 窗口、游戏继续跑”？
   （现在的实现是：把关闭请求交给游戏，游戏自己决定——多数游戏会弹“确认退出”，确认后才退出。）
2. 如果游戏完全不理会关闭请求，你希望窗口保持（现状，与 KDE 直连一致）还是超时后强制关掉镜像？

---

## G2 · 游戏自己最小化时宿主不跟随（P1）

**最终结论（2026-11-11）**
- **X11/XWayland 游戏：已修复**，按 KDE 直连的行为把游戏的最小化请求镜像到宿主窗口。
- **Wayland 游戏：不处理（归档）**。示例视觉小说 B Wayland 下 Wine 把 toplevel 缩成 160×31，KDE 直连也有同样
  的最小化异常，且 sway 不记录 Wayland 视图的最小化状态，没有可靠信号。现象与决定见
  `开发机归档/M2b-归档-游戏自我最小化.md`。

**X11 的真实信号（2026-11-11 04:32 实测）**
Wine 按 ICCCM 向嵌套 XWayland 的 root 发 `WM_CHANGE_STATE(IconicState)`：

```
$ DISPLAY=:2 /tmp/x11-watch 0x2400001
04:32:09.520 ClientMessage win=0x2400001 type=WM_CHANGE_STATE fmt=32 d=3,0,0,0,0
```

- KDE 直连时 KWin 照办，窗口进任务栏（`min=true`）。
- 嵌套的 sway 没有最小化概念、忽略该请求：`WM_STATE` 始终 Normal，surface 保持 mapped，
  `get_tree` 里 `visible=true`、`focused=true`。所以旧实现里的 `IsIconified`（WM_STATE）**从来没读到过**，
  X11 的最小化镜像其实一直没触发。
- 游戏随后画出“全黑 → 标题”两帧然后停笔；宿主把那一帧全黑呈现出去，就是你看到的“按 Esc 后黑闪一下”。

**修复（2026-11-11 04:38 验证）**
1. `NestedWindowProbe` 在 root 上选 `SubstructureNotifyMask` 监听 `WM_CHANGE_STATE`（不能选
   `SubstructureRedirectMask`，那会把请求从 sway 手里抢走），`TryTakeMinimizeRequest()` 交出 xid。
2. `NestedUpscaleSession.MirrorMinimize`：`state.Minimized = true` + 宿主 `xdg_toplevel.set_minimized`，
   当帧起不再呈现，全黑帧根本不会进宿主。
3. 从任务栏恢复宿主 → KWin 发 `activated` configure → `WaylandHostWindow.Restored` →
   `RestoreMinimizedWindow`：按 WM 的方式 unmap+map 游戏窗口（`Deiconify`，间隔 30ms）把它唤醒。
4. unmap+map 会让 sway 销毁并重建 toplevel（con id 5→6→7…）；`RekeyWindow` 现在为新 toplevel
   重建 capture，否则宿主停在旧帧——这也是之前手动 deiconify 后画面卡死的根因。
5. 恢复瞬间被拆掉的 surface 会送回一帧全黑，恢复后 500ms 内丢弃（`SuppressBlackUntil`），
   避免“恢复时再闪一下”。

**验证日志（两轮都通过）**
```
04:38:18.103 asked to minimize; minimizing host          → KWin min=true
04:38:26.828 nested host restored; deiconifying
04:38:26.863 black frame suppressed after restore       (mean=0 dark%=99)
04:38:26.866 view re-created 'id=6' ... id 5 -> 6
04:38:26.869 capture re-created 1280x720
04:38:26.923 frame-trace mean=49                        → 标题画面
```
第二轮：48.922 请求最小化 → 50.760 恢复、suppressed、6→7、capture 重建、标题帧。恢复后截图确认
宿主显示标题画面、无黑闪。

**已知边界**
- Wayland 游戏仍不镜像（没有信号，且 KDE 直连同样异常）。
- 宿主被最小化后必须从任务栏恢复：xdg-shell 没有“客户端撤销最小化”的请求，这是协议限制。
- 用户自己从任务栏最小化宿主（不是游戏发起）时不处理：游戏继续跑，宿主恢复后画面继续。
- 游戏在最小化状态下退出时，宿主随窗口消失一起移除。

**作废的方案（保留警示）**
`b3c7893` 曾用“宽 ≤200 且高 ≤80 视为最小化”的几何启发式（针对 Wayland 的 160×31），并用
`RebuildHost` 恢复；因为 KDE 直连也有同样问题，已在 `5b2bc21` 回退。几何启发式不要再用：
示例视觉小说 A 的 197×82 确认框和“最小化条”尺寸太接近。

## G3 · 游戏改窗口大小，宿主窗口不跟随（P1）

**现象**
- Wayland（A2-2）：游戏内切换窗口大小，咱们的窗口大小没变；比例变化时窗口里出现黑边。
- X11（A2-4）：同样，KDE 窗口大小没变；比例变化时画面出现黑边。

**已做的实验（事实）**
- 用宿主窗口探针直接调 `SetOriginalSize(1000,700)`（当前 800×600）：调用返回后窗口仍是 800×600，
  之后 20 秒也没有收到新的 configure。
  说明 `xdg_toplevel.set_min_size/set_max_size` **不能让 KWin 把已有窗口改成那个尺寸**。
- “给窗口塞一个更大的缓冲会不会让 KWin 放大窗口”还没测出来：探针在这个场景里先报
  `ErrorDeviceLost`（见附录 B），没能观察到 configure。正式程序里这个组合从未出现过
  （总是先全屏、再建大交换链），所以不能据此下结论。

**结论**：xdg-shell 里**没有“把窗口调整到 WxH”的客户端请求**（`move`/`resize` 是交互式的，需要 seat+serial）。
所以 Wayland 下要“跟随游戏窗口尺寸”，只有三条路：
- **A** 重建宿主窗口：销毁旧 `xdg_toplevel`，按新尺寸新建一个（会有闪烁、丢窗口位置/焦点，但尺寸正确）。
- **B** 接受 KWin 给的尺寸，画面按比例 letterbox（现状）。黑边是“设计如此”，不是 bug。
- **C** 折中：**只在比例变化时**重建窗口（缩放游戏窗口尺寸但比例不变时继续 letterbox），减少闪烁次数。

X11 宿主理论上可以用 `XMoveResizeWindow` 跟随（A2-4 没生效需要单独查，可能根本没走到
`ApplyHostRestoreSize`，或被 KWin 的几何约束覆盖）；但 A4 已经说明旧模式仅作归档，可以低优先级。

**已实现（2026-11-11 03:11）**：按你的决定，采用“重建宿主窗口”。

- `ApplyHostRestoreSize` 在 Wayland 宿主下不再调用 `SetOriginalSize`，而是记录目标尺寸，
  稳定 350ms 后 `RebuildHost` 销毁旧 `xdg_toplevel` 并按新尺寸新建一个（对话框会重新
  `set_parent`、焦点/标题保留）；X11 宿主仍原地 resize。
- toplevel 模式以前**根本没调用** `ApplyHostRestoreSize`（只在切换模式时用一次），这是
  “游戏改尺寸宿主不变”的直接原因；现在窗口 rect 变化就会走到这段逻辑。
- 重建后把上一帧重新呈现一次（`TryGetLastFrame`），否则新窗口没有 buffer、KWin 不会映射，
  看起来像“窗口消失”。顺带修了 `frame buffer too small`（抓帧尺寸与 buffer 约束不同步）
  和关窗后 `Stopped` 日志刷屏。

**已验证（合成客户端）**：GTK 探针 800×600 → 1000×700 → 640×480，宿主依次重建为
1001×701 / 641×480（1px 来自 1.5× 分数缩放取整），焦点在重建后立刻回来；
之后 Alt+Shift+A 超分再还原，回到的是**新的** 641×480，而不是最初的 800×600。
游戏自己切全屏/窗口（同探针）也能让宿主跟着全屏（3840×2160）再回到 800×600。

**待你复验**：真实游戏里改分辨率/窗口大小，宿主是否变成新尺寸（会有一次窗口闪烁）。

---

## G4 · 超分/还原的过渡拉伸 + 游戏内全屏切换（P2）

**现象（B1）**
> 进超分时第一帧是 4:3 窗口拉伸铺满屏幕（两侧无黑边），过一会儿才变成等比例全屏（两侧黑边）；
> 切回原窗口也是反过来的现象。

**证据（你这次运行的日志，已确认不是设计）**
```text
01:42:54.217 wayland host reconfigured logical=2560x1440 buffer=3840x2160 fullscreen=True
01:42:54.547 nested window '...' mode=upscale client=3840x2160
```
从“窗口变全屏”到“新尺寸画面出现”之间有 **330 ms**，这段时间 KWin 拿旧的 800×600 缓冲拉伸到
3840×2160，就是你看到的拉伸帧。切回时同理（3840×2160 的缓冲被压缩进 800×600 窗口）。

**可选方案**
- **A（推荐，改动小）** 提前建好目标尺寸的 blitter：`SetFullscreen` 之前就按 `_outputWidth/_outputHeight`
  创建交换链和管线，`configure` 一到就立刻 present。间隙从 ~330 ms 压到 1 帧（~16 ms），基本看不到。
  退出超分对称处理（先准备好小尺寸 blitter，再 `UnsetFullscreen`）。
- **B（彻底，改动大）** 固定使用“显示器物理尺寸”的大缓冲 + `wp_viewport` 源/目标：
  窗口化时把大缓冲裁成窗口比例、映射到窗口；全屏时铺满。窗口尺寸变化只改 viewport，不重建交换链，
  也不会再出现任何拉伸。代价是会话的坐标/尺寸模型要跟着改（`Host.Width/Height` 不再等于窗口像素）。
- **C** 记录为已知观感问题，不修。

**E1/E2/E3（游戏内全屏切换）**
你只写了“有问题”，没有具体现象。请补一句：是画面黑、尺寸不对、输入偏，还是和 B1 一样的拉伸？
我预期它们和 G3/G4 同源（游戏窗口 rect 变化 + 宿主尺寸不变），确认后并入本组一起修。

**E1/E2/E3 排查（2026-11-11 23:2x，用户配合复现）**
- 示例视觉小说 B：全屏窗口尺寸正常、画面正常（Wayland 多一个 CSD），和 KDE 直连一致 → **已知现象，不处理**。
- 示例视觉小说 A X11 全屏黑屏：**根因已定位**。切全屏后游戏渲染到一个 **override-redirect 的 X11 窗口**
  （`DISPLAY=:2 xwininfo` 等价物：`0xc00001 map=2 or=1 3838x2160+1+0`，同类名 steam_app_default），
  被 sway 管理的那个 toplevel（`0x2a00001`，3840×2160）反而没有 buffer（capture 一直是 1×1，
  一直重试）。KDE 直连之所以正常，是因为 KWin 直接把 override-redirect 窗口合成在屏幕上；
  toplevel 抓取看不到它。输出模式（`GEMPRISM_TOPLEVEL_CAPTURE` 不设）实测有画面、约 20fps，
  证明游戏确实在画、只是画在另一个窗口上。
  可选修法（待你批准）：toplevel 抓取拿不到 buffer 且窗口矩形≈整个输出时，回退到 **输出抓取**
  （`NestedCapture`）并按窗口矩形裁剪；窗口有 buffer 后再切回 toplevel。成本约 0.5–1.5 人日。
- 示例视觉小说 A Wayland 全屏（2026-11-12 02:5x–03:1x 复现+量化，截图 `开发机归档/M2b验收/20261112-*`）：
  - 嵌套输出 `HEADLESS-1` 3840x2160 @ scale 1；游戏 toplevel buffer 3840x2160，但内容只画在
    x=97..3276（3179x2160），右侧 564px 黑带。KDE 直连截图的窗口/内容约 3345x2160 贴左上，
    右侧露出桌面（更像 2230x1440 逻辑的无边框窗口，而不是铺满 2560x1440 的真全屏）。
  - 输入是**严重压缩**而不是简单偏移：在“返回標題”中心 (2750,1955) 注入点击无反应；在
    (3150,1955) 注入点击却选中了 y≈1163 的“文字快進”选项（截图证据：点击前后该行高亮从右
    选项换到左选项）；(2688,390)、(3839,924) 均无反应。按这组数据，游戏的有效输入区约在
    surface 左上 60%（y 方向比例约 1/1.68），x 方向还不能唯一确定（slider 只响应拖拽，不能
    当坐标探针）。
  - 运行时把嵌套输出 `scale 1.5` 后，游戏直接退出全屏（sway rect 回到 800x600），宿主仍停在
    3840x2160，画面/输入都不再可信。说明“对齐 KDE 逻辑几何”必须在**会话启动时**建好
    （SwayConfig），不能运行中改。
  - 下一步（待确认）：用 scale 1.5 / 逻辑 2560x1440 的**全新会话** + `GEMPRISM_TRACE_INPUT=1`
    做标定，拿到游戏输入空间后二选一：
    (a) 按标定结果在 NestedInput 做**输入补偿**，只对“全屏且内容有黑边”的窗口启用；
    (b) **不让游戏进它自己的全屏**：拦截全屏请求、保持窗口尺寸，由宿主全屏显示+超分，输入走
        已验证的窗口路径。两者都要动 `NestedUpscaleSession`/`NestedInput`/`SwayConfig`。

**决定（2026-11-12）**
1. G4 按 A 做：只额外处理“超分/还原”引起的尺寸变化；游戏自己改窗口尺寸不做特殊处理，按以前
   的方式处理，记为已知的可能问题，等出现异常游戏再说。
2. 示例视觉小说 A X11 / Wayland 全屏：v1.0 不解决。根因、证据、备选方案记录在 `docs/dev/history/fullscreen.md`。

---

## G5 · 光标形状与 X11 模式的光标跳转（P3）

**现象**
- 光标形状未转发：按钮上仍是普通箭头（你的“尝试解决”）。
- A2-5：X11 模式下，退出游戏的弹窗没有把光标移到默认按钮上，但光标响应和游玩正常。

**定位**
- 形状：宿主窗口是 Wayland 客户端，光标形状只能由宿主自己 `wl_pointer.set_cursor` 或
  `wp_cursor_shape_v1` 设置；但**游戏的光标图形在嵌套合成器里**，sway 目前不通过 IPC 暴露
  “当前光标图像/名称”。没有来源就无法转发。
- X11 那项：Wine 的 X11 驱动用 `XWarpPointer` 挪内部指针，不产生 `set_cursor_position_hint`，
  我们的 sway 补丁只记录后者，所以应用收不到位置 → 不会 warp。

**可选方案**
- **A（形状）** 给 sway 补丁把当前 cursor 的**名称**（wlr_cursor 里内置 cursor 的名字，
  或 XWayland 光标对应的标准名）暴露到 `get_seats`，宿主用 `wp_cursor_shape_v1` 映射标准形状
  （text/pointer/hand/…）；非标准形状只能不显示（或后续做软光标叠加）。
- **B（X11 warp）** 同一个补丁里，把 XWayland 的 `XWarpPointer` 路径也写进现有 hint 字段。
- **C** 只做 A，B 记录为已知限制（X11 模式本身就是回退方案）。

**实现与验收（2026-11-12）**
- 实现：sway 补丁暴露 `get_seats.cursor_shape`；Wayland 宿主用 `wp_cursor_shape_manager_v1`
  显示命名形状；X11 游戏改为轮询嵌套 XWayland 的 `XQueryPointer`，把 Wine 的 `XWarpPointer`
  镜像到宿主；宿主窗口固定为游戏窗口大小（min=max），只保留最小化/关闭。详见
  [cursor.md](cursor.md)。
- 探针结论：Wine 实际用 `wl_pointer.set_cursor` 的**图像光标**，`cursor_shape` 在所有位置都为空；
  “空=非箭头→显示手”的启发式会把整屏显示成手，不能用。命名形状只覆盖使用
  `wp_cursor_shape_v1` 的客户端。
- 验收：
  1. **X11 光标跳转：用户已验收通过。**
  2. **光标形状：没有实际验收场景**（Wine 走图像光标，v1.0 只做命名形状），按用户决定记为
     **通过 + 确认的已知问题**；图像光标（B 方案）列为 v1.0 后优化。
- 结论：**G5 关闭**。

---

## G6 · 验证缺口：中文输入法（C4）与 D4 说明（非 bug）

**C4**
- 你写“键盘输入正常，但不确定输入法是否正常，尤其是中文输入法”。
- 风险点：嵌套模式里，游戏是私有 sway 的客户端。Wayland 应用的输入法要走
  `zwp_text_input_v3` + input-method 协议；如果 sway/wlroots 侧没实现或不转发，**Wayland 游戏里中文
  输入法就是不可用的**（XWayland 游戏走 XIM，是另一条路径）。
- 需要确认：示例视觉小说 A / B里有没有“输入名字/存档名”的地方可以试；也可以先用 `alacritty` 在嵌套里开
  fcitx5 试打中文，区分“窗口不支持”还是“输入法没接进来”。

**D4**
- 手册里的“同时开两个 host”指的是：游戏自己开了两个顶层窗口（比如游戏窗口 + 设置窗口），
  这时应该出现两个 KDE 宿主窗口，`Alt+Shift+A` 只切换当前焦点那个。你写“不确定什么意思”，
  我们可以换一种描述，或者直接拿示例视觉小说 A 的“设置”窗口做例子。

**决定（2026-11-12）**
- G6 暂无实际应用/测试场景，C4（中文输入法）记录为已知项，v1.0 发布前不再测试。
- D4 的用户理解（多窗口时只切换当前焦点宿主）与实际设计一致，D4 记为通过。

---

## 建议讨论顺序

1. **G1**（会误关游戏，最高优先）
2. **G3**（Wayland 尺寸模型需要你拍板：重建窗口 vs 接受 letterbox）
3. **G2**（Wayland 不处理；X11 走 WM_CHANGE_STATE 镜像，不需再打 sway 补丁）
4. **G4**（过渡拉伸，A 方案可以先做）
5. **G6**（输入法：先确认能不能测）
6. **G5**（光标，最后打磨）

---

## 附录 A · 已决定不修 / 发布前再测

| 项 | 结论 |
|---|---|
| A4 旧模式回归 | 不兼容、不回归；仅作技术归档，只有新方案解决不了的问题才回头看 |
| 示例视觉小说 B Proton 无法隐藏 Wayland CSD | 上游/Proton 限制，v1.0 不解决 |
| 示例视觉小说 C （dwproton Wayland）启动器黑屏 | 非主要目标游戏，v1.0 不解决；属后续“对齐 KDE”专项 |
| F1–F4 性能/稳定性 | 你实际玩游戏时再评估；示例视觉小说 A / B静止不出帧，不能作为帧率指标 |

## 附录 B · 观察项（先记录，不定性）

- 探针里“销毁旧 blitter 后立刻在同一个 wl_surface 上建新 blitter、并把尺寸改成 1000×700
  （窗口仍是 800×600）”出现过 `ErrorDeviceLost`；正式程序反复切超分（同样是先销毁再新建）
  没有复现。可能是“新交换链尺寸 ≠ 窗口尺寸”让 KWin/驱动进入了异常组合，也可能是探针时序问题。
  G3 如果要走“换缓冲尺寸来让 KWin 跟随”的路线，第一件事就是复现并解释这个 DeviceLost。