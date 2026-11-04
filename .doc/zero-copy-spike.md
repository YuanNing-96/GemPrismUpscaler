# 零拷贝可行性 spike（M1b）

## 1. 问题

M1a/M1b 的捕获路径是：

```text
XShmGetImage(窗口) → 共享内存 → 分块哈希/上传 → Vulkan
```

能否跳过 CPU 回读，把 X11 窗口内容直接作为 Vulkan 可采样的 DMA-BUF？

X11 没有“窗口内容 → DMA-BUF”的标准高层 API；可行路径是：

```text
XCompositeNameWindowPixmap(window) → Pixmap
  → DRI3BuffersFromPixmap(pixmap)
  → DMA-BUF fd(s) + modifier
  → Vulkan VkImportMemoryFdInfoKHR + DRM format modifier image
```

## 2. 探针与命令

源码与构建命令见 [`tools/zero-copy/`](../tools/zero-copy/README.md)。核心命令：

```bash
./tools/zero-copy/dri3-export 0x<window-id>
./tools/zero-copy/dri3-vulkan-import 0x<window-id>
```

环境：KDE Plasma 6 Wayland + XWayland，NVIDIA RTX 4060 Laptop，DRI3 1.2，Composite 0.4，
Vulkan 1.4（NVIDIA）。探针只做格式/导入检查，不包含 fence 同步与端到端画面验证。

## 3. 结果

### 3.1 软件 X11 窗口（`tools/testpattern --static`，1280×720）

```text
dri3 version=1.2
NameWindowPixmap OK
BuffersFromPixmap OK nfd=2 1280x720 depth=24 bpp=32
  modifier=0x20000000056bb03
  buf0 fd=... size=3956736
  buf1 fd=... size=3956736
BufferFromPixmap FAILED error=4
```

- 2 个 fd、NVIDIA DCC/block-linear modifier。
- 旧的单 fd `DRI3BufferFromPixmap` 失败。
- 简单的“1 fd + 线性” Vulkan 导入路径不适用；若要支持，需要理解 DCC/metadata plane 的导入方式，
  且 NVIDIA 的 Vulkan 外部内存路径未必暴露该 plane。

### 3.2 GPU 窗口（`vkcube --wsi xcb`，500×500）

```text
DRI3 export: nfd=1 500x500 depth=24 bpp=32 modifier=0x0
BufferFromPixmap OK nfd=1 stride=2048
fd memory type bits=0x9
vkCreateImage: 0
vkAllocateMemory(import fd): 0
vkBindImageMemory: 0 => IMPORT OK
```

- 1 个 fd、**线性 modifier 0**、stride 2048。
- NVIDIA Vulkan 设备（设备 1）上创建 `DRM_FORMAT_MODIFIER` 图像、导入 fd、绑定内存全部成功。
- `VkPhysicalDeviceImageFormatProperties2` 的外部内存特性为 0x6（可导入/专用分配）。

## 4. 结论

**go（有条件）**：

1. **可导出**：`XCompositeNameWindowPixmap` + `DRI3BuffersFromPixmap` 在本机可用；GPU 窗口
   （vkcube）给出 1 fd、线性布局，NVIDIA Vulkan 可导入。
2. **需要运行时分支**：软件/部分窗口给出 2 fd + DCC modifier，简单线性导入不适用；必须做
   `nfd==1 && modifier==0` 检查，不满足就回退 XShm。
3. **同步未验证**：DRI3 的 fence (`FDFromFence`/`FenceFromFD`) 与 Vulkan external semaphore
   尚未接入；没有 fence 就读取合成器 buffer 可能撕裂/读到半帧。
4. **跨 GPU/真实游戏未验证**：vkcube 选中 NVIDIA；如果合成器/窗口 buffer 在 AMD iGPU，
   跨 GPU DMA-BUF 的格式/性能/同步都需要重新测。Wine/D3D、原生 GL、视频播放器也要分别测。
5. **不建议在 M1b 默认链路启用**：M1b 已交付 XShm + 分块哈希/上传，稳定且可回退；零拷贝作为
   M2（尤其 Wayland/PipeWire 后端）的候选路径继续验证。若后续启用，应保留 XShm 回退和
   `--no-zero-copy` 开关。

## 5. 后续工作

- DRI3 fence → `VkImportSemaphoreFdKHR`（binary or timeline）同步。
- 处理 `DRI3BuffersFromPixmap` 的 stride：线性路径可用 legacy `BufferFromPixmap` 的 stride，
  或 `fd size / height`；非线性的 plane layout 需要 `VkImageDrmFormatModifierExplicitCreateInfoEXT`
  的完整 plane 信息。
- 在真实游戏（Vulkan/D3D/Wine/GL）和双 GPU 环境复测。
- 与分块哈希的取舍：零拷贝避免 CPU 回读，但分块哈希需要 CPU 数据；可改为 GPU 侧 tile hash
  或依赖 XDamage/合成器 damage。
