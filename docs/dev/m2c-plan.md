# M2c · 嵌套零拷贝与色彩收尾（原计划；v1.0 后可选）

- 更新时间：2026-11-13
- 状态：**v1.0 前不做，不阻塞 v1.0**。v1.0 已能正常玩视觉小说，HDR 桌面下 SDR 也未观察到
  发灰；本文保留实现/验收资料，v1.0 发布后作为可选优化重启。
- 原目标版本：`0.6.0`；现不排期。
- 目标（未来）：解决嵌套主路径（4K 源）的抓帧 CPU 回读/带宽瓶颈，并保持 SDR 呈现正确。
- 非目标：完整 HDR 游戏捕获、混合 GL+Vulkan、跨屏拼接、非嵌套 raw input、portal/PipeWire
  零拷贝、非嵌套 XWayland DRI3+fence；这些统一记录在 [post-v1.0.md](post-v1.0.md)。

---

## 1. 范围收敛

| 项 | 决策 | 说明 |
|---|---|---|
| 嵌套 toplevel DMA-BUF 抓帧 | v1.0 后可选 | `ext-image-copy-capture` 的 dmabuf 路径，替代 4K 源的 shm CPU 回读 |
| shm 回退 | 保留 | 格式/修饰符/同步/跨 GPU 不支持时自动回退，不阻塞 |
| SDR 色彩正确性 | 已满足；v1.0 后回归 | 继续选 8-bit BGRA + `SRGB_NONLINEAR`；HDR 桌面实测正常 |
| 10-bit/HDR 宿主输出 | 可选实验 | KWin 支持 `wp_color_manager_v1` v3；若 Vulkan WSI 暴露 HDR colorspace 再做 |
| 完整 HDR 游戏捕获 | 不做 | 嵌套 sway 未 advertise `wp_color_manager_v1`，见 post-v1.0 §6 |
| portal/PipeWire 零拷贝 | 不做 | 继续 `gst-launch` CPU 路径，见 post-v1.0 §7 |
| 混合管线 / 跨屏拼接 / 非嵌套 raw / DRI3 撕裂 | 不做 | 见 post-v1.0 §2–§5 |

**一句话**：M2c 不再是 v1.0 前阶段；本文只保留“嵌套 GPU 抓帧 + SDR 正确性”的未来实现资料。

> 术语说明：`ext-image-copy-capture` 是 copy 协议，compositor 仍会把窗口内容拷进客户端提供的
> dmabuf；这里的“零拷贝”指**去掉 CPU 回读/格式转换/整帧上传**，不是“GPU 一次拷贝都不做”。

---

## 2. 当前基线

- 主路径：`ToplevelCapture` 用 `ext-image-copy-capture` + `ext-foreign-toplevel-list` 抓窗口，
  buffer 走 `wl_shm` + memfd；`ToplevelCapture.DmabufDevice` / `DmabufFormat` 仍是空实现。
- 4K 源 shm 路径实测约 24fps（`开发机归档/M2b-新路线-用户验收手册.md`），瓶颈在 CPU 读回/转换/上传，
  不在 effect；720p/800p 视觉小说源不受影响。
- 嵌套合成器（本地 sway 1.12 + wlroots 0.20）已提供：
  - `zwp_linux_dmabuf_v1` v5；
  - `wp_linux_drm_syncobj_manager_v1` v1；
  - `ext-image-copy-capture-v1` / `ext-foreign-toplevel-list-v1`；
  - `wp_color_representation_manager_v1` v1；
  - **没有** `wp_color_manager_v1`，`HEADLESS-1` 是 SDR、无 HDR metadata。
- KDE 宿主（KWin 6.8）提供 `wp_color_manager_v1` v3（含 PQ/HLG/linear 等），
  但当前 `VulkanBlitter` 固定优先 `B8G8R8A8Unorm` + `SRGB_NONLINEAR`，这是有意的 SDR 保护。
- 既有 spike：DRI3 的 GPU 窗口线性 1-fd 可导入 NVIDIA Vulkan；2-fd/DCC 需回退；fence 未接入
  （见 [zero-copy-spike.md](history/zero-copy-spike.md)）。M2c 的 dmabuf 路径不依赖 DRI3。

---

## 3. 实现方案

### 3.1 抓帧缓冲抽象

把“CPU 像素”和“DMA-BUF”统一成帧描述，避免渲染层关心来源：

```text
NestedFrame
  ├─ Width/Height/Sequence
  ├─ Kind = Shm | Dmabuf
  ├─ Shm: ReadOnlyMemory<byte> pixels, stride, format
  └─ Dmabuf: fd, fourcc, modifier, planeCount, offset[4], stride[4]
             width/height, sync (optional)
```

- `ToplevelCapture` 在 `DmabufDevice` / `DmabufFormat` 回调里记录 compositor 要求的设备与格式；
- 选择顺序：优先可导入 Vulkan 的 32-bit 格式（ARGB8888/XRGB8888/ABGR8888/XBGR8888），
  其次 modifier 0 线性，再其次 NVIDIA 可导入的 tiled modifier；都不行则回退 shm；
- `INestedFrameSource` / `NestedUpscaleSession` 增加 `FrameKind` 分支，渲染层按 kind 选择
  `RenderShm` 或 `RenderDmabuf`。

### 3.2 DMA-BUF 分配与 `wl_buffer`

- 用 **GBM** 在 compositor 指定的 DRM 设备上分配（`dmabuf_device` 的 dev_t 映射到
  `/dev/dri/renderD*`；不要假设 render 节点序号）：
  - `gbm_create_device(fd)`；
  - `gbm_bo_create_with_modifiers(format, width, height, modifiers, count)`；
  - `gbm_bo_get_fd` / `gbm_bo_get_plane_count` / `gbm_bo_get_stride_for_plane` /
    `gbm_bo_get_offset` / `gbm_bo_get_modifier`。
- 用 `zwp_linux_dmabuf_v1.create_buffer`（v5，带 modifier）把 fd/plane 包成 `wl_buffer`；
  每个 buffer 挂 `release` 监听。
- **缓冲池 2–3 个**，避免每帧分配；`release` 或渲染 fence 完成后回收。
- 尺寸/格式变化时销毁池、重建；保留 shm 路径作为即时回退。

### 3.3 Vulkan 导入

- 设备扩展：`VK_EXT_external_memory_dma_buf`、`VK_KHR_image_format_list`、
  `VK_EXT_image_drm_format_modifier`（按可用性启用）。
- 创建：
  - `VkExternalMemoryImageCreateInfo`（`DMA_BUF_BIT_EXT`）；
  - `VkImageDrmFormatModifierExplicitCreateInfoEXT`（modifier + plane offset/rowPitch；
    modifier 0 时可用线性布局）；
  - `VkImportMemoryFdInfoKHR` + `VkMemoryDedicatedAllocateInfo`（按需）；
  - 查询 `VkPhysicalDeviceImageFormatProperties2` 的外部内存特性，不支持就回退。
- 每个 `(width, height, fourcc, modifier)` 缓存 `VkImage`/`VkImageView`/memory；resize 时销毁。
- 通道顺序：DRM `ARGB8888` 在小端内存等价 `B8G8R8A8`，可直接复用现有 BGRA 采样路径；
  其他格式用 `VkComponentMapping` 或在 shader 里 swizzle，避免再做 CPU 转换。

### 3.4 同步

- `ext-image-copy-capture` 的 `ready` 事件语义是“frame is copied, available for reading”
  （协议原文），因此第一版按 `ready` 后直接采样；这是最小实现。
- 若实测出现撕裂/读到旧内容：
  - 先确认 `ext-image-copy-capture` 路径是否暴露可用的同步对象；`wp_linux_drm_syncobj` 主要
    面向 `wl_surface` 提交，不能假定它适用于 capture buffer；
  - 不适用时用 `VkFence` + 双缓冲，保证“上一帧 GPU 采样完成”后再复用 buffer；
  - 仍不达标则回退 shm。
- 不做“为了零拷贝而假设同步正确”；把同步验证列为 spike gate。

### 3.5 渲染路径

- 新增 `VulkanBlitter.RenderDmabuf(NestedDmabufFrame frame, RectI destRect)`：
  - 等待上一帧 fence / 获取 swapchain image；
  - 直接把导入的 `VkImageView` 作为 effect 的 source（Anime4K / Lanczos / bilinear）；
  - 跳过 `CopyToStaging` / `CopyTilesToStaging` / `CopySourceImage`；
  - present。
- **脏矩形**：dmabuf 第一版走全帧（没有 CPU 像素做 tile hash）；720p 源本来就不需要脏矩形优化，
  4K 源先用全帧验证正确性。后续若要恢复脏矩形，用 compositor 的 `damage` 事件或 GPU tile hash，
  不放进 v1.0 门槛。
- 统计：`capture` 包含等待/ready，`upload` 变为 import/bind，新增 `dmabufImport` 计时；
  保留现有 `FrameStats` 字段，避免 UI/CSV 大改。

### 3.6 色彩

- **SDR 保护（现状，保持）**：`VulkanBlitter` 继续优先精确的 `B8G8R8A8Unorm` +
  `VK_COLOR_SPACE_SRGB_NONLINEAR_KHR`；若 surface 只提供线性/HDR colorspace，记录警告并回退
  X11 宿主（或保持上一帧），不要静默用错 colorspace 导致发灰。
- **HDR 宿主输出（可选，不阻塞 v1.0）**：
  - 检测 KWin `wp_color_manager_v1` 与 output image description；
  - 若 Vulkan WSI 暴露 `VK_COLOR_SPACE_HDR10_ST2084_EXT` / 10-bit 格式，建 10-bit swapchain；
  - SDR 源 tone map（sRGB→PQ）或在 HDR 输出上做参考白映射；效果链内部仍是 linear/16f，
    最终 pass 做编码。
  - 若 WSI 不暴露或不稳定，保持 SDR 输出并记录为 post-v1.0。
- **完整 HDR 游戏捕获不做**：嵌套 sway 没有 `wp_color_manager_v1`，headless 输出没有 HDR
  metadata；给嵌套补 HDR 是独立项目，放入 post-v1.0 §6。

### 3.7 回退与开关

- 环境变量/配置：`GEMPRISM_NESTED_DMABUF=0|1`（默认 `1`，失败自动回退）；
- 回退条件：无 `dmabuf_device`/`dmabuf_format`、格式不可导入、Vulkan 导入失败、同步失败、
  跨 GPU、debug 校验层报错；
- 回退必须无黑帧/无崩溃：沿用现有 shm 帧，日志明确 `dmabuf unavailable; falling back to shm`；
- 保留 `--no-zero-copy` 类似的开发开关，便于 A/B。

---

## 4. v1.0 后验收（当前不排期）

1. **4K 源**：嵌套 toplevel dmabuf 路径稳定 ≥50fps（以用户实机为准），CPU capture 显著低于
   shm 基线（目标 <2ms/帧），GPU effect 不回归。
2. **画面正确**：同一静态帧 dmabuf vs shm 截图像素差 `AE=0`（或仅通道顺序差异，需说明）；
   文本/1px 线/黑边无块状伪影。
3. **同步**：连续动态画面无撕裂/旧帧回退；窗口 resize、格式协商变化不崩。
4. **回退**：强制 `dmabuf=0` 或模拟不支持时，shm 路径正常，日志有明确原因。
5. **色彩**：HDR 桌面下 SDR 内容不发灰/不偏色；SDR 桌面不变；10-bit 实验开关不影响默认路径。
6. **回归**：720p/800p 视觉小说捕获/超分/输入/弹窗/退出不回归；`dotnet build` 0 warning /
   0 error，测试通过。
7. **文档**：`m2-plan`/`roadmap`/`compatibility`/`changelog` 更新；post-v1.0 列表不误删。

---

## 5. 实施顺序

1. **Spike（0.5–1 天）**：在 `ToplevelCapture` 里把 `DmabufDevice`/`DmabufFormat` 打印出来，
   确认 wlroots 实际 advertise 的 fourcc/modifier；用 GBM 分配一个 buffer，确认能 attach/ready。
2. **抓帧抽象 + GBM 池**（1 天）：`NestedFrame` 增加 dmabuf 描述；缓冲池、release 回收、
   尺寸变化重建；shm 回退。
3. **Vulkan 导入 + 渲染**（1–1.5 天）：external memory/modifier 导入、缓存、`RenderDmabuf`、
   全帧 effect；A/B 截图。
4. **同步/性能**（0.5–1 天）：`ready` 语义实测；必要时 syncobj/fence；4K 帧率/CPU 记录。
5. **色彩保护 + 可选 HDR**（0.5 天）：SDR 精确选择/回退；HDR WSI 探测（可只记录）。
6. **测试/文档**（0.5 天）：单测、回归、更新文档。

预计 3–5 个工作日（仅在 v1.0 后重启时）；如果 spike 发现 wlroots 不 advertise 可导入的 dmabuf
格式，零拷贝范围需要重估（可能只保留 shm，只做色彩回归）。

---

## 6. 风险

- **wlroots/dmabuf 格式**：可能只 advertise NVIDIA tiled/DCC modifier，Vulkan 导入路径复杂；
  或只支持 2-plane/YUV，当前 effect 链路不兼容 → 回退 shm。
- **跨 GPU**：compositor 在 AMD iGPU、Vulkan 在 NVIDIA 时导入可能失败；按回退处理，不承诺。
- **同步语义**：`ready` 是否等价 GPU 完成需要实测；不达标时 syncobj/fence 的实现成本会上升。
- **GBM/设备匹配**：`dmabuf_device` 是 dev_t，需要正确映射到 render node；映射错会分配失败。
- **性能收益**：4K 源瓶颈可能在 effect/带宽而非 CPU 回读；零拷贝只解决 capture 段，需分段测量，
  不承诺“4K 一定 60fps”。
- **回归面**：dmabuf 路径绕过脏矩形/分块上传，必须保留 shm 路径和开关，避免视觉小说回归。

---

## 7. 参考

- [post-v1.0.md](post-v1.0.md)：v1.0 前不做的功能与已知问题。
- [zero-copy-spike.md](history/zero-copy-spike.md)：DRI3/DMA-BUF 既有结论。
- [compatibility.md](../compatibility.md)：支持矩阵与已知限制。
- [design.md](architecture.md)：抓帧/呈现架构。
- `开发机归档/M2b-新路线-用户验收手册.md`：4K shm 约 24fps 的基线记录。