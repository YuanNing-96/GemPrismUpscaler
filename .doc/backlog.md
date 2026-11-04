# 功能 backlog

本文记录尚未排期的功能、设置界面需求和明确不做的项。已排期内容见 [roadmap.md](roadmap.md)；支持范围见 [compatibility.md](compatibility.md)。

## 1. M1c 设置界面需求

M1a 只保留主界面上的窗口/显示器/算法选择；完整设置界面计划在 M1c。

| 设置项 | 默认 | 说明 |
|---|---|---|
| 热键 | `Alt+Shift+A` | 可修改修饰键 + 主键；冲突时提示并回退 |
| 日志路径 | 用户本地应用数据目录 | 可覆盖 `GEMPRISM_LOG`；设置优先级在实现时定 |
| 第二实例行为 | **仅提示错误** | 可选“激活第一个实例的窗口”；M1a 固定仅提示 |
| 默认显示器 | 自动（跟随游戏窗口） | 可选主屏或指定屏；M1a 选择只对本次会话生效 |
| 默认效果 | Anime4K S + Lanczos | M1b 增加 M/L 后可选 |
| 托盘关闭行为 | 直接退出 | 若启用托盘，可选“最小化到托盘” |
| 输入模式 | 当前 2D/视觉小说方案 | FPS/鼠标视角/raw input 实现前置灰，仅占位 |
| 跟随焦点 | 开启 | 与主界面开关同步 |

实现建议：TOML 配置；启动时读取，保存时原子写入；损坏配置回退默认并记录日志。M1a 不提前引入临时配置系统。

## 2. M1a 验收产生的后续优化项

以下各项已在 M1a 验收中确认“当前行为可用”，作为后续体验优化记录，不阻塞 M1a。

- **多屏焦点策略**：当前“跟随焦点”在切到另一块屏上的程序时会收起 overlay。可考虑增加“锁定覆盖层”“切屏不收起/延时收起”“仅在游戏窗口获得焦点时恢复”等策略，默认仍保持当前行为。
- **手动选屏交互**：当前 UI/CLI 可固定指定屏；后续决定更顺手的交互（记住上次、按游戏窗口自动、快捷键切换、默认屏设置）。
- **窗口列表自动刷新**：当前需要手动点“刷新窗口”。可增加定时刷新（如 0.5–2 秒可配）或“手动/自动”开关，避免游戏启动后才出现在列表里。
- **CLI 验收辅助**：M1a 的 CLI 条目（第二实例、`--status`、通知）由用户延期测试。后续可提供一段可复制的脚本/操作说明，降低“需要延迟操作”的测试门槛。
- **黑边输入说明**：非 stretch 模式下黑边不转发鼠标事件是预期行为（黑边不是源窗口内容）；stretch 后原黑边区域属于画面，会正常转发。后续可在 UI/文档中更明确提示。

## 3. M1b 已完成 / 后续项

- **Anime4K S/M/L**：✅ M1b 已接入 manifest 驱动 N-pass；默认 S；4K override 数据已记录；真实 4K 屏待复测。
- **分块脏矩形**：✅ M1b 已接入 64px 哈希 + halo + 分块上传/局部重算 + 持久合成图；`--no-dirty-rect` 可回退。
- **零拷贝 spike**：✅ 有条件 go；GPU 窗口线性 1-fd 可导入，2-fd/DCC 需回退；不默认启用；见 `.doc/zero-copy-spike.md`。
- **指标/CSV**：✅ M1b 已接入；后续可加 UI 仪表/告警（M1c）。
- **后续优化**：
  - 脏矩形 hash 在静态窗口首次读回可达 4–5ms；可考虑自适应 throttle/128px tile/SIMD/GPU hash。
  - 4K 源（2×→8K）的哈希/显存/带宽仍需专门评估，M1b 不承诺。
  - 零拷贝下一步：DRI3 fence → Vulkan external semaphore、真实游戏/跨 GPU、GPU 侧 tile hash。
  - `present` 计时含 vblank 等待；后续可加 `VK_KHR_present_wait`/显示延迟分析。
- **M1c**：动画、托盘、Profile、自定义 effect（TOML + 预编译 SPIR-V + manifest）。

## 4. M2 及以后

- 原生 Wayland 捕获（portal + PipeWire + DMA-BUF）与 layer-shell 呈现。
- 完整多显示器：热插拔、每屏 DPI、负坐标、跨屏拼接。
- HDR/linear 色彩管理。
- 混合 GL+Vulkan effect 管线（当前全 Vulkan；没有 GL 独占能力，暂不需要）。
- FPS/鼠标视角/raw input：uinput / libei / 合成器方案；需要处理 `/dev/uinput` 权限、反作弊、相对移动和 pointer lock。
- 第二实例“激活已有窗口”设置（M1c 设置界面的一部分）。

## 5. 需要更多需求才做

- FSR1 EASU + RCAS、NIS、Lanczos-3、Bicubic。
- Anime4K UL/VL、GAN、Restore/Denoise 组合。
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
