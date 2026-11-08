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

### M1b · 效率与 Anime4K S/M/L（v0.2.1，已完成 4K 外接屏验收）

| 模块 | 状态 | 内容 |
|---|---|---|
| 指标 | ✅ | capture / diff / upload / effect / present 分段、Vulkan timestamp、NVML GPU/显存、进程 CPU、p95、可选 CSV |
| 算法 | ✅ | manifest 驱动 N-pass；Anime4K **S/M/L**（5/9/10 pass）；默认 S；UI/CLI 切换；4K 数据见验收记录 |
| 脏矩形 | ✅ | 64px 分块哈希 + CNN halo；只上传/重算变化区域；持久 4K 合成图；≥50% 变化回退整帧 |
| 上传/呈现 | ✅ | 主路径直接从 shm 指针哈希/分块上传，去掉整帧托管拷贝；持久 staging；全帧/局部统一走持久合成图 |
| 零拷贝 | ✅ spike | DRI3+DMA-BUF 探针：GPU 窗口线性 1-fd 可导出并在 NVIDIA Vulkan 导入成功；软件窗口 2-fd/DCC 需回退；**不进默认链路** |

**验收标准**：

1. 静止对话画面 GPU 占用低于 2%：开发侧 `static` 0 capture/0 present、`repaint` captured≈60/presented=0、`gpu=n/a`、整卡 0%；外接 4K 复测已完成。
2. 脏矩形开启后画面无块状伪影，文本/1px 线正确；动态帧率不低于 M1a：开发侧 `dirty` 与 `--no-dirty-rect` 最终截图 `AE=0`；4K A/B 37.81 vs 37.77 fps（差 0.79%，软件 testpattern）；真实游戏 4K L 59.5–60.7 fps。
3. S/M/L 可切换；默认 S；4K 实测已记录：真实游戏 L 59.5–60.7 fps、gpuEffect 1.1–2.0 ms；S/M/L 全帧 gpuEffect 0.36/0.79/1.11 ms（testpattern）；详见 `.agent/M1b验收记录.md`。
4. 零拷贝 spike 有明确 go/no-go 结论，不把未验证路径放进默认链路：结论为**有条件 go**，详见 [zero-copy-spike.md](zero-copy-spike.md)。

### M1c · 设置、托盘、Profile 与 effect 扩展（v0.3.0，已完成主要功能；动画留后续）

| 模块 | 状态 | 内容 |
|---|---|---|
| 设置界面 | ✅ | 热键、日志路径、第二实例行为（仅提示/激活已有）、默认显示器、默认效果、缩放、托盘关闭行为、跟随焦点、窗口自动刷新、输入模式占位（FPS/raw input 置灰）、开发模式 |
| 托盘 | ✅ | 常驻托盘；开始/停止/显示隐藏/退出；关闭到托盘可选；不可用时降级 |
| 动画 | ⏸ | M1c 不做；仅对 GPU 输出矩形做进入/退出插值，overlay 本身全屏不动；留 v1.0 前评估 |
| Profile | ✅ | TOML 配置；按 exe/窗口类/Wine cmdline 自动套用；先匹配先赢；记住上次选择 |
| effect 扩展 | ✅ | TOML 描述 compute pass；自定义 shader 以预编译 SPIR-V 分发，运行时不依赖 glslang；ABI 见 `.doc/effects.md` |
| Anime4K VL | ✅ | 18 pass / 19 纹理 / 176B push constant；设备能力不足时不启用；帧率不作为验收门槛 |
| 错误/诊断 | ✅ | 功能级错误普通模式提示；非功能错误开发模式提示；分级日志、ErrorBus、Vulkan 校验层门控 |

**验收标准**：

1. 设置项可持久化并在重启后生效。
2. 托盘可开始/停止/退出；关闭到托盘与直接退出语义明确；托盘不可用时应用仍可正常使用。
3. 进出动画无撕裂、无黑帧。（**M1c 不做动画，留 v1.0 前评估**）
4. 新增 effect 只需放置文件 + 改 TOML，无需重新编译应用。
5. VL 能启动、画面正确、无 Vulkan 校验错误；帧率只记录，不作为门槛。
6. M1c 按约定只做主要功能冒烟；完整回归/边界兼容性留 v1.0。

---

## M2 · 平台扩展

M2 拆成三段：**M2a 平台核心 → M2b 嵌套合成器 → M2c 色彩/性能**。完整范围、决策、实现与验收见 [m2-plan.md](m2-plan.md)。

### M2a · Wayland 平台核心（v0.4.0，核心 + portal + GlobalShortcuts 已实现；正式验收待补）

| 模块 | 内容 |
|---|---|
| 后端抽象 | 最小 `ICaptureSource` / `IPresentTarget` / `IInputRoute` / `IOutputSet`；X11 保持原路径 |
| Wayland 呈现 | `zwlr_layer_shell_v1` overlay、Vulkan Wayland WSI、分数缩放、每屏 swapchain、热插拔 |
| XWayland 混合 | X11 捕获 + Wayland overlay + `wl_pointer` → `XSendEvent` 转发；保持 `--window` 体验 |
| 原生 Wayland 捕获 | portal `ScreenCast` v6 + PipeWire（memfd 优先）+ `restore_token`；全屏源输入穿透 |
| 多显示器 | Wayland 输出枚举/热插拔；4K/150% + 竖屏/100%；XWayland 按 XRandR 名称匹配 |
| 热键 | portal `GlobalShortcuts` v2；失败时明确提示并回退 `XGrabKey` best-effort |
| CLI/文档 | `--backend`、`--capture`、`--input`、`--portal-window`；`.doc/cli.md` |

**当前状态**：Wayland layer-shell overlay + Vulkan WSI、XWayland 混合、输入转发、多屏/分数缩放、portal + PipeWire 原生 Wayland 捕获（`--portal-window`/`--portal-monitor`）、portal GlobalShortcuts（原生热键）、GUI“Wayland 窗口…”入口均已实现并冒烟；详情见 `.agent/M2a-开发记录.md` 与 `.agent/M2a问题排查-20261108.md`。

**验收标准**：

1. KDE Wayland 下 XWayland 视觉小说窗口化：overlay/输入/弹窗/焦点/最小化/关闭不回归。
2. 原生 Wayland 全屏窗口（vkcube/mpv）经 portal/PipeWire 捕获、超分、呈现；输入穿透不抢焦点。
3. 4K/150% + 1800×2880/100% 双屏位置/比例/输入正确；输出热插拔 1 秒内跟随。
4. 静止画面不重复 capture/present；构建 0 warning / 0 error；91/91 + 新增测试。
5. 纯 X11 会话主要路径冒烟不回归。
6. 窗口化原生 Wayland 输入不在 M2a 解决（M2b 嵌套）。

### M2b · 嵌套合成器（v0.5.0，计划）

| 模块 | 内容 |
|---|---|
| 私有虚拟显示器 | sway headless 优先 / 隐藏嵌套窗口回退；输出 = 主窗口客户区尺寸（含 4:3） |
| 捕获 | 主程序内 C# helper：`wlr-screencopy` / `wlr-export-dmabuf`；Unix socket/共享内存 |
| 输入 | `wlr-virtual-pointer` 绝对/相对；虚拟键盘转发；指针锁定/raw input |
| 窗口识别 | Profile / token + PID/进程树 / sway IPC / transient+window type；歧义手动选择 |
| 弹窗/启动器 | 嵌套内对话框浮动；启动器 → 游戏切换；`--nested` / `--nested-session` |
| Lutris | per-game 环境变量/wrapper；Steam/Heroic 文档化 best-effort |

**验收标准**：

1. 1280×720（16:9）、1024×768（4:3）、1920×1080 在嵌套模式下捕获/超分/输入正确。
2. 无光标跳变；指针/键盘/滚轮/拖拽/指针锁定正确。
3. 启动器 → 游戏、对话框、关闭/退出正常；Lutris 实机通过。
4. sway 缺失/helper 崩溃有明确错误与清理。

### M2c · 色彩、性能与扩展（v0.6.0，计划）

| 模块 | 内容 |
|---|---|
| 色彩 | HDR/linear、tone map/直通；`wp_color_manager_v1` |
| 零拷贝 | PipeWire/嵌套 DMA-BUF 导入 + 显式同步；失败回退 memfd/shm |
| 混合管线 | GL+Vulkan 复核；无 GL 独占需求则明确 no-go |
| 多屏拼接 | 跨屏连续拼接（如需） |
| 输入/撕裂 | 非嵌套 X11 raw input；XWayland 撕裂最终方案（portal / DRI3+fence） |

### M2 已知限制

- GNOME（Mutter）不支持 `zwlr_layer_shell_v1`，M2 不做完整支持。
- portal 首次授权需要用户交互；`restore_token` 单次使用，需保存新 token。
- 嵌套模式必须由 GemPrism/启动器把游戏启动到嵌套 session，不能接管已运行窗口。
- KDE 优先；wlroots 真实桌面验收推后，可能不在 1.0 前完整验证。
- 窗口化原生 Wayland 输入依赖 M2b 嵌套；M2a 只做全屏穿透。

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
M0 ──→ M1a ──→ M1b ──→ M1c ──→ M2a ──→ M2b ──→ M2c ──→ M3
```

M1b 的脏矩形依赖 M1a 的多屏 overlay 抽象；M1c 的 Profile/设置依赖 M1a 的实例与显示器模型；M2a 引入 Wayland 呈现/捕获核心；M2b 的嵌套合成器复用 M2a 的 overlay/呈现/输入框架；M2c 依赖 M2a/M2b 的帧路径。

未排期功能、设置界面需求和明确不做的项见 [backlog.md](backlog.md)；M2 详细计划见 [m2-plan.md](m2-plan.md)。

---

## 版本命名

| 版本 | 对应 |
|---|---|
| 0.1.0 / 0.1.1 | M0a（捕获/呈现/输入/弹窗/UI/生命周期） |
| 0.1.2 | M0b（Anime4K S 2× CNN + Lanczos-2） |
| 0.2.0 | M1a（单实例/退出审计/多显示器基础） |
| 0.2.1 | M1b（指标/S/M/L/脏矩形/上传优化/零拷贝 spike） |
| 0.3.0 | M1c（设置/托盘/Profile/自定义 effect/VL/开发模式；动画留后续） |
| 0.4.0 | M2a（Wayland layer-shell / XWayland 混合 / 多屏 / portal 捕获） |
| 0.5.0 | M2b（嵌套合成器 / 窗口化原生输入 / Lutris） |
| 0.6.0 | M2c（HDR/linear、零拷贝、混合管线、跨屏拼接） |
| 1.0.0 | M3 完成，接口稳定 |
