# v1.0 后功能与已知问题（推迟项）

- 更新时间：2026-11-13
- 适用：v1.0 发布前
- 决策：下列功能在 v1.0 前**不主动开发**，按已知问题记录；v1.0 发布后再按优先级评估。
  **原 M2c 仅剩的两项（嵌套 DMA-BUF 零拷贝、SDR 色彩准确性）也列入本文件**：v1.0 已能正常玩
  视觉小说，HDR 桌面下 SDR 也未观察到发灰，因此不再阻塞 v1.0。
- 目的：把 v1.0 收敛到“嵌套单输出 + 按 toplevel 抓取 + 窗口化超分/还原”的主路径，减少发布前
  的非必要工作量。

---

## 0. 原则

1. 不阻塞 v1.0 主路径：窗口化视觉小说/2D 游戏的捕获、超分、还原、输入、弹窗、退出必须稳定。
2. 不静默失效：确实不支持的能力，在 UI/文档/日志里明确说明，不假装支持。
3. 每项记录“现象/现状、规避、重启条件”，便于 v1.0 后直接继续，不靠考古。
4. 旧模式（`--window`、`--portal-*`、多输出池）只作为技术归档/回退，不在 v1.0 前继续投入。

---

## 1. M2bβ2：XWayland 指针锁与 raw input

**状态：v1.0 前不做，已知问题。**

### 原计划

见 [m2-plan.md](m2-plan.md) 的 β2 小节：

- XWayland：nested XTest relative + `XI_RawMotion` 探针；按钮/滚轮 raw；
- 原生 Wayland：virtual pointer relative（Wayland 没有 raw 概念）；
- 示例视觉小说 C （`lutris:rungameid/<id>`，X11）实机验收；
- 如果 XTest 不满足，再评估 patched wlroots/自定义输入后端（uinput 受 libseat 限制）。

### 当前实际状态（2026-11-13）

- **原生 Wayland**：相对注入已实现（`wlr-virtual-pointer` 的 `motion`），指针锁场景下按相对位移
  跟随宿主；这条不缺。
- **XWayland**：仍是 `XWarpPointer` 绝对定位（`NestedInput.SendPointerPosition`）；
  `XTestFakeRelativeMotionEvent` 只有声明，没有产品调用。
- **没有** X11 grab 检测（无 XRecord）、没有 XI2 raw 探针、没有按钮/滚轮 raw 验收。
- **示例视觉小说 C**：Wayland 启动器黑屏是已知问题，规避为 X11；Lutris 已设置
  `PROTON_ENABLE_WAYLAND=0` / `Graphics: x11`，但**X11 下的镜头/鼠标视角没有验收记录**。
- git 历史里的 `97dbbf2 M2bβ2: minimize mirroring...` 是旧 β1 路线的验收修复
  （最小化镜像、CSD shim、绝对/相对输入、Bgr888 抓帧），不是本节的 raw + 示例视觉小说 C。

### 为什么推迟

- 视觉小说/2D 主路径不受影响；raw/指针锁只影响 FPS/3D 鼠标视角。
- Wine 的 raw input 可能走 HID（`/dev/input`/hidraw），XTest 造不出设备级事件；是否可行要先做
  PoC，不能直接排期。
- 示例视觉小说 C不是 v1.0 的主要目标游戏，且其 Wayland 启动器问题本身已推迟。
- 需要新增 XRecord 监听、指针模式状态机、相对注入和示例视觉小说 C实机验收，属于新功能而不是收尾。

### 规避

- 视觉小说/2D：无需处理，现有绝对注入可用。
- FPS/3D：v1.0 可使用非嵌套的**全屏/输入穿透**路径（M2a portal，游戏直接收真实输入）作为
  workaround；`--window` 的 `XSendEvent` 仍不满足 raw，嵌套模式不承诺鼠标视角。
- 示例视觉小说 C：使用 X11 可绕过启动器黑屏；镜头是否可用需 v1.0 后实测。

### v1.0 后重启顺序

1. 30 分钟 PoC：外部 `XTestFakeRelativeMotionEvent` 注入 nested XWayland，示例视觉小说 C X11 看镜头是否响应。
2. 若响应：XRecord 监听 `X_GrabPointer`/`X_UngrabPointer` → 相对/绝对模式切换 → 按钮/滚轮验证。
3. 若不响应：确认是 HID raw，记录 no-go；不再为 raw 投入 uinput/libei（会动真实光标/需要更高权限）。
4. 最后做示例视觉小说 C X11 实机与反作弊限制文档。

---

## 2. 混合 GL + Vulkan 管线

**状态：v1.0 前不做，已知问题。**

- 当前所有内置/自定义 effect 都是 SPIR-V compute pass，最终 Lanczos-2 也是 Vulkan graphics pass；
  没有已知的 GL 独占需求。
- 复核结论倾向 **no-go**：引入 headless EGL + DMA-BUF 交接会增加上下文/同步/跨 GPU 复杂度，
  对现有 effect 没有收益。
- v1.0 后只有在出现“必须用 GL 的 effect/第三方滤镜”时再评估；方案是 headless EGL 渲染到
  DMA-BUF，再由 Vulkan 导入采样。
- 文档口径：不再把“混合管线”列为 v1.0 待办；在 [design.md](architecture.md) 记录为 no-go 候选。

---

## 3. 多屏连续拼接

**状态：v1.0 前不做，已知问题。**

- 当前是“每块屏一个独立 overlay/swapchain”，选中哪块屏就只覆盖哪块；跨屏移动窗口会重建/跟随，
  但不做跨屏连续画面。
- 视觉小说/2D 没有跨屏拼接需求；跨屏拼接还会引入内容矩形切分、每屏同步、输入跨屏映射等回归面。
- 规避：需要双屏时，把游戏窗口放在目标屏，或使用非嵌套模式；不做跨屏拉伸。
- v1.0 后若确有需求，再按“多输出 layer surface + 内容矩形切分 + 每屏 swapchain”评估。

---

## 4. 非嵌套 X11 raw input

**状态：v1.0 前不做，已知问题；与第 1 节同源。**

- 纯 X11 会话的 raw input 需要 uinput/libei 级别的设备注入；uinput 会创建全局设备、可能移动真实
  光标，libei 需要 portal/权限，均不适合 v1.0 默认路径。
- 非嵌套模式只承诺视觉小说/2D 输入；FPS/鼠标视角不承诺。
- 与第 1 节一起在 v1.0 后评估；若第 1 节判定 raw 不可行，本节同样 no-go。

---

## 5. XWayland 撕裂最终方案（DRI3+fence / portal）

**状态：v1.0 前不做，已知问题。**

- v1.0 主路径是嵌套 + `ext-image-copy-capture` 按 toplevel 抓取，抓的是合成后的窗口 buffer，
  不再走 `XShmGetImage`，因此**主路径不依赖**这个最终方案。
- 仍受影响的是旧的非嵌套 `--window` X11/XWayland 路径：`XShmGetImage` 读取 GPU 更新窗口时可能
  拿到半帧。规避：源程序开 VSync；或改用嵌套模式。
- v1.0 后可选方案：
  - DRI3 + `XCompositeNameWindowPixmap` + `DRI3BuffersFromPixmap` + fence → Vulkan external
    memory/semaphore；只支持 `nfd==1 && modifier==0`，其余回退 XShm（spike 结论见
    [zero-copy-spike.md](history/zero-copy-spike.md)）；
  - portal 捕获 XWayland 窗口（合成帧，天然无半帧），但会包含装饰/按输出缩放，且需要系统选择器。
- 不静默宣称修复：若未达到“真实游戏无旧帧/撕裂”，继续记录为已知限制。

---

## 6. 嵌套内完整 HDR 游戏捕获

**状态：v1.0 前不做，已知问题。**

- 当前嵌套合成器（sway 1.12 + wlroots 0.20）不 advertise `wp_color_manager_v1`；`HEADLESS-1`
  是 SDR 输出，没有 HDR metadata。因此游戏在嵌套里无法按 HDR 输出/提交。
- v1.0 只保证 **HDR 桌面下 SDR 内容正确**（SDR 直通，不发灰/不偏色），已在实机验证。
- 完整 HDR 需要：sway/wlroots 接入 `wp_color_manager_v1` + 输出 HDR metadata + 客户端 HDR
  表面 + 10-bit swapchain + tone mapping；这是新功能，不是收尾。
- v1.0 后若重启，优先评估“portal/KWin HDR 流”而不是给嵌套合成器补 HDR。

---

## 7. Portal/PipeWire 零拷贝

**状态：v1.0 前不做，已知问题。**

- 当前 portal 路径是 `gst-launch pipewiresrc` + CPU 帧管道（M2a 的回退/验证路径），没有
  DMA-BUF 导入。
- v1.0 的零拷贝只做嵌套 toplevel 主路径（见 [m2c-plan.md](m2c-plan.md)）；portal 继续用现有
  CPU 路径，保证可用和回退。
- v1.0 后若要 portal 零拷贝：改用原生 `libpipewire` 客户端 + `SPA_DATA_Dmabuf` +
  `SPA_META_SyncTimeline`/显式同步，失败回退 memfd。

---

## 8. 原 M2c 与已记录的其他 v1.0 后项

### 8.1 原 M2c：嵌套 DMA-BUF 零拷贝

**状态：v1.0 前不做；v1.0 后可选优化。**

- 现状：嵌套主路径 `ToplevelCapture` 走 `wl_shm` + memfd，4K 源实测约 24fps；720p/800p
  视觉小说源不受影响，v1.0 主路径已经够用。
- 方案：GBM 分配 dmabuf + `zwp_linux_dmabuf_v1` → `ext-image-copy-capture` 写入 →
  Vulkan `VK_EXT_external_memory_dma_buf` 导入；失败回退 shm。完整实现与验收见
  [m2c-plan.md](m2c-plan.md)。
- 为什么推迟：v1.0 目标游戏是视觉小说/2D，4K 源不是主场景；零拷贝需要新增格式协商、GBM
  设备匹配、Vulkan 导入和同步，属于性能优化而不是发布必需。
- 重启条件：v1.0 后如果用户实际遇到 4K 源帧率不足，或 M3 的性能数据表明抓帧是瓶颈。

### 8.2 原 M2c：SDR 色彩准确性

**状态：v1.0 已满足；v1.0 后作为回归项，不新增功能。**

- 现状：`VulkanBlitter` 优先精确的 `B8G8R8A8Unorm` + `VK_COLOR_SPACE_SRGB_NONLINEAR_KHR`；
  HDR 桌面下 SDR 内容实测正常，没有观察到发灰/偏色。
- v1.0 只保持这条路径；colorspace 不精确时记录并回退，不静默用错。
- 完整 HDR 游戏捕获/10-bit 输出仍不做（见 §6）；可选 10-bit 宿主输出实验见
  [m2c-plan.md](m2c-plan.md) §3.6，不纳入任何验收门槛。

### 8.3 已记录的其他 v1.0 后项

以下两项已在 [backlog.md](backlog.md) §4.1 记录，不重复展开：

- 图像光标转发（G5 方案 B）：Wine 图像光标目前仍显示箭头。
- 宿主窗口图标：KDE 标题栏/任务栏默认图标。

---

## 9. v1.0 明确不承诺

- 嵌套模式下 FPS/3D 游戏的鼠标视角/raw input。
- 示例视觉小说 C在嵌套模式下的启动器画面（Wayland）与镜头（X11 未验收）。
- 完整 HDR 游戏捕获/输出；只保证 HDR 桌面下 SDR 正确。
- 原 M2c 的嵌套 DMA-BUF 零拷贝、可选 10-bit 宿主输出。
- 混合 GL+Vulkan 管线、跨屏连续拼接、非嵌套 raw input、XWayland DRI3+fence 撕裂修复。
- portal/PipeWire 零拷贝。

这些项不影响 v1.0 的窗口化视觉小说/2D 主路径；v1.0 后按本文件顺序重启。