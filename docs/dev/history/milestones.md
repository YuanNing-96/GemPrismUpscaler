# 里程碑摘要（M0–M3）

## M0 · 最小可用闭环

**目标**：在 KDE Wayland + XWayland 下，对窗口化视觉小说完成捕获 → 超分 → 全屏呈现 → 输入转发 → 安全退出。

**结果**：

- X11/XWayland 捕获（XShm + XDamage）、4K override-redirect overlay、Vulkan 呈现。
- 鼠标坐标反算 + `XSendEvent` 转发；补发进入/离开事件；EWMH 焦点。
- 弹窗让位、最小化收起、游戏关闭退出、热键。
- Anime4K S 2× CNN + Lanczos-2 收尾；双线性回退。
- 真实游戏验收通过（文档中匿名化为示例视觉小说 A / B）。

**关键教训**：overlay 必须按物理像素布局；“点击穿透”不够，必须转发；混合 GL+Vulkan 不应阻塞最小闭环。

详见 [M0 可行性评估](m0-feasibility-review.md)。

## M1 · 体验、效率与扩展

### M1a（v0.2.0）

- 单实例 Unix socket、退出审计、多显示器基础、窗口选择。
- AGPL 许可、README/兼容性/backlog 文档。

### M1b（v0.2.1）

- 分段指标、GPU timestamp、NVML、CSV、p95。
- Anime4K S/M/L manifest 驱动 N-pass。
- 64px 脏矩形 + CNN halo + 持久合成图。
- 零拷贝 DRI3/DMA-BUF spike（有条件 go，未进默认链路）。
- 4K/60 真实游戏验收。

详见 [M1b 测试报告](m1b-test-report.md)。

### M1c（v0.3.0）

- 设置持久化、托盘、Profile、自定义 effect（TOML + SPIR-V）。
- Anime4K VL（18 pass / 19 纹理 / 176 B push constant）。
- 动画明确不做。

详见 [M1c 测试报告](m1c-test-report.md)。

## M2 · 平台扩展

### M2a（v0.4.0）

- 后端抽象；Wayland `layer-shell` overlay + Vulkan WSI。
- XWayland 捕获 + Wayland overlay + 输入转发。
- 原生 Wayland portal ScreenCast + PipeWire；GlobalShortcuts。
- 多屏/分数缩放/热插拔基础。

详见 [M2a 开发记录](m2a-development.md)、[M2a 崩溃排查](m2a-crash-investigation.md)。

### M2b（v0.5.0）

- 私有 sway + 单输出 `HEADLESS-1`，按显示器物理分辨率/刷新率创建。
- `ext-image-copy-capture` 按 toplevel 抓帧（默认），`GEMPRISM_TOPLEVEL_CAPTURE=0` 回退输出裁剪。
- 原生 Wayland 宿主窗口（`xdg_toplevel`、装饰、viewport、分数缩放、指针 warp）。
- 多宿主窗口 + 共享虚拟输出；对话框、窗口尺寸变化、关闭转发。
- Linux 输入：虚拟指针/键盘；X11 光标跳转。
- 大量稳定性修复：SIGSEGV、拉伸首帧、黑帧、Wine CSD、光标形状。

详见 [单输出方案](m2b-single-output.md)、[验收问题分组](m2b-acceptance-groups.md)、[SIGSEGV 修复](m2b-sigsegv-dialog.md)。

### M2c（推迟）

原计划的嵌套 DMA-BUF 零拷贝、SDR 色彩准确性不阻塞 v1.0，推迟为 v1.0 后可选优化。见 [M2c 计划](../m2c-plan.md)。

## M3 · 打磨与发布（v1.0.0）

- GUI 收敛为三页签；嵌套为默认启动方式；暂停/停止语义明确。
- CLI：`--doctor`、`--config-check`、`--diagnose`、`--wayland-*`；退出码文档化。
- 诊断日志：session header、5 MB × 3 轮转、脱敏诊断包、崩溃快照。
- 文档迁移到 `docs/`；用户/开发/历史分层；示例配置与脚本。
- 发布物：解压即用的压缩包（自包含运行时 + 私有 sway）。

## 版本对应

| 版本 | 阶段 |
|---|---|
| 0.1.x | M0 |
| 0.2.0 | M1a |
| 0.2.1 | M1b |
| 0.3.0 | M1c |
| 0.4.x | M2a / 验收快照 |
| 0.5.0 | M2b |
| 1.0.0 | M2b 主路径 + M3 |