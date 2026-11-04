# 宝石棱镜 · GemPrismUpscaler 路线图

每个阶段以“可验证的最小闭环”为单位，不追求功能齐全再验收。阶段内不再设置时间盒，只保留范围、依赖和验收标准。

---

## M0 · 最小可用闭环（已完成）

**目标**：在目标环境下，对窗口化的视觉小说完成一次从捕获到超分的完整链路，且能安全退出。

| 项 | 状态 |
|---|---|
| M0a：X11/XWayland 捕获、4K overlay、输入转发、热键、弹窗让位、生命周期、Avalonia UI | ✅ |
| M0b：Anime4K S 2× CNN + Lanczos-2 收尾 | ✅ v0.1.2 |
| M0b：双线性回退（M0a 路径） | ✅ `--effect bilinear` |
| 真实游戏验收（腐姬 / 秽翼的尤斯蒂娅） | ✅ 2026-11-02 |
| 混合 GL+Vulkan 管线 | ⏸ 决定不做；M2 再评估 |

M0 的端到端链路：

```text
X11/XWayland 游戏窗口
  └─ XShmGetImage（XDamage 帧级跳过）
       └─ staging buffer → BGRA8 source image
            ├─ Anime4K S：5 个 compute pass（rgba16f）
            └─ Lanczos-2：graphics pass → swapchain（内容矩形）
                 └─ override-redirect overlay
                      └─ 鼠标 XSendEvent 转发 + EWMH 焦点 + 热键/弹窗/生命周期
```

---

## M1 · 体验、效率与扩展

**目标**：从“能用”到“愿意日常使用”。

### M1a · 体验与可退出（v0.2.0，2026-11-04 已验收）

| 模块 | 内容 |
|---|---|
| 单实例 | `$XDG_RUNTIME_DIR` Unix socket；第二个 GUI 只提示错误、不激活已有窗口；第二个 CLI 报错；`--status` |
| 退出审计 | 窗口关闭、退出按钮、`SIGTERM`/`SIGINT`、游戏关闭、引擎异常；日志记录 PID/原因 |
| 多显示器基础 | XRandR 1.5 枚举；自动跟随游戏窗口或手动选屏；每屏独立 overlay/swapchain；输入坐标按屏原点换算；跨屏移动自动重建 |
| 窗口选择 | 过滤面板/桌面/本进程窗口；显示 PID、类名、分辨率、显示器；刷新保持选择 |
| 文档/许可 | README 支持矩阵、兼容性文档、AGPL-3.0-or-later、backlog、0.2.0 |

**验收标准**：

1. 第二个 GUI 实例只弹出“已在运行”提示并退出；不创建第二个 overlay，不激活已有窗口。
2. 窗口 X / 退出 / `SIGTERM` / 游戏关闭 / 引擎异常后，进程及时退出，无残留 overlay。
3. 游戏在任意一块显示器上时，overlay 只覆盖该屏；fit/stretch 比例正确；鼠标映射准确。
4. 自动模式下窗口跨屏移动，overlay 在 1 秒内跟随；手动模式固定不动。
5. M0a/M0b 的弹窗、最小化、焦点、热键、关闭游戏、静止画面不重复呈现全部回归。
6. 构建 0 warning / 0 error；测试通过。

### M1b · 效率与 Anime4K S/M/L（v0.2.1，开发完成，待外接屏验收）

| 模块 | 状态 | 内容 |
|---|---|---|
| 指标 | ✅ | capture / diff / upload / effect / present 分段、Vulkan timestamp、NVML GPU/显存、进程 CPU、p95、可选 CSV |
| 算法 | ✅ | manifest 驱动 N-pass；Anime4K **S/M/L**（5/9/10 pass）；默认 S；UI/CLI 切换；4K 数据见验收记录 |
| 脏矩形 | ✅ | 64px 分块哈希 + CNN halo；只上传/重算变化区域；持久 4K 合成图；≥50% 变化回退整帧 |
| 上传/呈现 | ✅ | 主路径直接从 shm 指针哈希/分块上传，去掉整帧托管拷贝；持久 staging；全帧/局部统一走持久合成图 |
| 零拷贝 | ✅ spike | DRI3+DMA-BUF 探针：GPU 窗口线性 1-fd 可导出并在 NVIDIA Vulkan 导入成功；软件窗口 2-fd/DCC 需回退；**不进默认链路** |

**验收标准**：

1. 静止对话画面 GPU 占用低于 2%：开发侧 `static` 0 capture/0 present、`repaint` captured≈60/presented=0、`gpu=n/a`、整卡 0%；外接屏复测待做。
2. 脏矩形开启后画面无块状伪影，文本/1px 线正确；动态帧率不低于 M1a：开发侧 `dirty` 与 `--no-dirty-rect` 最终截图 `AE=0`；`animate` 两者均 47.9 fps（当前合成器上限）。
3. S/M/L 可切换；默认 S；M/L 的实测帧率、显存、GPU 占用记录在案：开发侧 4K override 数据 S 0.65 ms/555 MiB、M 1.3–1.5 ms/585 MiB、L 2.1–2.3 ms/593 MiB（gpuEffect）；详见 `.agent/M1b验收记录.md`。
4. 零拷贝 spike 有明确 go/no-go 结论，不把未验证路径放进默认链路：结论为**有条件 go**，详见 [zero-copy-spike.md](zero-copy-spike.md)。

### M1c · 设置、托盘、动画与扩展

| 模块 | 内容 |
|---|---|
| 设置界面 | 热键、日志路径、第二实例行为（仅提示/激活已有）、默认显示器、默认效果、托盘关闭行为；输入模式占位（当前 2D / FPS-raw input，后者先置灰） |
| 托盘 | 常驻托盘；开始/停止/显示隐藏/退出；关闭到托盘可选 |
| 动画 | 仅对 GPU 输出矩形做进入/退出插值，overlay 本身全屏不动 |
| Profile | TOML 配置；按 exe/窗口类自动套用；记住上次选择 |
| effect 扩展 | TOML 描述 pass；自定义 shader 以预编译 SPIR-V + manifest 分发，运行时不强制依赖 glslang |

**验收标准**：

1. 设置项可持久化并在重启后生效。
2. 托盘可开始/停止/退出；关闭到托盘与直接退出语义明确。
3. 进出动画无撕裂、无黑帧。
4. 新增 effect 只需放置文件 + 改 TOML，无需重新编译应用。

---

## M2 · 平台扩展

| 模块 | 内容 |
|---|---|
| Wayland 捕获 | xdg-desktop-portal + PipeWire + DMA-BUF，支持 `restore_token` 持久授权 |
| Wayland 呈现 | wlr-layer-shell overlay + 空 input region + `keyboard_interactivity=none` |
| 多显示器完整版 | 热插拔、每屏 DPI、负坐标、跨屏拼接 |
| 色彩管理 | linear 工作空间，可选 HDR 直通 |
| 混合管线 | 重新评估 GL+Vulkan 分 pass 执行 |
| 输入 | FPS/鼠标视角/raw input 的完整方案（uinput / libei 等） |

**验收标准**：

1. KDE 与 wlroots 系合成器下，原生 Wayland 窗口可捕获并超分。
2. Wayland 下输入完全透明，无光标跳变。
3. 多显示器热插拔与不同 DPI 下覆盖窗口位置正确。
4. FPS/raw input 方案有明确支持范围，不静默失效。

### 已知限制

- GNOME（Mutter）不支持 `zwlr_layer_shell_v1`，需要单独方案。
- portal 首次授权需要用户交互，之后靠 token 免打扰。
- X11 下窗口 pixmap 到 DMA-BUF 的零拷贝没有通用标准路径，可能只能在 Wayland 后端实现。

---

## M3 · 打磨与分发

- Flatpak / AUR 打包
- 崩溃恢复与诊断日志
- 性能分析工具与基准套件
- 完整文档与示例配置
- 国际化

---

## 里程碑依赖

```text
M0 ──→ M1a ──→ M1b ──→ M1c ──→ M2 ──→ M3
```

M1b 的脏矩形依赖 M1a 的多屏 overlay 抽象；M1c 的 Profile/设置依赖 M1a 的实例与显示器模型；M2 的 Wayland 后端复用 M0 定义的 `CaptureBackend` / `PresentBackend` / `InputBackend` 接口。

未排期功能、设置界面需求和明确不做的项见 [backlog.md](backlog.md)。

---

## 版本命名

| 版本 | 对应 |
|---|---|
| 0.1.0 / 0.1.1 | M0a（捕获/呈现/输入/弹窗/UI/生命周期） |
| 0.1.2 | M0b（Anime4K S 2× CNN + Lanczos-2） |
| 0.2.0 | M1a（单实例/退出审计/多显示器基础） |
| 0.2.1 | M1b（指标/S/M/L/脏矩形/上传优化/零拷贝 spike） |
| 0.2.x | M1c |
| 0.3.x | M2 |
| 1.0.0 | M3 完成，接口稳定 |
