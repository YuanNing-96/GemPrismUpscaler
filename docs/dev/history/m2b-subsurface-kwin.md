> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# subsurface 合成对齐 KWin：补丁成本评估

日期：2026-11-10
评估对象：sway 1.12 + wlroots 0.20.2（系统包，`/usr/lib/libwlroots-0.20.so`）
对照实现：KWin 6.8.2（invent.kde.org 源码）、sway 1.12 源码

## 0. 结论（TL;DR）

1. **当前症状（示例视觉小说 B / 示例视觉小说 A Wayland 的客户区黑）不是合成器差异，无法复现。**
   wlroots 0.20.2 对 “Wine 自绘 CSD 父 surface（shm + `wp_viewport`）+ client
   subsurface（dmabuf XR24 + `wp_linux_drm_syncobj` 显式同步）” 的合成结果与 KWin
   一致。最小复现客户端 `subwin` 与真实游戏（bare sway、GemPrism 自己的 nested
   session）共 7 组配置全部正常，见 §2。

2. **可复现的真实缺陷在抓取侧，不在合成侧。**
   sway 的 foreign-toplevel 抓取走的是 `view->image_capture_scene` 私有 scene
   快照（`sway/server.c:231`）。它**理论上包含子表面**：用 `subwin` 验证时，
   toplevel 抓取确实抓到了绿色子表面（§2.2）。但对真实 Wine 窗口，这条路径
   只给出“首帧黑 + 之后没有新帧”，新建 session 甚至等不到 constraints；
   也就是说它对这个“晚创建子表面 + dmabuf 显式同步 + 静态 CSD 父表面”的结构
   **不可靠**。旧的 `hide-window2.png` 尺寸是 **1286x752**，正好是 toplevel
   抓取尺寸（不是 3840x2160 的输出整屏），它本来就不是“合成器输出截图”。

3. **不建议为这个症状给 wlroots 打合成补丁。**
   合成层 P0 成本 ≈ 0（没有需要修的东西）；若仍要对齐 KWin 语义，成本从
   0.5–1 人日（同步失败回退）到 3–5 人日（position/clip 语义，跨 sway 仓库）
   再到 3–6 周（scene 重构）。另外还有每次 wlroots 升级 0.5–1 人日的 fork 维护成本。

4. **建议把 1–2 人日投在抓取回退（output+crop / scene source）**，这是真正缺的
   东西；补丁留到“未来确实观察到合成差异”时再按 P1→P2 顺序做。

## 1. 评估方法

- 源码对照：wlroots 0.20.2（freedesktop GitLab tarball）、KWin 6.8.2
  （invent.kde.org tarball）、sway 1.12（GitHub tarball）。
- 最小复现客户端：`开发机归档/m2b-spike/toplevel-capture/subwin.c`
  （xdg_toplevel 父 surface + `wp_viewport` + client subsurface；
  可切换 shm/dmabuf、显式/隐式同步、viewport、desync、静态/动画）。
- 输出整屏抓取：给 `capture-spike` 新增 `--output <name>`
  （`ext_output_image_capture_source_manager_v1`），见 §7。
- 真实游戏 A/B：bare nested sway 脚本 + GemPrism `--nested-probe` 起的 nested
  session（游戏通过 Lutris 注入同一个 Wayland socket）。
- 本地构建 wlroots 0.20.2（meson 390 个 target），加临时日志验证同步路径与
  scene 纹理路径。

## 2. 复现矩阵

### 2.1 合成结果（客户区是否显示）

| 配置 | 结果 |
| --- | --- |
| bare sway，gles2，无 shim | 正常（64855 色，标题画面完整） |
| bare sway，gles2，decoration v2 shim | 正常 |
| bare sway，gles2，shim + `GEMPRISM_HIDE_SYNCOBJ=1` | 正常 |
| GemPrism 自己的 nested session（`--nested-probe` 起的 sway） | 正常 |
| `subwin`：shm 子表面，无显式同步 | 正常（绿色区域 = 视口目标 800x600） |
| `subwin`：dmabuf（线性 XR24），隐式同步 | 正常 |
| `subwin`：dmabuf + 显式同步（acquire 点已 signal） | 正常 |
| `subwin`：dmabuf + 显式同步（commit 后再 signal） | 正常（修掉 fd 混用后） |

早期 02:38 的 `/tmp/out-hide2.ppm` 窗口区域确实是黑的（214 色），但今天在
bare sway、app nested、带/不带 shim、带/不带 `HIDE_SYNCOBJ` 的重复测试都无法
复现。该帧只能说明“那一次运行处于异常状态”，不能作为 wlroots 合成 bug 的证据。

> 复现客户端的第一版曾把 gbm device fd 复写进 `drm_fd`，导致
> `drmSyncobjTimelineSignal` 在另一个 fd 上返回 ENOENT、commit 永远停在
> `lock_surface_commit` 里，看起来和“黑客户区”一模一样。修掉这个 fd 混用后
> dmabuf+显式同步正常。这个坑也说明：单看“黑”很容易误判成合成器问题，
> 必须有客户端侧 signal/commit 的证据。

### 2.2 抓取结果（问题真正所在）

同一时刻、同一窗口：

- toplevel source（`capture-spike --app-id bgi.exe`）：1286x752，橙色/蓝色 CSD
  标题栏 + **全黑客户区**——这正是旧 `hide-window2.png` 的样子与尺寸。
- output source（`capture-spike --output HEADLESS-1`）：3840x2160 整屏，
  窗口区域是完整的标题画面。

补充验证：`subwin`（父 surface + 子 surface 都有明确提交）用 toplevel 抓取时，
得到的 1286x752 图像里绿色子表面**是存在的**（green=477840）。所以
“toplevel 抓取一律不含子表面”不成立；问题在于这条 scene 快照路径对 Wine
窗口的**首帧/帧驱动**不可靠——抓到的第一帧早于 DXVK 首帧（黑），之后 CSD
父表面不再提交，抓取端也就收不到新帧（旧记录里的“185s 无新帧 / watchdog”
由此解释）。新建 session 时甚至会卡在等 constraints（`capture-spike` 超时）。
用户在 GemPrism host 窗口里看到的“黑客户区”正是这份 toplevel 抓取帧，
不是合成器输出；这也解释了为什么 KDE 直连（KWin 自己合成整窗）看起来正常。

因此修复方向不是“让 sway 合成子表面”，而是换一条不依赖 Wine 窗口结构的
抓取路径：抓输出整屏（合成器最终画面，天然包含所有子表面）+ 按窗口矩形裁剪。

## 3. KWin vs wlroots 代码级差异清单

| 语义点 | KWin 6.8.2 | wlroots 0.20.2 | 现状影响 | 对齐补丁规模 |
| --- | --- | --- | --- | --- |
| subsurface 位置 | `subcompositor.cpp:116` 把 `set_position(x,y)` 除以 `parent->clientToCompositorScale()`（分数缩放/viewport 感知），存到父 surface 的 pending 状态 | `wlr_subcompositor.c:65` 起存原始整数坐标；`subsurface_tree.c:121/141` 直接作为 scene node 位置 | scale=1 时等价；分数缩放或父 viewport 非 1:1 时子表面可能偏移 | 20–60 行，风险中 |
| 父区域裁剪 | KWin 的 item 树不把子 surface 裁到父 surface 范围（观察行为 + 规范） | sway 在 `sway/tree/view.c:1033 view_center_and_clip_surface` 给窗口内容树设 clip；`subsurface_tree.c:68` 再把 clip 传给子节点 | 子表面超出窗口几何时，sway 会裁掉；本次 1280x720 @ (3,29) 落在 1286x752 内，未触发 | sway 侧 10–30 行（CSD 窗口不裁），风险中 |
| 显式同步失败 | 等待/回退 | `render/gles2/pass.c:206-224`：export sync file / `eglCreateSyncKHR` / `eglWaitSyncKHR` 任一失败就 **静默 return**，该 quad 不画 | 本机（NVIDIA EGL）未触发；换驱动/平台时可能表现为局部黑 | 20–40 行（失败时回退 CPU 等待或放弃 wait），风险低 |
| 可见性/damage | item 级 damage | scene node damage + clip | 本次未观察到差异 | — |
| map/unmap、desync 应用时机 | 父 commit 应用 position/order/desync | 一致（`wlr_surface_map` 递归、`subsurface_consider_map`） | 一致；`subwin` 验证 | 0 |

补充：wlroots 的 `surface_reconfigure` 把显式同步点作为 `wlr_scene_buffer`
的 `wait_timeline` 传给渲染 pass（`types/scene/surface.c:348`），而
`types/wlr_linux_drm_syncobj_v1.c:237` 的 `lock_surface_commit` 会先锁住
commit 直到 acquire 点 materialize。这两段在本机的 NVIDIA + EGL 下都工作正常
（已用日志确认 lock→signal→apply 的完整链路）。

## 4. 补丁分层与成本

| 层级 | 内容 | 代码量 | 开发 | 回归 | 风险 |
| --- | --- | --- | --- | --- | --- |
| P0 | 诊断/复现工具（subwin、output capture） | ~350 行（已完成） | 0.5 人日 | — | 低 |
| P1 | 显式同步失败不再静默跳过 | 20–40 行 | 0.5–1 人日 | 0.5 人日 | 低 |
| P2 | position 换算（分数缩放/viewport）+ CSD 窗口不做父裁剪 | wlroots 20–60 行 + sway 10–30 行 | 3–5 人日 | 2–3 人日 | 中（跨仓库、影响所有窗口） |
| P3 | scene 改成 KWin 式 item 树（每 surface 一个 item，统一坐标/裁剪） | 1500–3000 行 | 3–6 周 | 1–2 周 | 高，不建议 |

### 4.1 构建与分发成本

- 依赖：meson + ninja。系统没有时用 `python3 -m venv` + pip 安装（无需 root），
  一次 ~10s；`meson setup` ~10s。
- 编译：390 个 target，本机约 55s；产物 `libwlroots-0.20.so` 4.8MB。
- 替换方式：只给嵌套 sway 用 `LD_LIBRARY_PATH` 指向自建 `.so`；sway 二进制
  不需要重编（同 soname、ABI 兼容），宿主/游戏进程不受影响。
- 打包：安装包 +4.8MB；MIT 许可兼容；CI 需要 meson/ninja（可用 pip venv）。
- 维护：每次 wlroots 0.20.x→0.21 升级要 rebase，0.5–1 人日/次；上游接受
  “compositor 特定语义”补丁的概率低，长期等于 fork。
- 风险：与系统 `libwlroots-0.20.so` 行为漂移；NVIDIA/AMD 双 GPU、HDR/色彩管理
  （0.20 新增 color-management）都要双份验证；还有 glibc/wayland ABI 之外的
  隐性依赖（例如 `wlr_egl` 私有扩展）。

### 4.2 验证成本

- 已有：`subwin`（shm/dmabuf、显式/隐式同步、viewport、desync 开关）、
  `capture-spike --output`、真实游戏 harness。
- 补丁验证矩阵：`subwin` 4 组 × {gles2, vulkan} × {NVIDIA, AMD} + 真实游戏 2 个
  + GTK/Qt/XWayland 常规窗口回归；约 2–3 人日。
- 不做补丁、只做抓取回退的验证：1 人日（output+crop 已经在 spike 里跑通，
  接进 `NestedUpscaleSession` + 裁剪 + watchdog 即可）。

## 5. 建议

1. **撤回** `开发机归档/M2b-v1.0-已知问题.md` §8 的“wlroots/sway 合成差异”结论
   （已追加 §9 更正）。
2. **不做** wlroots 合成补丁；把预算投到抓取回退：
   - 对 “toplevel capture 长时间无新 damage，且窗口存在 client subsurface” 的
     窗口，切 `ext_image_copy_capture` 的 output source + 按窗口矩形裁剪；
   - 这条路径顺带解决 4K 下 toplevel 抓取帧率不足的问题（与单输出方案共用）；
   - 预计 1–2 人日，不需要 fork。
3. 保留 decoration shim；Wine 11 起走 SSD，CSD 结构会自然减少。
4. 只有当未来在分数缩放/超窗子表面上确实观察到偏移或裁剪差异时，再按 P1→P2
   打补丁；P1 风险最低，应作为第一个候选。

## 6. 复现命令

```bash
# 最小复现（shm 父 + viewport + dmabuf 子表面 + 显式同步）
开发机归档/m2b-spike/toplevel-capture/subwin        # 需先起 headless sway

# 输出整屏抓取
开发机归档/m2b-spike/toplevel-capture/capture-spike --output HEADLESS-1 --out /tmp/out.ppm

# 真实游戏（bare sway，输出整屏）
/tmp/gp-game-shot.sh base
/tmp/gp-game-shot2.sh appcfg     # app 等价 sway 配置 + decoration shim

# 本地构建 wlroots（打补丁/加日志）
python3 -m venv /tmp/wlbuild-venv && /tmp/wlbuild-venv/bin/pip install meson ninja
PATH=/tmp/wlbuild-venv/bin:$PATH meson setup /tmp/wlroots-build \
  开发机归档/m2b-runtime/src/wlroots-0.20.2 -Dexamples=false -Dxwayland=enabled
PATH=/tmp/wlbuild-venv/bin:$PATH ninja -C /tmp/wlroots-build
```

关键产物：

- `/tmp/gp-appgame-out.ppm` / `gp-appgame-win.png`：app nested session 的输出整屏，
  游戏正常；
- `/tmp/gp-gameshot-base/`、`/tmp/gp-gameshot-shim/`、`/tmp/gp-gameshot-appcfg/`、
  `/tmp/gp-gameshot-hidesync/`：四组 A/B；
- `/tmp/wlroots-build/libwlroots-0.20.so`：本地构建产物（含临时日志，勿分发）；
- `开发机归档/m2b-spike/toplevel-capture/subwin.c`、`capture-spike.c`（`--output`）。
## 8. 2026-11-10 04:00 补丁落地（无人值守）

按 §5 的“不做整屏抓取”路线，改为给 wlroots 的 scene capture source 打
“按需渲染”补丁，并把 wlroots 与 sway 成对固定：

- 补丁：`tools/patches/wlroots-0.20.2-capture-render-on-request.patch`
  - 文件：`types/ext_image_capture_source_v1/scene.c`
  - 行为：`source_request_frame()` 不再等 damage，而是强制整窗 damage 后立即
    `source_render()`；客户端每请求一帧就能拿到当前内容。
- 构建/打包：`tools/build-nested-runtime.sh`
  - 下载/复用 wlroots 0.20.2 tarball，应用补丁，meson+ninja 构建；
  - 把 `libwlroots-0.20.so` 和 sway 二进制放到
    `src/GemPrismUpscaler/bin/Release/net10.0/runtime/sway/`；
  - 运行期由 `NestedSession.ResolveWlrootsLibDir()` 检测同目录的
    `libwlroots-*.so`，只对嵌套 sway 设置 `LD_LIBRARY_PATH`；可用
    `GEMPRISM_WLROOTS_LIB_DIR=off` 关闭，回退系统库。
- 验证：
  - 静态 `subwin`（内容不变）连续抓 3 帧成功，191fps；
  - bare sway + 补丁库：示例视觉小说 B 的 **toplevel 抓取**直接抓到 1286x752 完整标题画面
    （64855 色），客户区不再是黑；输出整屏同样正常；
  - 产品路径 + `testwin`：host 窗口显示红色窗口与移动白块，约 140fps；
  - 无遮挡问题：抓取的是窗口自己的 scene 子树，不是屏幕矩形。

**遗留阻塞（与补丁无关）**：由 App 启动 Lutris/游戏时，嵌套 sway 的**主输出**
客户区仍是黑的（不是抓取黑）；用系统库、不打补丁也能复现，且同一份配置/二进制
在 bare sway 下能正常出画面。说明是“App 会话里游戏没有真正开始绘制”的独立问题
（疑似 headless 输出没有 frame callback 导致 DXVK vsync 停滞，或键盘 enter/激活
时序）。详见 `开发机归档/M2b-无人值守交接-20261110.md`。
