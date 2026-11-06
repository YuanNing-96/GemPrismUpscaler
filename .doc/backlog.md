# 功能 backlog

本文记录尚未排期的功能、设置界面需求和明确不做的项。已排期内容见 [roadmap.md](roadmap.md)；支持范围见 [compatibility.md](compatibility.md)。

## 1. M1c 设置界面（已实现）

M1c 已实现完整的设置/配置界面；下表保留为需求对照。

| 设置项 | 默认 | 状态 |
|---|---|---|
| 热键 | `Alt+Shift+A` | ✅ 可配置；冲突提示并回退；处理 NumLock/CapsLock |
| 日志路径 | 用户本地应用数据目录 | ✅ 可配置；`GEMPRISM_LOG` 仍优先 |
| 第二实例行为 | **仅提示错误** | ✅ 可选“激活已有窗口”；CLI 仍报错 |
| 默认显示器 | 自动（跟随游戏窗口） | ✅ 持久化；Profile 可覆盖 |
| 默认效果 | Anime4K S + Lanczos | ✅ S/M/L/VL/双线性/自定义 effect |
| 托盘关闭行为 | 直接退出 | ✅ 可选“最小化到托盘”；托盘不可用强制退出 |
| 输入模式 | 当前 2D/视觉小说方案 | ✅ 占位；FPS/raw input 置灰 |
| 跟随焦点 | 开启 | ✅ 与主界面同步 |
| 窗口列表自动刷新 | 关闭 | ✅ 可配置 250–5000ms |
| 开发模式 | 关闭 | ✅ 详细日志 + 非功能错误提示 |

实现结果：TOML 配置；启动读取、保存原子写入；损坏配置备份回退并记录日志；`GEMPRISM_CONFIG` 可覆盖。

## 2. M1a 验收产生的后续优化项

以下各项已在 M1a 验收中确认“当前行为可用”，作为后续体验优化记录，不阻塞 M1a。

- **多屏焦点策略**：当前“跟随焦点”在切到另一块屏上的程序时会收起 overlay。可考虑增加“锁定覆盖层”“切屏不收起/延时收起”“仅在游戏窗口获得焦点时恢复”等策略，默认仍保持当前行为。
- **手动选屏交互**：当前 UI/CLI 可固定指定屏；后续决定更顺手的交互（记住上次、按游戏窗口自动、快捷键切换、默认屏设置）。
- **窗口列表自动刷新**：✅ M1c 已加可配置定时刷新；默认关闭。
- **CLI 验收辅助**：M1a 的 CLI 条目（第二实例、`--status`、通知）由用户延期测试。后续可提供一段可复制的脚本/操作说明，降低“需要延迟操作”的测试门槛。
- **黑边输入说明**：非 stretch 模式下黑边不转发鼠标事件是预期行为（黑边不是源窗口内容）；stretch 后原黑边区域属于画面，会正常转发。后续可在 UI/文档中更明确提示。

## 3. M1b/M1c 已完成 / 后续项

- **Anime4K S/M/L**：✅ M1b 已接入 manifest 驱动 N-pass；默认 S；4K override 与真实 4K@144（终末地 720p60 + L，59.5–60.7 fps）数据已记录。
- **Anime4K VL**：✅ M1c 已接入（18 pass / 19 纹理 / 176B push constant）；设备能力不足时不启用；帧率不作为验收门槛。
- **分块脏矩形**：✅ M1b 已接入 64px 哈希 + halo + 分块上传/局部重算 + 持久合成图；`--no-dirty-rect` 可回退。
- **零拷贝 spike**：✅ 有条件 go；GPU 窗口线性 1-fd 可导入，2-fd/DCC 需回退；不默认启用；见 `.doc/zero-copy-spike.md`。
- **指标/CSV**：✅ M1b 已接入；后续可加 UI 仪表/告警。
- **M1c**：✅ 托盘、Profile、自定义 effect（TOML + 预编译 SPIR-V + ABI 文档）、错误/开发模式；⏸ 动画留 v1.0 前评估。
- **后续优化**：
  - 脏矩形 hash 在静态窗口首次读回可达 4–5ms；可考虑自适应 throttle/128px tile/SIMD/GPU hash。
  - 4K 源（2×→8K）的哈希/显存/带宽仍需专门评估，M1b 不承诺。
  - 零拷贝下一步：DRI3 fence → Vulkan external semaphore、真实游戏/跨 GPU、GPU 侧 tile hash。
  - `present` 计时含 vblank 等待；后续可加 `VK_KHR_present_wait`/显示延迟分析。
  - 自定义 effect 目前只支持 compute pass + 内置 Lanczos-2 收尾；自定义 graphics 最终 pass 留后续。
  - 冷启动/多 pass effect 的首帧 pipeline 创建开销较大；后续可做缓存。

## 4. M2 及以后

M2 已拆成 M2a / M2b / M2c，详细计划见 [m2-plan.md](m2-plan.md)。

- **M2a（0.4.0，开发中）**：Wayland layer-shell 呈现、XWayland 混合、原生 Wayland portal/PipeWire 捕获（全屏输入穿透）、多屏/分数缩放/热插拔、GlobalShortcuts、CLI 文档。
- **M2b（0.5.0，计划）**：嵌套合成器（sway + helper）解决窗口化原生 Wayland 输入；任意分辨率/4:3；弹窗/启动器/Lutris；无光标跳变；raw input/指针锁定。
- **M2c（0.6.0，计划）**：HDR/linear 色彩；PipeWire/嵌套 DMA-BUF 零拷贝；混合 GL+Vulkan 复核；跨屏连续拼接（如需）；非嵌套 X11 raw input；XWayland 撕裂最终方案。
- **动画**：M1c 未做，留 v1.0 前评估。
- **自定义 effect 的 graphics 最终 pass**：当前只支持 compute + 内置 Lanczos-2；留后续。
- **4K 源（2×→8K）**：脏矩形/显存/带宽仍需专门评估。

## 5. 需要更多需求才做

- FSR1 EASU + RCAS、NIS、Lanczos-3、Bicubic。
- Anime4K UL、GAN、Restore/Denoise 组合。
- 4K 源 → 8K 输出。
- 视频播放器/桌面超分。
- Windows/macOS 后端。
- 自动更新、安装包（M3 再统一做）。

## 6. 明确不做

- 注入游戏进程、API hook、反作弊规避。
- 帧生成（frame generation）。
- 游戏内 HUD overlay。
- 修改游戏文件/存档/Wine prefix。
- 在 M1 阶段承诺完整 FPS/raw input 支持。

## 7. M1 阶段记录、留待后续处理

- **X11/XWayland GPU 窗口捕获撕裂（M1c 未解决，M2c 最终处理）**：源窗口在屏幕上看正常，但 `XShmGetImage` / `XGetImage` / `XCompositeNameWindowPixmap` 读 GPU 更新的窗口 drawable 时可能拿到半帧，overlay 表现为旧帧回退/撕裂。已排除：FIFO/immediate、present wait 实验、XComposite 捕获、焦点隐藏、direct-scanout 阻断。对照实验：mpv OpenGL 未开 vsync 时源窗口捕获和 overlay 都出现数字错位；加 `--opengl-swapinterval=1 --video-sync=display-resample` 后对齐；软件 testpattern 稳定。根因是捕获没有 GPU fence/原子帧保证。M2a 的 XWayland 混合仍用 XShm；M2c 用 portal 合成帧或 DRI3+fence 做最终方案。临时规避：源程序开启 VSync/垂直同步。

- **KDE Wayland + 4K/150% 缩放下 ComboBox 下拉列表不跟随窗口移动**：在 HDMI 4K（150%）上打开下拉框后拖动窗口，选择列表停留在旧位置；DP 100% 屏正常。判断为 Avalonia 12.1.4 在 Wayland 下 popup 定位/缩放的限制，不是本项目的布局问题。M1 不处理；已尝试“窗口移动时自动收起”，但在 Wayland 下收不到可靠的窗口位置变化，效果不成立，已回退。留到 M2 或 Avalonia 升级后复测。
- **热键改为“实际按一遍”录入**：M1c 设置页目前是文本框输入字符串；后续改成捕获用户真实按键/修饰键，实时显示并校验，避免手写格式错误。M1 暂不修改。
- **日志路径默认值展示**：未配置日志路径时，设置页目前是空输入框 + “留空使用默认路径”占位；后续应显示灰色默认文本 `(默认) <默认日志路径>`，并在用户清空时回退默认。M1 暂不修改。

