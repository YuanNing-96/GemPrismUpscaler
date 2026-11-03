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

## 2. M1b / M1c 已讨论项

- **Anime4K M/L**：M1b 接入；默认 S，M/L 为质量档；L 在 4K 60fps 下可能不足，需实测标注。
- **分块脏矩形**：M1b；64/128 分块哈希 + CNN 感受野 halo；大面积变化回退整帧。
- **零拷贝**：M1b 可行性 spike；X11 窗口 pixmap 到 DMA-BUF 没有通用标准路径，可能只能在 Wayland 后端实现。
- **动画**：M1c；只对 GPU 输出矩形插值，overlay 全屏不动。
- **托盘**：M1c；开始/停止/显示隐藏/退出；关闭到托盘可选。
- **Profile**：M1c；按 exe/窗口类自动套用；记住上次选择。
- **自定义 effect**：M1c；TOML 描述 pass；shader 以预编译 SPIR-V + manifest 分发；系统有 glslang 时才允许源码。

## 3. M2 及以后

- 原生 Wayland 捕获（portal + PipeWire + DMA-BUF）与 layer-shell 呈现。
- 完整多显示器：热插拔、每屏 DPI、负坐标、跨屏拼接。
- HDR/linear 色彩管理。
- 混合 GL+Vulkan effect 管线（当前全 Vulkan；没有 GL 独占能力，暂不需要）。
- FPS/鼠标视角/raw input：uinput / libei / 合成器方案；需要处理 `/dev/uinput` 权限、反作弊、相对移动和 pointer lock。
- 第二实例“激活已有窗口”设置（M1c 设置界面的一部分）。

## 4. 需要更多需求才做

- FSR1 EASU + RCAS、NIS、Lanczos-3、Bicubic。
- Anime4K UL/VL、GAN、Restore/Denoise 组合。
- 4K 源 → 8K 输出。
- 视频播放器/桌面超分。
- Windows/macOS 后端。
- 自动更新、安装包（M3 再统一做）。

## 5. 明确不做

- 注入游戏进程、API hook、反作弊规避。
- 帧生成（frame generation）。
- 游戏内 HUD overlay。
- 修改游戏文件/存档/Wine prefix。
- 在 M1 阶段承诺完整 FPS/raw input 支持。
