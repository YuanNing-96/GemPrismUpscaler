> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M2b v1.0 已知问题（不修复）

- 记录时间：2026-11-09 21:15（Asia/Shanghai）
- 适用版本：GemPrismUpscaler v1.0
- 原则：下列问题在 v1.0 **不修复**，只记录现象、已定位的根因、规避方法和不修复原因。修复成本高、需要改 Wine、或属于上游/第三方限制的，均在发布说明中作为已知问题列出。

## 1. LAUNCH-WAYLAND：示例视觉小说 C 启动器在“嵌套 Wayland + dwproton”下黑屏/纯白

**现象**

- 示例视觉小说 C （`lutris:rungameid/<id>`，runner `dwproton`，Graphics=wayland）启动后，启动器窗口尺寸正常（1312×754），但内容区只有背景色（深灰 `#141414`；设 `WAYLANDDRV_OFFSCREEN=1` 时纯白），没有启动器 UI。
- 直连 Lutris+Proton（KWin Wayland）正常：先白屏、窗口闪烁几次后出现画面。

**影响**

- 仅影响“嵌套 Wayland + dwproton”的组合；改用 X11 后启动器和游戏均正常。

**规避**

- 该游戏使用 **X11** 后端：Lutris `Graphics=x11`，去掉 `PROTON_ENABLE_WAYLAND`。

**根因（已定位到 Wine 侧）**

- 嵌套合成器默认选错 GPU、NVIDIA 下 screencopy 返回 `Bgr888` 这两个问题已经修复（见验收清单），但仍黑。
- `WAYLAND_DEBUG` 显示：dwproton 的 `winewayland` 把 WebEngine 客户区 surface（1408×768）挂成 subsurface 后约 100ms 调用 `wl_subsurface.destroy()`，之后不再 `get_subsurface`；顶层窗口只剩背景色。所有后续内容都提交到没有角色的 surface，合成器忽略。
- 直连 KWin 下同一 Wine 也会 destroy，但约 0.4 秒后**重新创建** subsurface，画面出现；nested 下不会重建。
- GE-Proton11-9 在 nested 下正常，但它不支持 ACE，示例视觉小说 C 只能用 dwproton。

**已排除的 compositor 侧因素**

- 焦点/激活：`swaymsg focus`、EWMH `_NET_ACTIVE_WINDOW`、把抢焦点的 explorer 窗口移出、强制 leave/enter，均无效；
- 动态 output：每窗口独立 output 的创建/移动/mode 变化（1920×1080→3840×2160）；用临时“单静态 output”模式复测，仍黑；
- 采集链路：直接抓嵌套输出确认合成器底色为纯黑、启动器窗口区域为均匀背景色，采集正常。

**已尝试但失败的方案**

- 复制 dwproton runner，仅替换 GE-Proton11-9 的 `winewayland.so` / `winewayland.drv`：启动报 `Application tried to create a window, but no driver could be loaded.`——两家 Wine 11.0 的内部 ABI/补丁不兼容；
- 整体换成 GE 的 Wine 树：可解决画面，但丢失 dwproton 的 ACE 支持，游戏无法启动。

**不修复原因**

- 触发点在 Wine 自身的 Win32 客户区可见性/生命周期：Wine 主动 destroy 后，只有它自己的 show/可见性事件会触发重建，compositor 无法命令它重建。
- 要彻底修复需要 dwproton 的源码/构建配方，把 GE/上游 Wine 的 wayland 修复 backport 回去并重建 Wine（工作量大，且需重新验证 ACE），超出 v1.0 范围。

**版本说明**

- `GE-Proton11-9` 的 Wine 实际是 `wine-11.0 (Staging)`，不是 11.9；“11-9”是 Proton 构建号。
- `dwproton-11.0-16` 是 `wine-cachyos 11.0`。两者同为 Wine 11.0，差异在补丁集。

## 2. W-TITLE：旧 Wine 的 Wayland 窗口仍带 Wine 自绘 CSD 标题栏

**现象**

- 部分 Wine Wayland 窗口（示例视觉小说 A、示例视觉小说 B 等）在 Wayland 模式下顶部有蓝色 Windows 风格标题栏；KWin 直连时由 KDE 画标题栏，没有这个问题。

**影响**

- 窗口内容被标题栏占去一部分，观感异常，但不阻塞操作；不影响 X11 后端。

**规避**

- 升级到支持 xdg-decoration 的 runner（GE-Proton10-34 / 11.x 等），或该游戏使用 X11 后端。

**根因与现状**

- Wine 只有收到合成器的 `xdg-decoration v2` 协商结果才不画 CSD；sway/wlroots 默认只 advertise v1。
- GemPrism 已随包 `xdg-decoration-v2-shim.so`（`tools/xdg-decoration-v2-shim.c`），启动嵌套 sway 时自动 `LD_PRELOAD`，把该 global 版本从 1 提到 2。
- 但 **Wine 自身必须实现 xdg-decoration** 才能利用它：GE-Proton10-17 及以下（以及 dwproton 的旧版本）的 `winewayland.so` 没有这部分代码，仍会自绘 CSD。实测可用/不可用版本见验收清单的“W-TITLE 的 Wine 版本要求”表。
- 锁定在旧 Wine 的游戏（例如示例视觉小说 B 只能用 GE-Proton10-14）无法在 Wayland 下去掉标题栏。

**不修复原因**

- 这是 Wine 驱动的能力缺失，不是 GemPrism 或合成器能补救的；需要换 Wine 或给旧 Wine 打补丁。

## 3. FULL-X11 / FULL-WAYLAND：全屏界面异常

**现象**

- 游戏内切全屏后，可能出现 800×600 内容停在左上角、黑边、CSD 残留、宿主尺寸不对等界面问题。

**影响**

- 不阻塞玩家操作；默认窗口模式下超分正常。

**规避**

- 使用窗口模式（v1.0 默认路径）。

**不修复原因**

- v1.0 明确不承诺全屏兼容，只做 best-effort；详见 [`m2b-fullscreen-policy.md`](m2b-fullscreen-policy.md)。

## 4. 混合显卡环境的使用注意（非缺陷）

- 嵌套合成器现在会自动选择**非 boot VGA 的独显** render node，避免“客户端在 NVIDIA 分配 dmabuf、合成器在 AMD 导入”的跨 GPU 失败；该选择会随机器不同而变化。
- 如果游戏实际跑在 iGPU（或选错 GPU），可用 `GEMPRISM_WLR_RENDER_DRM_DEVICE=/dev/dri/renderDXXX` 覆盖；合成器启动失败时会自动回退默认 GPU。
## 7. Wine Wayland + 自绘 CSD 的窗口客户区不显示（示例视觉小说 B / GE-Proton10-14，2026-11-10）

**现象**

- 示例视觉小说 B 在 Wayland 模式下（GE-Proton10-14，`PROTON_ENABLE_WAYLAND=1`）窗口正常创建，
  但只有 Wine 自绘的 CSD 标题栏可见，客户区全黑；鼠标/键盘无响应。
- 合成器输出截图（sway 输出直接抓图）同样是「橙色 CSD 标题栏 + 全黑客户区」，
  说明不是抓取链路丢帧，而是客户区内容没有进入可见窗口。

**证据**

- `PROTON_LOG=+waylanddrv,+win,+cursor,+seh` 显示 Wine 持续提交客户区 buffer：
  `wayland_surface_attach_shm_buffer surface=0xf7428e60 ... (896x640)` 共 111 次，
  `buffer_release` 111 次（合成器确实消费了这些 buffer）。
- 同一窗口在 sway 树里是 `1286x752`（1280x720 + CSD），但 wl_surface 的 896x640 内容
  没有出现在合成结果里；`wayland_client_surface_attach Created subsurface for toplevel=...`
  表明客户区走的是 **subsurface**（自绘 CSD 结构）。
- 我们的 toplevel capture 只在窗口创建瞬间拿到一帧（也是黑客户区），之后 185s 无新帧；
  watchdog 持续输出。
- GE-Proton10-14 **没有 xdg-decoration 支持**，因此随包 shim（v2）对它无效，
  CSD 无法避免；换成 ≥10-34 可以去掉 CSD，但示例视觉小说 B 在 10-34 上启动即 page fault。

**定位**

- 触发点在 winewayland 的 CSD/subsurface 呈现路径，与 nested 合成器如何合成 subsurface
  有关；和 LAUNCH-WAYLAND（示例视觉小说 C）属于同一族（Wine Wayland 自绘 CSD/subsurface 生命周期）。
- 不是抓取格式/遮挡问题：同一套 toplevel capture 在原生 xdg 窗口（无 CSD）上工作正常。

**规避**

- 示例视觉小说 B 使用 **X11** 后端（Lutris `Graphics=x11`，去掉 `PROTON_ENABLE_WAYLAND`）；已验证可用。

**后续**

- 归入「对齐 KDE」专项，与 LAUNCH-WAYLAND 一起解决；需要对比 KDE 直连下同一 Wine 的
  subsurface/CSD 行为，再决定是补 compositor 还是 Wine 侧。


## 8. 补充（2026-11-10 02:45）：Wine CSD 的黑客户区是 subsurface 合成差异

用 `WAYLAND_DEBUG=1` + `PROTON_LOG=+waylanddrv` 完整抓到了示例视觉小说 B （GE-Proton10-14）的协议：

- 窗口 hwnd=0x30078，CSD 由 Wine 画在 **toplevel 父 surface**（`wl_surface#3`）上，
  buffer 为 shm `XRGB8888`（不透明），并按 `wp_viewport` 裁到窗口大小；
- 游戏内容（DXVK swapchain）走 **client subsurface**（`wl_surface#41`），
  `get_subsurface(child,parent)` → `set_desync()` → `set_position(3,29)` →
  `place_above(parent)`；buffer 是 `zwp_linux_dmabuf` 的 **XR24 / modifier 0 / 1280x720**，
  带 `wp_linux_drm_syncobj` 显式同步点；
- KDE 直连能合成出客户区内容；嵌套 sway 只合成父 surface（CSD），客户区为黑，
  **sway stderr 没有任何 dmabuf/EGL 导入错误**；
- 用 shim 隐藏 `wp_linux_drm_syncobj_manager_v1`（强制隐式 fence）后现象不变 →
  排除显式同步；
- 按 wl_subsurface 规范，新 subsurface 默认就在"兄弟与父的最上层"，
  且 `place_above(parent)` 是合法请求、不做父区域裁剪。Wine 的用法完全合规，
  因此这是 **wlroots/sway 对该结构的合成差异**，不是 Wine 用法错误。

**结论**：Wine 10-14 这类"自绘 CSD + client subsurface（dmabuf）"的 Wayland 窗口，
在 wlroots 合成器下客户区不显示；KWin 正常。v1.0 对这些游戏仍建议 **X11**。
对齐 KDE 的正式解法只有两条：给 wlroots/sway 打 subsurface 合成补丁，
或升级到支持 xdg-decoration 的 Wine（示例视觉小说 B 在 10-34 上崩溃，暂不可行）。

**对抓取链路的影响**：即使合成修好，toplevel capture 只跟踪 toplevel surface 的 damage，
client subsurface 的提交不会产生新帧；这类窗口需要 output+crop 回退抓取（或按帧重启
capture session）。已记录为后续工作。

## 9. 更正（2026-11-10 03:10）：§7/§8 的“合成差异”结论撤回，真实缺陷在抓取

复核方式：源码对照（wlroots 0.20.2 / KWin 6.8.2 / sway 1.12）+ 最小复现客户端
`subwin` + 真实游戏输出整屏抓取（新增 `capture-spike --output`）。

**结果**

- wlroots 0.20.2 能正确合成“Wine 自绘 CSD 父 surface（shm + `wp_viewport`）+
  client subsurface（dmabuf XR24 + `wp_linux_drm_syncobj` 显式同步）”。
  7 组配置全部正常：
  - bare sway gles2（无 shim / decoration v2 shim / shim + `HIDE_SYNCOBJ`）；
  - GemPrism 自己的 `--nested-probe` session（游戏注入同一 socket）；
  - `subwin` 的 shm 子表面、dmabuf 子表面（隐式同步）、dmabuf + 显式同步。
- 旧 `hide-window2.png` 是 **1286x752**，正好是 toplevel 抓取的尺寸，不是
  3840x2160 的合成器输出；同一时刻的 output 抓取里客户区是完整标题画面。
  `ext-image-copy-capture` 的 foreign-toplevel source 只跟随 toplevel surface
  自身的 buffer/damage，不含 client subsurface，这就是“黑客户区 + 无新帧”的来源。
- 02:38 的 `/tmp/out-hide2.ppm` 窗口区域确实是黑的，但今天用 bare sway、app
  nested、带/不带 shim、带/不带 `HIDE_SYNCOBJ` 都无法复现；没有证据把它归因于
  wlroots 合成。此前“隐藏 syncobj 后现象不变 → 排除显式同步”的推理也依赖那次
  不可复现的运行，不再作为结论。

**结论**

- 不是 wlroots/sway 的 subsurface 合成 bug，**不需要**为此打 compositor 补丁；
- 真实缺陷是抓取链路：这类窗口必须走 output+crop（或 scene source）回退，
  1–2 人日可接进 `NestedUpscaleSession`；
- 详细成本评估见 `开发机归档/M2b-subsurface对齐KWin-成本评估.md`。

**保留的有效观察**（与本次结论不冲突）

- Wine 10-14 的 CSD 是已知的 Wine/Proton 低版本兼容问题，不是合成器问题；
- 若未来出现分数缩放或子表面超出窗口几何的偏移/裁剪差异，再按
  `M2b-subsurface对齐KWin-成本评估.md` §4 的 P1→P2 顺序评估补丁。

## 10. 2026-11-10 04:20：按窗口抓取的帧投递已修复（补丁固定 wlroots）

- 根因：wlroots scene capture source 只在有 damage 时渲染，静态窗口/特定提交
  模式下客户端请求帧永远等不到；表现为 host 停在首帧黑图。
- 修复：`tools/patches/wlroots-0.20.2-capture-render-on-request.patch`
  + `tools/build-nested-runtime.sh`（sway + wlroots 成对固定，只对嵌套会话
  设置 `LD_LIBRARY_PATH`）。
- 验证：静态窗口连续出帧；示例视觉小说 B toplevel 抓取得到完整标题画面；产品 + testwin
  140fps；不抓整屏、不受遮挡。
- 未完成：App 会话里游戏**主输出**本身不绘制（与补丁无关，系统库也复现），
  见 `开发机归档/M2b-无人值守交接-20261110.md` §2。
