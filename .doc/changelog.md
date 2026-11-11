# 更新记录

## v0.5.0（2026-11-10）· M2b：单输出嵌套、toplevel 抓取与原生 Wayland 宿主窗口

M2b 把嵌套合成器从“每个游戏窗口一个虚拟输出”改成“一个虚拟桌面 + 按 toplevel 抓取”，
并把 KDE 侧窗口换成原生 Wayland 窗口。本节随 M2b 收尾继续更新。

### 新增

- **单输出 + toplevel 抓取**：私有 sway 只保留一个 headless 输出，抓帧改用
  `ext-image-copy-capture` 按 toplevel 取客户端缓冲（`GEMPRISM_TOPLEVEL_CAPTURE=1` 开发开关）；
  窗口移动/缩放不再需要热插拔虚拟输出。旧的多输出池保留为多窗口模式。
- **原生 Wayland 宿主窗口**：`IHostWindow` 抽象 + `WaylandHostWindow`（`xdg_toplevel`、
  `zxdg_decoration_manager_v1`、`wp_viewporter` + `wp_fractional_scale_manager_v1`、`wp_pointer_warp_v1`）。
  `GEMPRISM_HOST_X11=1` 可回到 X11/EWMH 宿主窗口；Wayland 不可用时自动回退。
- **光标跳转**：游戏在嵌套桌面里请求移动光标时，应用把位置换算成宿主窗口坐标并调用
  `wp_pointer_warp_v1`，KWin 的可见光标跟着跳。位置来源是 sway 补丁暴露的
  `cursor_hint_*` IPC（`tools/patches/sway-1.12-cursor-hint-ipc.patch` +
  `tools/build-nested-sway.sh`）。详见 [pointer-warp.md](pointer-warp.md)。
- **宿主尺寸模型**：宿主窗口按 `logical × output scale` 分配缓冲，`wp_viewport` 映射回逻辑尺寸，
  超分输出正好等于显示器物理分辨率（1.5× 屏上全屏 = 3840×2160，不是 5120×2880）。
  相关换算有单测（`HostSurfaceGeometryTests`）。
- **窗口化宿主尺寸**：toplevel 模式下宿主尺寸取游戏窗口实际矩形（不再用 Wine 报告的
  设计分辨率），退出超分后回到 1286×752 而不是 806×632。

### 修复

- **Wine 游戏窗口黑屏**：虚拟输出刷新率为 0 时，wlroots 把 `CAPTURE-N` 当成 frame-pacing
  输出，而它只有 commit、没有 present 反馈，`vkWaitForPresentKHR` 永久阻塞。现在会话开始时
  从 XRandR 快照显示器刷新率（取不到时退 60Hz）并写入 sway 输出模式。
- **点击位置错误**（Wayland 宿主引入）：`wl_pointer.button` 事件没有位置，修复前被当成
  (0,0) 映射到窗口左上角，表现为“有悬停、点击无效”。现在按钮/滚轮事件带上最后一次
  motion 的缓冲像素坐标。
- **超分切换后窗口尺寸错误**：全屏 configure 的逻辑尺寸 × scale 现在直接得到物理分辨率，
  不再出现 4K 显示器上退出超分后窗口变成 4K 大小、带黑边。
- **游戏改窗口大小时重建宿主窗口**：xdg_toplevel 没有客户端主动 resize 的请求，`set_min_size`/
  `set_max_size` 实测不会改变已有窗口尺寸。现在 toplevel 模式会跟踪游戏窗口 rect，尺寸稳定
  （350ms 防抖）后销毁旧宿主、按新尺寸重建一个，对话框的 `set_parent`/焦点/标题都会带上；
  X11 宿主仍原地 resize。重建后会把上一帧重新呈现一次，新窗口立刻映射、不会黑屏。
  （顺带修掉两处：抓帧把“最后一次成功转换的帧尺寸”和当前 buffer 约束混用导致的
  `frame buffer too small`；窗口关闭后 `Stopped`/`BufferConstraints` 的日志刷屏。）
- **关闭宿主窗口改为转发给游戏窗口**：以前点 KDE 标题栏的关闭按钮会结束整个会话（`--nested`
  模式还会杀掉游戏进程树），于是“关掉退出确认弹窗”变成了“关掉游戏”。现在把关闭请求转发给嵌套
  合成器里对应的那个窗口（sway `kill` → `xdg_toplevel.close` / `WM_DELETE_WINDOW`），和 KDE 直连
  时的语义一致：弹窗关掉、主窗口恢复响应；游戏不理会请求时窗口也保持原样。宿主镜像在对应
  窗口消失后立刻移除，不再等 1 秒宽限期。
- **关闭对话框时 SIGSEGV（NVIDIA Wayland WSI）**：每个宿主窗口原本各自创建并销毁
  `VkInstance`；关闭对话框窗口销毁 instance 时，驱动里另一窗口仍在用的状态被一并拆掉，
  主窗口下一次 present 跳空指针崩溃（`ip=0`，栈在 `libnvidia-glcore`）。现在按平台扩展集
  共享一个进程级 `VkInstance`（`VulkanInstanceCache`），窗口仍各自拥有 device/swapchain/
  surface。分析与复现见 `.agent/M2b-修复-SIGSEGV-关闭对话框.md`。
- **移除每秒强制绝对定位**：那是“光标不会跳”时期的兜底，会让嵌套指针每秒被拉回宿主演算
  位置、与 sway 刚回报的 warp 参考点互相拉扯。现在只保留 sway hint 触发的参考点重设，
  以及点击/进入时的一次绝对定位。归档见 `.agent/M2b-归档-指针周期绝对刷新.md`。
- **X11 游戏按 Esc 最小化时宿主“黑闪一下”**：Wine 按 ICCCM 发 `WM_CHANGE_STATE(IconicState)`
  请求最小化，KWin 直连时会照办，嵌套的 sway 却忽略该请求；客户端在隐藏自己之前会画一帧全黑，
  宿主把那一帧呈现出来就是你看到的“黑一下”。现在探针在嵌套 XWayland 的 root 上监听这个
  ClientMessage（`SubstructureNotifyMask`，不能选 `SubstructureRedirectMask`），收到后 600ms 内
  丢弃整帧全黑的候选帧，宿主保持上一帧、不闪；游戏本身照常运行，不受影响。
  没有把“宿主跟着最小化”做下去：Wine 在窗口被客户端 map 之后 ~25ms 会再发一次最小化请求
  （即使完全照搬 KDE 直连实测到的 KWin 顺序：`_NET_WM_STATE_HIDDEN` → map → `WM_STATE=Normal`
  也一样），宿主会在用户点开后被立刻弹回任务栏，比保持显示更糟。测试与对照记录见
  `.agent/M2b-归档-游戏自我最小化.md`；以后要做“宿主跟随最小化”的 sway/wlroots
  修复方案与成本评估见 [minimize.md](minimize.md)。
- **超分/还原切换的第一帧被拉伸（G4 A）**：进入超分时先把目标 4K 交换链建好，并把当前帧
  预呈现进去，再发 `set_fullscreen`；退出时同理先把窗口尺寸的交换链建好再 `unset_fullscreen`。
  configure 到达时合成器已经持有正确尺寸的帧，不再把旧的窗口缓冲拉伸 ~300ms（“第一帧 4:3
  拉伸铺满、过一会儿才归位”）。Wayland 宿主可以给 `wl_surface` 挂任意尺寸的 buffer，
  `wp_viewport` 负责映射到当前窗口；X11 宿主受窗口 extent 限制，仍走 configure 后重建。
  游戏自己改变窗口尺寸的路径不做特殊处理（保持原逻辑，记为已知的可能问题）。
- **光标形状 / X11 光标跳转 / 宿主窗口按钮（G5 A）**：sway 补丁把
  `wp_cursor_shape_v1` 的形状名暴露到 `get_seats.cursor_shape`，Wayland 宿主用
  `wp_cursor_shape_manager_v1` 显示 KDE 原生光标；X11 游戏的光标跳转改为轮询嵌套
  XWayland 的 `XQueryPointer`（Wine 的 `XWarpPointer` 不产生 locked-pointer hint）；
  宿主窗口窗口化时固定为游戏窗口大小（min=max），KWin 隐藏最大化按钮并禁止拖拽缩放，
  只保留最小化和关闭。详见 [cursor.md](cursor.md)。

### 验证

- `dotnet build`：0 warning / 0 error；`dotnet test`：146/146 通过（新增 21 个坐标换算测试）。
- 独立探针（未提交）：warp 请求 `buffer=450,300` 后下一次 motion 正好回报 `450,300`；
  截图测得窗口内容在屏幕上为 1:1 物理像素。
- zenity 冒烟：窗口 344×270 → 全屏 `logical=2560x1440 buffer=3840x2160` → 还原 344×270。
- G4 A 冒烟（zenity 300×226）：`mode prepared 3840x2160` → `mode=upscale`；再
  `mode prepared 300x226` → `reconfigured logical=200x151 buffer=300x227 fullscreen=False`
  → `mode=original`；两次都没有 swapchain 错误，全屏/还原后的截图正常，无拉伸帧。
- G5 冒烟（zenity）：`get_seats.cursor_shape` 返回 `default`；Wine 探针悬停按钮时变成空串
  （图像光标）；KWin 报 `resizeable=false / maximizable=false`，标题栏只剩最小化/关闭，
  拖拽和最大化均不可用。
- 实机游戏（秽翼的尤斯蒂娅，Proton/Wine Wayland）：界面显示、悬停、点击、光标跳转、
  超分进出全部正常；一次退出确认框记录 25 次 warp。
- `GEMPRISM_HOST_X11=1` 冒烟：旧 X11 宿主窗口仍可创建并呈现。

### 已知限制

- **Wayland 窗口位置**：xdg-shell 不提供定位请求，窗口化位置由 KWin 决定；只有全屏能指定输出。
- **最小化不可逆**：`xdg_toplevel.set_minimized()` 没有取消请求，游戏自我最小化后无法从客户端
  恢复宿主，需要用户从任务栏恢复（反方向正常）。
- **无焦点窃取**：Wayland 下 `Focus()`/`Activate()`/`Raise()` 为空操作。
- **显示器热插拔**：运行中的虚拟桌面不会跟随分辨率/刷新率变化（v1.0 规则）；新会话才重新快照。
- **游戏内全屏（腐姬）**：X11 全屏黑屏（游戏画在 override-redirect 窗口上，toplevel 抓取看不到）；
  Wayland 全屏画面居左、点击错位（KDE 直连同样异常）。v1.0 不解决，根因与备选方案见
  [`fullscreen.md`](fullscreen.md)。
- **图像光标**：Wine 用 `wl_pointer.set_cursor` 的图像，不实现 `wp_cursor_shape_v1` 的命名形状；
  v1.0 只转发命名形状，游戏自定义光标（含“手”）仍显示默认箭头。图像转发方案见
  [`cursor.md`](cursor.md)，列为 v1.0 后优化。

## v0.4.0（2026-11-07）· M2a：Wayland layer-shell、XWayland 混合与 portal 捕获

M2a 把呈现从 X11 override-redirect 扩展到 Wayland layer-shell，并加入原生 Wayland portal 捕获。按约定，M2a 只做主要路径冒烟；完整回归/边界兼容性留到 v1.0。

### 新增

- **Wayland 呈现**：`zwlr_layer_shell_v1` overlay + Vulkan `VK_KHR_wayland_surface`；`wp_viewporter` + `wp_fractional_scale_manager_v1` 分数缩放；每输出 layer surface。
- **XWayland 混合**：X11 捕获（XShm + XDamage + 脏矩形）+ Wayland overlay + `wl_pointer` → `XSendEvent` 输入转发；保留 `--window` 体验。
- **原生 Wayland 捕获**：`--portal-window` / `--portal-monitor`；xdg-desktop-portal ScreenCast v6 + PipeWire（GStreamer `pipewiresrc` 桥）；`restore_token` 轮换保存到 `portal.token`；GUI 会话页新增“Wayland 窗口…”入口。
- **多屏/缩放**：`wl_output` + `zxdg_output_v1` 输出枚举；旋转/100%/150% scale；当前输出消失时回退另一块屏。
- **CLI/config**：`--backend auto|x11|wayland`、`--input auto|passthrough|forward`、`--capture auto|x11|portal`；`[capture]`、`[input] pointer_mode`。
- **文档**：新增 `.doc/m2-plan.md`（M2a/M2b/M2c 详细计划）、`.doc/cli.md`；更新 roadmap/design/compatibility/backlog/README。
- **测试**：新增 Wayland 几何/旋转 scale/4:3/配置测试；修复后总计 103/103。

### 0.4.0 修复（2026-11-08）

- **修复 layer-shell 恢复崩溃与层级丢失**：`attach(null)` 会让 KWin 发出 `zwlr_layer_surface_v1.closed` 并把 layer 重置为 Bottom；再 attach 时既可能报 `buffer attached prior to first configure` / `SurfaceLostKhr`，也会让恢复后的 overlay 落在全屏游戏下面。现在隐藏不再 unmap，而是保留 surface、清空 input region，并把它移到**整个逻辑桌面右侧之外**（不是当前输出左侧，否则会落到左边的 DP 上）；显示时重新锚定整屏并重新声明 Overlay layer。弹窗/焦点恢复、原生 portal 收起/显示均正常，层级保持最上层。
- **portal 选择器默认每次弹出**：不再默认复用 `restore_token` 跳过选择器；新增 `--portal-reuse-token` 显式复用上次窗口。
- **接入 portal GlobalShortcuts**：原生 Wayland 焦点下 `Alt+Shift+A` 也可用；通过 `org.freedesktop.host.portal.Registry` 注册 app id，并自动写入 `~/.local/share/applications/org.gemprism.GemPrismUpscaler.desktop`。KDE 的 trigger 使用 `ALT+SHIFT+A`（大写）。
- **portal 托盘/快捷键手动收起**：原生会话支持托盘“隐藏/显示覆盖层”和 portal 快捷键；隐藏后恢复也会走新的 configure 流程。
- **测试**：新增 XDG trigger 格式测试；总计 103/103。

### 已知限制

- **原生 Wayland 跟随焦点未实现**：portal 不提供源窗口身份/焦点，无法可靠判断“用户切到了别的程序”。M2a 用 `Alt+Shift+A` / 托盘手动收起；自动跟随属于 M2b 嵌套合成器。
- **KDE 快捷键可能存成 `none`**：首次授权窗口若没有把快捷键“应用”为当前值，`kglobalshortcutsrc` 会写成 `toggle-overlay=none,Alt+Shift+A`；此时热键不生效，需在“系统设置 → 快捷键 → GemPrismUpscaler”手动分配。
- **KDE 窗口流按输出 scale 合成**：720p 全屏游戏在 4K/150% 输出上 `pipewire caps` 为 3840×2160；2× 会产生 8K 中间纹理（实测 VL 10–12fps、VRAM 7.6GB、GPU 100%）且没有真实超分收益。M2a 在源尺寸 >= 输出尺寸时自动降级 bilinear；低分辨率原生源需 M2b 嵌套合成器或改用 XWayland 窗口捕获。
- **窗口化原生捕获含标题栏/阴影**：KDE 窗口流为 960×582（150% + 装饰）；M2a 正式验收以全屏/无边框源为准，窗口化原生输入由 M2b 嵌套合成器解决。
- **XWayland 捕获撕裂**仍用 XShm；最终方案在 M2c。
- **wlroots 真实桌面**未验证；M2 只做协议级。
- `--window` + `--capture portal` 未接；原生窗口用 `--portal-window`。

## v0.3.0（2026-11-06）· M1c：设置、托盘、Profile、自定义 effect 与 VL

M1c 把 M1a/M1b 的能力收拢成可日常使用的配置界面，并补齐 Anime4K VL。按约定，M1c 不做动画、不做完整回归，边界兼容性留到 v1.0 统一验收。

### 新增

- **配置/设置**：`~/.config/GemPrismUpscaler/config.toml`，`GEMPRISM_CONFIG` 可覆盖；TOML 原子写入、损坏备份回退、schema=1。设置页覆盖热键、日志路径、第二实例行为、默认显示器/效果/缩放、跟随焦点、窗口列表自动刷新、托盘开关/关闭行为、开发模式。
- **主窗口 Tab**：左侧 5 个 Tab：会话 / 设置 / Profile / 效果 / 日志。
- **托盘**：Avalonia `TrayIcon`（StatusNotifierItem/AppIndicator）；显示设置、开始/停止、显示隐藏覆盖层、退出。默认启用、关闭默认退出；不可用时自动禁用并强制直接退出。KDE Wayland 下“显示设置窗口”增加 KWin 脚本兜底激活。
- **Profile**：`[[profile]]` 支持 WM_CLASS、`/proc/<pid>/exe`、Wine cmdline `.exe` 通配匹配；按文件顺序先匹配先赢；可覆盖 effect/缩放/显示器/跟随焦点/脏矩形/热键。`remember_last` 记住上次成功使用的效果/缩放/显示器。
- **自定义 effect**：TOML 描述 compute pass 图 + 外部预编译 SPIR-V；运行时不需要 glslang。ABI v1、纹理上限 19、单 pass 输入 16、push constant 256B；详见 `.doc/effects.md`，样例 `examples/effects/sharpen-2x/`。
- **Anime4K VL**：新增 `anime4k-vl`（18 pass / 19 纹理 / 176B push constant），默认不启用；设备 `maxPushConstantsSize` 不足时不启用。验收只要求画面正确，帧率不作为门槛。
- **错误/诊断**：分级日志、`ErrorBus`、功能级/非功能级错误区分；开发模式输出详细日志与界面提示；Vulkan 校验层按 Debug/Release/开发模式/`GEMPRISM_VALIDATION=1` 门控。
- **窗口列表自动刷新**：可配置间隔 250–5000ms；默认关闭。
- **热键可配置**：支持 Ctrl/Alt/Shift/Super + 主键，处理 NumLock/CapsLock 组合；冲突时提示并回退。
- **第二实例**：默认 `notify`（只提示，不影响已有实例）；可配置 `activate`，通过 Unix socket 让已有 GUI 到前台；第二个 CLI 仍报错。

### 修复

- **日志 Tab 字体崩溃**：`AppendLog` 在日志页未显示时不再设置 `CaretIndex`，避免 `Could not create glyphTypeface` 导致 SIGABRT；增加 UI 线程未处理异常兜底。
- **X11 清理/焦点竞态**：focus 前检查源窗口仍 viewable；`X11Overlay` 幂等销毁；X error 进入诊断日志。
- **Vulkan 校验层**：Release 默认关闭，Debug/开发模式/环境变量开启；避免日常运行的性能与日志开销。
- **配置恢复**：TOML 语法错误、无效值、未知键都视为坏配置；原文件改名为 `config.toml.bad-*`，写回修正后的配置（值坏改该值，语法错误整体回默认），避免每次启动重复生成 `.bad`。
- **Profile 重新加载**：Profile Tab 的“重新加载”现在会重新读取 `config.toml`，而不是只刷新内存中的列表。
- **呈现顺序**：X11/XWayland overlay 固定使用 `VK_PRESENT_MODE_FIFO_KHR`。FIFO 本身未能消除旧帧闪回/撕裂，根因已定位为 GPU 窗口捕获的半帧问题（M1c 已知限制，M2 处理）。
- **焦点/显示**：焦点丢失增加 300ms 去抖，避免瞬时焦点抖动导致 overlay 反复隐藏/显示；`X11Overlay.Show()` 先把窗口背景清黑再映射，避免重新显示时露出上一帧。焦点丢失日志会记录当前 X11 focus 窗口 id。
- **测试工具**：`tools/testpattern` 设置 `WM_CLASS=testpattern`，便于用 `class = ["testpattern"]` 验证 Profile。

### 验证

- `dotnet build -c Release`：0 warning / 0 error（NuGet 审计缓存只读的环境警告除外）；`dotnet test -c Release`：91/91。
- 自定义 effect `sharpen-2x` 在 1280×720 测试图案 + 4K 输出上成功启动并呈现，无 Vulkan 校验错误。
- Anime4K VL 在 1280×720 测试图案 + 4K 输出上成功启动并呈现，无 Vulkan 校验错误；首帧 effect CPU 约 466ms（含 18 个 pipeline 创建），VRAM 约 880 MiB（整卡）。
- 托盘在 KDE Wayland 下创建成功；SNI `Activate` 经 KWin 兜底把窗口带到前台。
- 按约定未做 M1b 完整回归；静态/脏矩形/4K 等边界验收留 v1.0。

### 注意事项

- KDE Wayland + 4K/150% 下 ComboBox 下拉列表不跟随窗口移动（DP 100% 正常）；Avalonia/Wayland popup 限制，M1 不处理，记录在 `.doc/backlog.md` §7。
- 自定义 effect 只支持 compute pass + 内置 Lanczos-2 收尾，不支持自定义 graphics 最终 pass。
- M1c 不做动画；VL 帧率不作为验收门槛；完整回归/边界兼容性留 v1.0。

## v0.2.1（2026-11-05）· M1b：指标、Anime4K S/M/L、脏矩形与零拷贝 spike

M1b 把 effect 管线从 5-pass 硬编码改成 manifest 驱动的 N-pass，加入 S/M/L、分块脏矩形、持久合成图和每秒指标；默认仍是 S + Lanczos-2，双线性回退保留。

### 新增

- **Anime4K S/M/L**：`tools/build_shaders.py` 从同一份 mpv user-shader 源展开 S=5、M=9、L=10 pass，生成 `anime4k_{s,m,l}_pN` SPIR-V 与 `anime4k_manifest.json`；`Anime4KChain` 按 manifest 分配纹理/描述符/pipeline；L 的并行分支和 M 的 1×1 合并 pass 都能跑。默认 S；UI 三项 + 双线性；CLI `anime4k-s|anime4k-m|anime4k-l`，`anime4k` 仍为 S 别名。
- **指标**：`FrameStats` 增加 capture/diff/upload/effect/present 平均/p95；`VulkanBlitter` 用 Vulkan timestamp query 测 upload/effect/tail；`NvmlMetrics` 读整卡 GPU 利用率和显存；进程 CPU；每秒 `stats` 行；可选 `GEMPRISM_STATS=/path/stats.csv`。
- **脏矩形**：64px 分块 64-bit 哈希；只上传变化块（staging 使用全宽 `BufferRowLength`）；按 manifest halo 扩展后只重算变化区域；Lanczos 写入持久 4K 合成图，再整图 copy 到 swapchain；≥50% 变化回退整帧；`--no-dirty-rect` 可关闭。主路径直接从 shm 指针哈希/上传，去掉整帧托管拷贝。
- **测试工具**：`tools/testpattern.c`（static/dirty/animate/repaint/popup）、`tools/bench-m1b.sh`（场景/截图/CSV）、`.agent/M1b验收记录.md`。
- **零拷贝 spike**：`tools/zero-copy/` 的 DRI3 导出与 Vulkan 导入探针；GPU 窗口（vkcube）线性 1-fd 导入成功，软件窗口 2-fd/DCC 需回退；结论“有条件 go”，不进默认链路；详见 `.doc/zero-copy-spike.md`。
- **版本**：0.2.1；`--version`、日志、文档同步。

### 修复

- **持久合成图 usage**：`VulkanBlitter` 创建 composed image 时补 `VK_IMAGE_USAGE_TRANSFER_DST_BIT`；全帧 clear 不再触发 `VUID-VkImageMemoryBarrier-oldLayout-01213` / `VUID-vkCmdClearColorImage-image-00002`（修复前每轮 3 条，修复后 0 条；脏块/弹窗恢复 AE 仍为 0）。

### 验证

- `dotnet build -c Release`：0 warning / 0 error；`dotnet test -c Release`：53/53。
- 1280×720 静态：`captured=0 presented=0 gpu=n/a`，整卡 0%；每帧重绘同像素：`captured≈60 presented=0`。
- 小脏块 one-shot：脏矩形与 `--no-dirty-rect` 最终截图 `AE=0`（逐像素一致）；文本/1px 网格无块缝。
- 小脏块动画：`diff≈1.05 ms`、`gpuEffect≈0.02–0.17 ms`、`gpuTail≈0.1–0.3 ms`，47.9 fps（软件 testpattern/开发机数据；真实 4K 游戏见下条）。
- 全屏动画（软件 testpattern）：脏矩形开/关均 47.9 fps；GPU effect 约 2.9 ms（1920×1200），4K override 下 S/M/L 约 0.65/1.3–1.5/2.1–2.3 ms，显存 555/585/593 MiB。
- 双线性回归、弹窗让位/恢复、单实例、退出路径保持正常。
- **真实 4K/60**：外接 HDMI-A-1 3840×2160@144（HDR、150%）+ 终末地 1280×720@60 + Anime4K L：`fps=59.5–60.7`、`presented=60/s`、`capture=3.3–4.6 ms`、`gpuEffect=1.1–2.0 ms`；游戏与 overlay 两个 MangoHud 均 60。

### 注意事项

- 软件 testpattern 的 4K capture 约 23 ms，4K 下只有 37–38 fps；真实游戏（Vulkan、同屏）capture 3.3–4.6 ms，4K L 59.5–60.7 fps。不要把 testpattern 的 fps 当真实游戏上限。
- 4K 源（2×→8K）未测；脏矩形哈希 720p 约 1 ms/帧，4K 源约 4 倍。
- 零拷贝只验证导出+导入，未做 fence 同步/端到端画面，不进入默认链路。
- `present` 计时包含 `QueuePresent` 的 vblank/队列等待。
- 真实 4K@144 外接屏已复测（Endfield 4K L 59.5–60.7 fps）；4K 源（2×→8K）仍未测。

## v0.2.0（2026-11-04）· M1a：单实例、退出审计与多显示器基础

M1a 不改变 effect 管线，仍使用 Anime4K S + Lanczos-2 和双线性回退；重点是把“能退出、能选屏、不会跨屏”做扎实。

### 新增

- **单实例**：`$XDG_RUNTIME_DIR` 下的 Unix socket；第二个 GUI 实例弹出“已在运行”错误提示并退出，不激活已有窗口；第二个 CLI 返回非 0；新增 `--status` 查询 PID/模式/会话/显示器。
- **退出审计**：窗口关闭、退出按钮、`SIGTERM`/`SIGINT`、游戏窗口关闭、引擎异常都会记录日志并销毁 overlay；引擎停止最多等待 3 秒。
- **多显示器基础**：XRandR 1.5 枚举物理显示器矩形；overlay/swapchain 只覆盖选中屏；支持“自动跟随游戏窗口”和 `--monitor <name>` 手动选屏；窗口跨屏移动时自动重建；选屏失效时回退自动。
- **输入坐标修正**：`XQueryPointer` 的 root 坐标在同步指针时减去显示器原点，修复多屏下悬停位置偏移。
- **窗口选择**：过滤面板/桌面窗口；列表显示 PID、类名、分辨率、窗口 ID 和所在显示器；刷新后保持原选择；单实例状态接入 UI。
- **文档与许可**：README 改为支持矩阵/当前验证环境/系统要求；新增 `compatibility.md`、`backlog.md`；roadmap 重排为 M1a/M1b/M1c；删除时间盒表述；许可证改为 **AGPL-3.0-or-later**，新增 `AUTHORS`。

### 验证

- `dotnet build -c Release`：0 warning / 0 error。
- `dotnet test -c Release`：32/32（新增显示器选择、竖屏布局、单实例状态）。
- 1280×720 → DP-1（1800×2880）：overlay 1800×2880+0+0，内容 1800×1012+0+934，合成测试约 46–50 fps。
- 1280×720 → HDMI（3840×2160）：overlay 3840×2160+1800+489，内容 3840×2160，合成测试约 41–44 fps。
- 自动模式窗口跨 DP-1/HDMI 移动，overlay 在约 0.3–0.7 秒内重建；手动 `--monitor` 固定不跟随。
- 第二 CLI 返回 4；第二 GUI 显示错误提示；`SIGTERM` 后无残留进程/overlay。

### 注意事项

- M1a 的第二实例**不会**激活已有窗口；该行为作为 M1c 设置项。
- 多显示器只做基础版：热插拔、每屏 DPI、负坐标、跨屏拼接的完整处理在 M2。
- 没有托盘和完整设置界面；见 `.doc/backlog.md`。
- 版本号升到 0.2.0；已在验收通过后打本地 `v0.2.0` tag，push 由用户手动执行。

## v0.1.2（2026-11-02）· M0b：Anime4K 2× + Lanczos

**M0 完成**：M0a（捕获/呈现/输入/弹窗/生命周期）与 M0b（Anime4K 2× + Lanczos-2）均通过用户真实游戏验收。混合 GL+Vulkan 管线按降级方案不做，效果全部在 Vulkan 中执行；M2 再评估。

M0b 把 M0a 的“双线性放大”升级为真正的超分管线，并保留双线性作为对比/回退。

### 新增

- **Anime4K 2× CNN（S）**：`Upscale_CNN_x2_S` 展开为 5 个 Vulkan compute pass（4 层 3×3 CNN + Depth-to-Space 残差），中间纹理 `rgba16f`。
- **Lanczos-2 收尾**：4×4 taps 从 2× 结果重采样到最终内容矩形；graphics pass 直接写入 swapchain，render pass 清黑边。
- **算法切换**：UI“超分算法”下拉框、CLI `--effect anime4k|bilinear`；默认 Anime4K。
- **版本号**：`Directory.Build.props` 统一 `0.1.2`；UI 标题、`--version`、日志都显示 v0.1.2。
- **离线着色器构建**：`tools/build_shaders.py` 解析 mpv user-shader 指令、生成 GLSL、`glslangValidator` 编译、`spirv-val` 校验；SPIR-V 嵌入程序，运行时不依赖 glslang。

### 技术方案

- `Anime4KChain`：5 个 compute pipeline、每 pass 独立 descriptor set、48 字节 push constants（输出尺寸 + 5 张纹理尺寸）；pass 间用 image barrier 做 `ShaderWrite → ShaderRead`。
- `LanczosPresenter`：全屏三角形 + Lanczos-2 fragment shader；通过 viewport/scissor 把绘制限制在内容矩形，render pass 的 clear 负责黑边；swapchain 从 `PresentSrcKHR`/`Undefined` 转入 `ColorAttachmentOptimal`。
- 源图仍由 XShm → staging → `B8G8R8A8_UNORM` 上传；效果链读取源图，输出 2× 图像；最终 pass 直接写 swapchain，避免额外的 4K 中间纹理和 float→unorm blit 的格式兼容问题。
- 保留 M0a 的 `vkCmdBlitImage` 路径；`EffectMode.Bilinear` 可在运行时由 CLI/UI 选择。

### 验证

- `dotnet build -c Release`：0 warning / 0 error；`dotnet test -c Release`：23/23（新增 8 个：版本号 1 个 + 7 个着色器资源）。
- 4:3 fit（1024×768 → 2880×2160）：左右黑边像素均值 0，内容居中。
- 4:3 stretch：铺满 3840×2160，无黑边。
- 画质测试卡（1280×720 → 4K，8-bit PNG 截图）：梯度均值 Anime4K 3.43 vs 双线性 2.71（+26%）；Laplacian 均值 3.49 vs 1.79（+96%）。
- 1280×720 动态画面：42–51 fps；当前 HDR/144Hz 桌面合成器上限约 48–50 fps，基线同环境 49–51 fps。GPU 占用 Anime4K 约 35–46%，显存 515 MiB。
- 静止画面仍然 0 capture / 0 present。

### 注意事项

- 本版本只内置 Anime4K **S**；M/L 变体未接入。FSR1、`Clamp_Highlights`、混合 GL+Vulkan 管线不在 M0b 范围。
- 画质指标是合成测试卡上的客观锐度对比，真实游戏的观感仍需用户复测；Anime4K 对 2D/动漫风格效果最好。
- 帧率目前受本机 HDR/144Hz 合成器上限影响，不能据此判断 60Hz 下的上限；GPU 仍有约 60% 余量。
- 4K 输出下效果链显存约 515 MiB（RTX 4060 Laptop 8 GB 足够）；后续 4K 源/多显示器需要重新评估。
- HDR 仍是 SDR 直通，未做色彩管理。

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
