# 宝石棱镜 · GemPrismUpscaler 路线图

每个阶段以"可验证的最小闭环"为单位，不追求功能齐全再验收。

---

## M0 · 最小可用闭环

**目标**：在目标环境下，对窗口化的视觉小说完成一次从捕获到超分的完整链路，且能安全退出。

### 当前进度（2026-11-02 13:20）

| 项 | 状态 |
|---|---|
| M0a：捕获/4K overlay/输入/热键/弹窗/UI/60fps | ✅ 已跑通（含验收整改） |
| M0b：Anime4K 2× + Lanczos 收尾 | ✅ v0.1.2 |
| M0b：FSR1 / 混合管线 | ⏸ 决定不做（全 Vulkan；M2 再评估） |
| M0 混合管线实验 | ⏸ 决定不做（见下方决策结果） |
| 真实游戏验收（腐姬 / 秽翼的尤斯蒂娅） | ✅ 2026-11-02 复测通过 |

**M0 结论（2026-11-02）**：M0a、M0b 和真实游戏验收全部通过，M0 完成。

M0a 的 60fps 是合成 1280×720 测试窗口的结果，不等于所有游戏的最终性能。M0b 的超分效果用 Anime4K `Upscale_CNN_x2_S` + Lanczos-2，固定 2× CNN 后再由 Lanczos 收尾。

混合管线未做时间盒实验：按“全 Vulkan、保留 `EffectMode`/backend 扩展点”的降级方案执行，M2 再评估。

### 范围

| 模块 | 内容 |
|---|---|
| 捕获 | X11 / XWayland 窗口，XShm 直读 + XDamage 帧级跳过 |
| 超分 | M0a：Vulkan 双线性；M0b：Anime4K S 2×（5 compute pass）+ Lanczos-2 graphics pass |
| 呈现 | override-redirect 全屏窗口，物理尺寸 3840×2160（X11 root 尺寸） |
| 输入 | overlay 接收鼠标 + 坐标映射 + `XSendEvent` 转发 + 全局热键 |
| 弹窗 | 200ms 窗口轮询（viewable 过滤）+ 让位 |
| 界面 | Avalonia 最小配置页（选择窗口、开关、退出） |
| 混合管线 | **时间盒实验**，见下方决策点 |

### 验收标准

1. 1280×720 窗口的游戏，开启超分后全屏显示，画面比例正确。
2. 在超分画面的任意位置点击都能推进游戏；键盘操作正常；无卡顿。
3. 游戏弹出确认对话框时，超分画面自动让位，对话框可见可操作。
4. 关闭超分或退出游戏后，桌面恢复原状，无残留窗口。
5. 画面连续变化段平均 ≥60fps；静止段不重复捕获/呈现、GPU 接近空闲。腐姬若源上限 <60，按“达到源上限且不额外掉帧”验收。
6. 在 100% 与 150% 系统缩放下均正确。

### 混合管线决策点

**决策结果（2026-11-02）**：M0 不实现混合管线，采用全 Vulkan；`EffectMode` 仅区分 Bilinear/Anime4K，effect backend 扩展点留到 M2。下面的 2 周时间盒方案作为历史背景保留。

M0a 闭环跑通后，用 2 周时间盒尝试 Vulkan + OpenGL 两条 effect 路径，通过 DMA-BUF 交接。

关键前提：GL 必须与 Vulkan 在同一块 GPU 上。本机 XWayland GLX 在 AMD，Vulkan 在 NVIDIA，因此必须用 **headless EGL surfaceless context（NVIDIA）**，不能直接用 GLX。已验证 GL 纹理可导出 DMA-BUF；Vulkan 导入与同步仍需 spike。

**降级条件**（满足任一即切换为全 Vulkan）：

- 时间盒耗尽仍未跑通稳定链路
- 跨 API 路径在目标驱动上出现不可复现的同步问题
- 需要为混合路径引入超过 300 行的额外状态管理

**降级方案**：所有 GLSL 编译为 SPIR-V，全部在 Vulkan 中执行。接口层保持 `backend` 字段不变，M2 再补实现，不返工。

### 明确不做

- 原生 Wayland 窗口
- 多显示器
- HDR
- FPS/鼠标视角/raw input 游戏
- 动画
- 自动 Profile 匹配

---

## M1 · 体验与效率

**目标**：从"能用"到"愿意日常使用"。

### 范围

| 模块 | 内容 |
|---|---|
| 脏矩形 | 只重算变化区域；XDamage 在 XWayland 常报全窗口，需分块哈希 |
| 动画 | 进出超分的缩放过渡 |
| Profile | 按 exe 名 / 窗口类名自动套用配置 |
| 零拷贝 | DRI3 + DMA-BUF，替换 XShm 回读 |
| effect 扩展 | 用户可放置自定义 GLSL，配置文件组合 |
| 算法 | 补齐 FSR1、NIS、Lanczos、Bicubic |
| 托盘 | 常驻托盘，快速切换 |

### 验收标准

1. 静止对话画面时 GPU 占用低于 2%
2. 进出动画无撕裂、无黑帧
3. 新增 effect 只需放置文件 + 改 TOML，无需重新编译
4. 启动游戏后自动识别并套用配置

---

## M2 · 平台扩展

**目标**：覆盖 Wayland 原生与更复杂的显示环境。

### 范围

| 模块 | 内容 |
|---|---|
| Wayland 捕获 | xdg-desktop-portal + PipeWire + DMA-BUF，支持 `restore_token` 持久授权 |
| Wayland 呈现 | wlr-layer-shell overlay + 空 input region + `keyboard_interactivity=none`（KWin 6.8 已提供 layer-shell v5） |
| 混合管线 | 补齐 M0 降级时跳过的 GL 执行路径 |
| 多显示器 | 覆盖窗口定位到游戏所在屏幕，处理不同 DPI |
| 色彩管理 | linear 工作空间，可选 HDR 直通 |

### 验收标准

1. KDE 与 wlroots 系合成器下，原生 Wayland 窗口可捕获并超分
2. Wayland 下输入完全透明，无光标跳变
3. 多显示器场景下覆盖窗口位置正确

### 已知限制

- GNOME（Mutter）不支持 `zwlr_layer_shell_v1`，需要单独方案
- portal 首次授权需要用户交互，之后靠 token 免打扰

---

## M3 · 打磨与分发

**目标**：可被普通用户安装使用。

### 范围

- Flatpak / AUR 打包
- 崩溃恢复与诊断日志
- 性能分析工具与基准套件
- 完整文档与示例配置
- 国际化

---

## 里程碑依赖

```
M0 ──→ M1 ──→ M2 ──→ M3
 │
 └─ 混合管线决策点：保留 or 降级
```

M1 的脏矩形依赖 M0 的捕获抽象稳定；M2 的 Wayland 后端复用 M0 定义的 `CaptureBackend` / `PresentBackend` / `InputBackend` 接口，接口在 M0 就必须定下来。

---

## 版本命名

| 版本 | 对应 |
|---|---|
| 0.1.0 / 0.1.1 | M0a（捕获/呈现/输入/弹窗/UI/生命周期） |
| 0.1.2 | M0b（Anime4K 2× CNN + Lanczos-2） |
| 0.2.x | M1 |
| 0.3.x | M2 |
| 1.0.0 | M3 完成，接口稳定 |
