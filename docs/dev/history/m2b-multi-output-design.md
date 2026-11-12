> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M2b 多窗口虚拟屏幕方案（2026-11-08 决策稿）

> 状态：**已实施（2026-11-09），等待用户验收**。本文档是恢复入口；
> 实现记录见 §8，验收步骤见 `开发机归档/M2bβ1-用户验收步骤.md`。
> 目标不是只修示例视觉小说 A / B，而是建立 X11 / XWayland / native Wayland、
> KDE / GNOME / 其他 Wayland 合成器、视觉小说 / raw input 的通用展示层。

## 1. 决策

采用用户提出的**每实际窗口一个虚拟屏幕 + 一个备用屏幕**方案：

```text
virtual output 数 = 当前实际游戏顶层窗口数 + 1
```

- 每个游戏实际顶层窗口移动到独立 headless output；
- 每个 output 固定为启动时指定物理显示器的实际分辨率（如 HDMI 3840×2160）；
- 始终保留 1 个空 output 作为备用池；
- 新窗口出现：取备用 output，再把窗口 move 到该 output；若没有备用则先 create；
- 窗口关闭：释放/移除对应 output，保持 1 个备用；
- 游戏运行期间不因窗口大小改变 output 分辨率。

## 2. 为什么用这个方案

已排除：

- 只做 `wlr-layer-shell`：GNOME 不支持；任务栏/焦点/模态不可用。
- 只做 X11 host：XWayland 关闭/纯 Wayland 不可用。
- 只做原生 `xdg_toplevel`：X11 会话不可用。
- 标准 Wayland 无 per-window capture；native Wayland 被遮挡窗口无法单独抓取。
- 每个窗口一个 output 但动态热插拔不控制：游戏会收到显示器变化事件。

当前方案：

- output 提供隔离，**不依赖 per-window native Wayland capture 协议**；
- 每个 output 只有一个目标窗口，screencopy 内容无遮挡；
- host 窗口由 X11/xdg 双后端承载，可覆盖 KDE/GNOME/其他 Wayland/X11；
- raw input 仍在 nested 内部处理，和 host 合成器无关。

## 3. 关键结构

### 3.1 NestedSession

- 启动基础 output `HEADLESS-1`，分辨率为 `--monitor` 对应物理屏；
- `--monitor` 必填；`--nested-size` 废弃/忽略；
- 提供 `SwayIpc` 给 output pool。

### 3.2 SwayOutputPool

职责：

- 创建 `HEADLESS-N`（`swaymsg create_output`）；
- 设置分辨率为 monitor 物理分辨率；
- 维护 output origin（`swaymsg -t get_outputs`）；
- 维护空闲池：任何时刻 **至少 1 个空 output**；
- 窗口出现：分配空闲 output，`[con_id] move container to output NAME`；
- 窗口关闭：`output NAME unplug` 移除多余 output。

约束：

- output 位置自动，使用 get_outputs 实际 `rect`；
- 窗口 rect 是全局坐标，裁剪时减去 output origin；
- 动态 output 会触发 XWayland RandR 屏幕变化，游戏可能收到事件；这是本方案最大风险，需实测。

### 3.3 NestedWindowTracker

新增：

```text
AllWindows: 所有可见游戏顶层窗口（main + dialogs + transient + 同 token/pid）
```

每个窗口：

- con_id；
- title/app_id/pid/shell；
- rect（全局）；
- fullscreen_mode；
- transient/type；
- 是否 dialog/popup。

主窗口仍用于标题和全屏判断，但展示层按**每个窗口**建状态。

### 3.4 WindowState（每个实际窗口）

```text
WindowInfo
OutputName
OutputOrigin (x,y)
NestedCapture (该 output)
CropRect (窗口 rect - output origin)
NestedHostWindow (KDE host)
VulkanBlitter
Upscale bool
Focused bool
Input mapping (crop + output origin)
```

- Host 原窗口客户区 = 窗口 rect（裁剪后）；
- Host 标题 = 窗口 title（主窗口/弹窗各自）；
- 游戏全屏 → 该 host 全屏；窗口化 → 普通窗口；
- 原窗口模式：1:1 / 全屏 bilinear 等比例；
- 超分：该 host 全屏 Anime4K。

### 3.5 焦点 / A 键

- 只有当前 KDE host 获得焦点；
- host FocusIn → `swaymsg [con_id] focus`，更新输入映射；
- A 只切换**当前焦点 host**；
- 同一时间只允许一个 host 处于超分；
- 超分 host 出现新窗口 → 自动退出超分，回到原窗口，显示新窗口 host；
- 焦点回到 host → 保持原窗口模式，按 A 才超分（沿用用户确认逻辑）。

### 3.6 输入

- 每个 host 的指针事件用该 host 的 `CropRect + OutputOrigin` 映射；
- 键盘事件发给当前焦点 host 对应的 nested 窗口；
- X11/XWayland：XTest；
- native Wayland：virtual pointer/keyboard helper；
- raw input 仍在 nested 内实现，和多 output 无关。

### 3.7 弹窗 / 模态

- 弹窗是独立 output + 独立 host；
- 不再需要从主窗口裁剪里找弹窗，不会遮挡；
- 仍需处理模态：
  - 弹窗出现时 `floating enable` + `focus`；
  - 主窗口区域点击是否转发需按模态语义控制；
  - host 关闭必须在事件循环外处理（不能在 X11 回调里 Dispose，否则段错误）。

### 3.8 Host 生命周期

- 游戏退出：关闭对应 host、释放 output；session 保留；
- `--nested-session` 下关闭 host：session idle，不退出进程；
- `--nested` 一次性：host 关闭才结束；
- 禁止在 X11 `Closed`/`CloseRequested` 回调里直接 Dispose；
  - 只设 `_hostClosePending`；
  - 主循环统一处理。

## 4. 当前代码状态（实施前，历史）

- `--monitor` 已要求必填；nested 输出固定为 monitor 物理分辨率；
- 抓完整输出后按 `GameBounds`/rect 裁剪；单 host 已实现；
- 默认原窗口、A 超分、超分失焦回原窗口；
- `NestedCapture` 已改为 RGB 源 + Vulkan R8，去掉 CPU 红蓝交换；
- 4K 捕获实测约 20–28fps；
- 未确认/未提交的探索性改动：
  - dialog union/gate/focus；
  - host 关闭后 session idle 的局部处理；
  - 顺序启动第二个游戏的预裁剪修复；
  - **这些需要在新多窗口实现中重做/验证，不能视为已完成。**

## 5. 实施顺序

1. `NestedCapture` 支持按 output name 选择（当前只 bind 第一个 wl_output）。
2. `NestedWindowTracker.AllWindows` 暴露全部可见顶层。
3. `SwayOutputPool`：create/unplug、resolution、origin、spare=1。
4. 新 `NestedMultiWindowSession`（或重构 `NestedUpscaleSession`）：
   - WindowState 集合；
   - 每 host/capture/blitter；
   - focus/A/upscale；
   - 新窗口 auto exit upscale；
   - 关闭/释放。
5. 测试：
   - 空会话 = 1 output；
   - 主窗口 = 2 output（1 used + 1 spare）；
   - 主+弹窗 = 3 output；
   - 关弹窗回 2；
   - 关游戏回 1；
   - 连续启动两个游戏；
   - 示例视觉小说 A / B退出确认弹窗；
   - 最大化、Alt+Tab、A、Wayland 后端。
6. 文档/验收手册更新。

## 6. 风险/待确认

- 动态 output 的 XWayland RandR 热插拔可能让游戏重置窗口/闪屏；
- native Wayland popup（菜单/tooltip）不是 toplevel，不能独立 output；
- 每个 output 4K 的内存/GPU/抓帧开销；
- 多 host 的 KDE/GNOME 任务栏/焦点/模态语义；
- host 关闭、进程生命周期；
- 输入偏移必须严格包含 output origin + window rect origin。

## 7. 备份/恢复

- `git stash@{0}`：M2bα layer-shell 单窗口版本（A/fixed 前）；
- `git stash@{1}`：用户原有 `back`；
- 文档/截图/runtime 备份：`/tmp/gemprism-pre-xdg-20261108-212452.tar.gz`；
- 当前工作区是 A 之后的实验状态，尚未提交。


## 8. 实施记录（2026-11-09）

### 8.1 代码路径

| 文件 | 作用 |
| --- | --- |
| `Core/Platform/Linux/Nested/SwayOutputPool.cs` | 动态 create/unplug、解析真实 output 名/rect、spare=1、高水位命名 |
| `Core/Platform/Linux/Nested/NestedWindowTracker.cs` | `AllWindows`（main + same pid/token/launcher tree/transient）、popup 过滤、move/focus |
| `Core/Platform/Linux/Nested/NestedCapture.cs` | 绑定并选择指定 `wl_output`（不再永远第一个） |
| `Core/Platform/Linux/Nested/NestedHostWindow.cs` | 每窗口 X11 host；transient/dialog；UTF-8 `_NET_WM_NAME`；EWMH activate；WM_DELETE |
| `Core/Platform/Linux/Nested/NestedInput.cs` | XWayland 走 XTest/XWarp；native Wayland 走 virtual pointer/keyboard；多 output 全局坐标 |
| `Core/Engine/NestedUpscaleSession.cs` | WindowState 字典、每窗口 capture/host/blitter、focus/A、新窗口退超分、延迟关闭 |
| `Program.cs` | SIGINT/SIGTERM 只置标志，主线程先停 presentation 再停 sway |

### 8.2 与 §5 的差异

1. **output 名称不能假设**：sway headless `create_output HEADLESS-2` 在 unplug 后可能实际生成 `HEADLESS-3`。池记录“创建前名称集合”，取实际新增 output，并以高水位序号继续；这是实测修复的关键。
2. **关闭窗口时不立即 unplug 对应 output**：如果还有其他窗口，释放的 output 直接作为 spare 保留，避免每次弹窗关闭都触发一次 create/unplug 热插拔。只有窗口数归零才 `TrimToBase`。
3. **XWayland 输入不再优先 virtual pointer**：实测 virtual pointer 不能可靠驱动 nested XWayland 的 X 指针；XWayland 改用 `XWarpPointer` + `XTestFakeButtonEvent/KeyEvent`。native Wayland 仍用 virtual pointer/keyboard。
4. **transient_for 是 X11 window id，不是 sway con id**：模态拦截与宿主 parent 关系改为拿 `window_properties.transient_for` 去匹配 host X11 window id。
5. **`WM_DELETE_WINDOW` 在 XClientMessage 的 data.l[0]**：旧代码读 `XEvent.Detail`（data.l[3]）所以收不到关闭请求；改为 `XClientMessageEvent.Data0`。
6. **host 激活**：只用 `XSetInputFocus` 可能让宿主留在 native Wayland 窗口后面；新增 EWMH `_NET_ACTIVE_WINDOW` 请求。
7. **host title**：中文标题用 `_NET_WM_NAME`/`UTF8_STRING`；`XStoreName` 的 Latin-1 WM_NAME 会乱码，仅作为回退。
8. **host backend 仍为 X11**：本阶段没有实现 xdg_toplevel host；纯 Wayland/禁用 XWayland 的环境不在本次验收范围。

### 8.3 已验证（2026-11-09，KDE Plasma 6.8.2 / HDMI-A-1 4K）

- 0 窗口 = 1 output；1 窗口 = 2；3 窗口 = 4；4 窗口 = 5；关闭后逐级回落。
- 3 个测试窗口分别 move 到 HEADLESS-1/2/3，crop + 抓帧正确；host 截图内容/颜色/方向正确。
- 弹窗独立 output/host；`WM_TRANSIENT_FOR` 指向主 host；点击主 host 被模态拦截，点击弹窗命中 `(50,50)`。
- 第二个 output 上的窗口：host 内点 `(100,100)` → nested 指针 `(5610,1030)`，游戏收到 `(100,100)`；键盘 B → nested keycode 56。
- `Alt+Shift+A` 超分、失焦自动回原窗口、焦点回来不自动超分；新窗口出现时旧超分自动退出。
- 全屏游戏请求 → sway `fullscreen_mode=1`、host `_NET_WM_STATE_FULLSCREEN`、窗口化恢复。
- 关闭宿主窗口：会话保持运行，无崩溃；关闭游戏全部窗口：output 清空，会话 idle。
- 真实游戏《示例视觉小说 B》进入 nested：1280×720 原窗口、4K 超分截图正确；中文标题 `_NET_WM_NAME` 正常。
- 真实游戏《示例视觉小说 A》进入 nested：800×600 原窗口截图正确，中文标题正常；退出确认弹窗尚未实机触发，由合成 dialog 用例覆盖。
- native Wayland `vkcube` 创建 `xdg_shell` 窗口并得到独立 host。
- 有/无窗口时 SIGINT 均 exit code 0；无残留 sway/Xwayland。
- 构建 0 warning / 0 error；测试 **118/118**。

### 8.4 本阶段未完成

- xdg_toplevel host（纯 Wayland 无 XWayland）。
- popup/菜单/tooltip 独立 output（协议上不可行；只能随父窗口）。
- raw input、pointer lock/relative pointer。
- 多个 host 的自动平铺/避让；目前同物理屏居中，弹窗 transient 置顶。
- 性能优化（X11 窗口抓取、DMA-BUF）。
