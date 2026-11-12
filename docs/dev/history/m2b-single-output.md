> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M2b 新路线：单输出 + 按窗口抓取（KDE 对齐）

- 分支：`m2b-single-output`（基于 `97dbbf2`）
- 开始日期：2026-11-10
- 目标：在 v1.0 前把"嵌套合成器"从"模拟 KDE"改成"只做 KDE 不会做的部分"：
  单块稳定虚拟屏 + 按 toplevel 抓原始 buffer；客户端可见世界尽量与 KDE 一致。

## 1. 范围决定（用户 2026-11-10）

- **GUI/CLI 暂不要求**：开发期以能验证效果为主，验收通过后再重构 GUI/CLI。
- **LAUNCH-WAYLAND（示例视觉小说 C）必须解决，且在 v1.0 前**：作为"对齐 KDE"专项的一部分，
  不再以"建议 X11"归档。
- **全屏**：v1.0 发布时可以不支持；但后续要作为对齐 KDE 的一环补上（不是永久放弃）。

## 2. 已完成（S1 + S2 首版）

### S1 协议验证（独立 C spike，`开发机归档/m2b-spike/toplevel-capture/`）

- sway 1.12 已 advertise `ext-image-copy-capture` + `ext-foreign-toplevel-list` +
  `ext-foreign-toplevel-image-capture-source`；遮挡窗口下抓取仍可得目标窗口原始内容（PASS）。
- 格式：`shm_format` 只广告 `BG24`（BGR888，24bpp），wl_shm 的内存字节序是 R,G,B；
  `dmabuf_format` 广告 `XR24`/`AR24` + 13 个 modifier。
- 吞吐（shm）：4K ~24–27fps、1080p ~83fps、720p ~113fps（144fps 动画源）。

### S2 首版（产品代码）

- 新增 `ToplevelCapture`（NWayland `ExtImageCopyCaptureV1` 等），实现 `INestedFrameSource`。
- `NestedUpscaleSession` 增加 `GEMPRISM_TOPLEVEL_CAPTURE=1` 模式：
  - 不创建 `SwayOutputPool`，只用 `HEADLESS-1`（monitor 物理分辨率）；
  - 每个窗口按 app_id/title 找 foreign-toplevel，建 capture session；
  - 不再 crop/origin 换算；输入映射用 **窗口 rect**（合成器显示区域）而不是 buffer 尺寸；
  - `--nested-probe`：不建 host，直接抓帧写 PPM，用于协议/格式/吞吐验证。
- 实测（testwin 1280×720，60fps 动画，4K 输出）：
  - 会话初始化：`nested single-output/toplevel-capture: output=3840x2160`；
  - `toplevel capture: 1280x720 format=Xrgb8888 bpp=4`；
  - host 正常创建并呈现，`captured≈presented≈40fps`，capture≈0.6ms；
  - probe 彩色校验：纯红/纯蓝/纯绿窗口颜色正确（BGRA 转换正确）。

## 3. 已知限制 / 待验证

1. **窗口匹配**：目前按 app_id + title 精确匹配。同一 app_id 多窗口（对话框、标题带尾随空格）
   需要更稳的键——候选：foreign-toplevel `identifier` + sway PID/顺序，或按 PID 过滤。
2. **4K shm 性能**：4K 源 ~24fps，需 dmabuf 路径（导入 Vulkan）；1080p 及以下 shm 可接受。
3. **buffer vs 窗口 rect**：客户端不按 configure 尺寸绘制时（如示例视觉小说 A 800×600），capture 返回
   buffer 尺寸，host 按 buffer 显示；输入按窗口 rect 映射。需要在真机复测中确认手感。
4. **焦点/激活语义**：KDE 在窗口 map 时立刻给 `wl_keyboard.enter`；nested 仍需逐项对齐
   （这与示例视觉小说 C subsurface 生命周期直接相关）。
5. **全屏**：单输出下 fullscreen 的 rect/状态仍需与 KDE 对齐（v1.0 不阻塞）。

## 4. 下一步

- [ ] dmabuf 抓取 → Vulkan 导入（4K 60fps 的前提），shm 保留回退
- [ ] 单输出下的输入/焦点/全屏语义逐项对齐（parity 清单）
- [ ] 真机：示例视觉小说 B （Wine Wayland 指针锁）、示例视觉小说 A（窗口↔全屏、CSD）复测
- [ ] **示例视觉小说 C （dwproton）专项**：按 KDE 的激活/键盘 enter/visibility 时序复现并修复
- [ ] KDE 全屏 A/B，完成 W-HANG 归因
- [ ] 验收通过后再重构 GUI/CLI

## 5. 开发入口

```bash
# 协议/格式/吞吐探针（不建 host）
GEMPRISM_TOPLEVEL_CAPTURE=1 TESTWIN_FPS=60 \
  ./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler \
  --nested-probe --monitor HDMI-A-1 --frames 120 --out /tmp/probe.ppm -- \
  开发机归档/m2b-spike/toplevel-capture/testwin spike-blue 0xff0000ff 1280 720

# 单输出 + toplevel 抓取 + host/超分链路
GEMPRISM_TOPLEVEL_CAPTURE=1 TESTWIN_FPS=60 \
  ./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler \
  --nested --monitor HDMI-A-1 --effect anime4k-s -- \
  开发机归档/m2b-spike/toplevel-capture/testwin spike-red 0xffff0000 1280 720
```