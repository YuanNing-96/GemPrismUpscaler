# 零拷贝可行性探针（M1b spike）

这两个程序只用于验证 X11 窗口 pixmap → DRI3 DMA-BUF → Vulkan 导入的可行性，**不是生产链路**。

## 1. 导出：窗口 pixmap → DMA-BUF

```bash
cc -O2 -o dri3-export dri3-export.c \
  $(pkg-config --cflags --libs xcb xcb-dri3 xcb-composite libdrm)
./dri3-export 0x<window-id>
```

它会打印 DRI3/Composite 版本、支持的 modifier、`XCompositeNameWindowPixmap` 结果，以及
`DRI3BuffersFromPixmap` / `DRI3BufferFromPixmap` 返回的 fd、尺寸、stride、modifier。

## 2. 导入：线性 DMA-BUF → Vulkan image

```bash
cc -O2 -o dri3-vulkan-import dri3-vulkan-import.c \
  $(pkg-config --cflags --libs xcb xcb-dri3 xcb-composite vulkan)
./dri3-vulkan-import 0x<window-id>
```

只处理 `nfd==1 && modifier==0` 的线性 buffer；创建 `VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT`
图像，用 `VkImportMemoryFdInfoKHR` 导入 fd，并 `vkBindImageMemory`。输出 `IMPORT OK` 表示绑定成功。

## 开发机结果（2026-11-05）

- `vkcube --wsi xcb` 的 500×500 窗口：1 fd、modifier 0、stride 2048；NVIDIA Vulkan 导入成功。
- `tools/testpattern --static` 的软件窗口：2 fd、NVIDIA DCC modifier `0x20000000056bb03`；旧的
  `DRI3BufferFromPixmap` 失败；线性导入路径不适用。

结论与限制见 [.doc/zero-copy-spike.md](../../.doc/zero-copy-spike.md)。
