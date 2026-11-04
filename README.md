# 宝石棱镜 · GemPrismUpscaler

> Linux 优先的通用窗口超分工具 · 独立进程 · 不注入游戏

宝石棱镜把低分辨率的游戏窗口实时超分到全屏显示。它支持任意分辨率与任意宽高比的组合，目标是通过可配置的 effect 管线组合 Anime4K、FSR 等算法。它不修改游戏、不注入 DLL、不依赖 gamescope，因此对 32/64 位、D3D9/11/12/OpenGL/Vulkan 游戏一视同仁。

当前版本是 **v0.2.1（M1b）**：在 M1a 的单实例/退出审计/多显示器基础之上，加入 **Anime4K S/M/L**、**分块脏矩形**、**分阶段指标** 和 **零拷贝可行性 spike**。默认仍是 Anime4K S + Lanczos-2，双线性回退保留。M1b 的 4K 真实外接屏验收待用户接屏后执行，开发侧自动化与 1920×1200/4K override 数据已完成。

## 支持范围

| 维度 | 状态 |
|---|---|
| 操作系统 | **Linux：部分支持**（仅 X11/XWayland）；**Windows：暂不支持**；**macOS：暂不支持** |
| 桌面/会话 | **KDE Plasma 6 Wayland + XWayland：已验证**；KDE X11：理论可用、未验证；GNOME / wlroots（sway、Hyprland、niri 等）+ XWayland：未验证；**原生 Wayland 窗口：不支持**（M2） |
| 窗口类型 | X11 / XWayland 顶层窗口：支持；原生 Wayland 窗口：不支持 |
| 显示器 | 单显示器：支持；**多显示器：M1a 基础支持**（自动跟随游戏窗口 / 手动选择输出屏）；热插拔、每屏 DPI 等完整支持在 M2 |
| HDR | SDR 直通：支持（HDR 桌面下可正常显示 SDR 内容）；HDR 色彩管理：未做（M2） |
| 输入 | 视觉小说/2D 的鼠标点击、滚轮、键盘：支持；**FPS/鼠标视角/raw input：暂不支持** |
| GPU | NVIDIA 专有驱动：已验证；AMD/Intel Vulkan：理论可用、未验证；多 GPU/混合显卡：未验证 |
| 算法 | **Anime4K S/M/L 2× + Lanczos-2：支持**（默认 S）；双线性回退：支持；FSR1、NIS、Lanczos-3、Bicubic：未排期 |
| 脏矩形 | **支持**（64px 分块哈希 + CNN halo + 局部上传/重算 + 持久合成图）；可 `--no-dirty-rect` 关闭 |
| 指标 | **支持**：capture/diff/upload/effect/present 分段、GPU timestamp、NVML GPU/显存、进程 CPU、p95、CSV |

更细的兼容性、已测试组合和已知限制见 [.doc/compatibility.md](.doc/compatibility.md)。

## 当前开发/验证环境

| 项 | 配置 |
|---|---|
| 发行版 | Arch Linux 系 |
| 桌面 | KDE Plasma 6，Wayland 会话 + XWayland |
| 显示 | 目标双屏环境：HDMI-A-1 3840×2160@144Hz（HDR，150%，主屏）+ DP-1 1800×2880 竖屏（SDR，100%）；M1b 开发机当前为 eDP-2 1920×1200，4K 数据用 `--output-size 3840x2160` 覆写得到 |
| GPU | AMD iGPU + NVIDIA RTX 4060 Laptop 8 GB；effect 在 NVIDIA |
| .NET | 10.0.114 SDK |
| Vulkan | 1.4，NVIDIA 专有驱动 |
| 测试游戏 | 腐姬、秽翼的尤斯蒂娅（M0/M1a 验收） |

## 系统要求

- Linux，X11 或 XWayland；Vulkan 1.2 及以上
- .NET 10 SDK（仅构建需要；运行使用构建产物）
- 4K 输出建议 ≥2 GB 显存；M1b 实测 4K 输出约 555–593 MiB（S/M/L）
- 可选：`glslangValidator`、`spirv-val`（仅重新生成着色器时需要）
- 不再需要 CMake/C++ 原生库：X11 使用 P/Invoke，Vulkan 使用 Silk.NET

## 构建与测试

```bash
dotnet build GemPrismUpscaler.slnx -c Release   # 0 warning / 0 error
dotnet test  GemPrismUpscaler.slnx -c Release   # 53/53
```

重新生成 S/M/L 着色器与 manifest（需要 `glslangValidator` 与 `spirv-val`）：

```bash
python3 tools/build_shaders.py
```

运行程序不需要这两个工具：`.spv` 与 `anime4k_manifest.json` 已预编译并嵌入 `GemPrismUpscaler.Core`。

## 使用

启动配置界面：

```bash
dotnet run --project src/GemPrismUpscaler -c Release
# 或
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler
```

界面里可以：

- 刷新窗口列表；列表会显示 PID、类名、分辨率、窗口 ID 和所在显示器；
- 选择“输出显示器”：**自动（跟随游戏窗口）** 或指定某块屏；
- 选择“超分算法”：**Anime4K S / M / L 2× + Lanczos** 或双线性；
- 开始/停止超分；“跟随焦点”默认开启。

**单实例行为**：同一时间只允许一个实例。第二个 GUI 实例会弹出“已在运行”的错误提示并退出，不会激活已有窗口；第二个 `--window` CLI 会向 stderr 报错并返回非 0。

命令行模式：

```bash
# 列出显示器和可捕获窗口
./GemPrismUpscaler --list

# 查询当前是否已有实例在运行
./GemPrismUpscaler --status

# 自动跟随游戏窗口所在显示器；默认 Anime4K S
./GemPrismUpscaler --window 0x1200001 --effect anime4k-s

# 质量档 M/L
./GemPrismUpscaler --window 0x1200001 --effect anime4k-m
./GemPrismUpscaler --window 0x1200001 --effect anime4k-l

# 固定输出到某块显示器（名称来自 --list）
./GemPrismUpscaler --window 0x1200001 --monitor HDMI-A-1

# 其他
./GemPrismUpscaler --window 0x1200001 --stretch
./GemPrismUpscaler --window 0x1200001 --no-follow-focus
./GemPrismUpscaler --window 0x1200001 --no-dirty-rect
./GemPrismUpscaler --window 0x1200001 --output-size 3840x2160  # 调试/基准
./GemPrismUpscaler --version
```

`--effect anime4k` 仍作为 S 的别名，兼容 M0b/M1a 命令。默认保持源宽高比（fit，4:3 源会有黑边）；`--stretch` 拉伸填满。`--monitor` 省略时自动选择窗口所在显示器；窗口跨屏移动时 overlay 会跟随重建。`--output-size` 是调试/基准用的输出尺寸覆写（可大于屏幕），正常使用不要加。

日志：设置 `GEMPRISM_LOG=/path/app.log` 可指定日志文件；否则写入用户本地应用数据目录。设置 `GEMPRISM_STATS=/path/stats.csv` 会另外写每秒一行指标 CSV。日志第一行应包含：

```text
engine started version=0.2.1 effect=anime4k-s ... monitor=HDMI-A-1 ...
```

每秒的 `stats` 行示例：

```text
stats fps=47.9 captured=48 presented=48 capture=0.45/0.67ms diff=1.05/1.16ms upload=0.00/0.00ms effect=0.11/0.21ms present=17.04/18.18ms gpuUpload=0.03ms gpuEffect=0.07ms gpuTail=0.13ms cpu=10.1% gpuUtil=15% vram=466/8188MiB ...
```

阶段值是 `平均/p95` 毫秒；`present` 包含 `QueuePresent` 的 vblank/队列等待，不是纯 CPU 工作；`gpu=n/a` 表示该秒没有提交帧（静止）。

## 测试与基准

```bash
# 静态：期望 0 capture / 0 present
./tools/bench-m1b.sh --effect anime4k-s --pattern static --seconds 10

# 每帧重绘但像素不变：captured≈60、presented=0
./tools/bench-m1b.sh --effect anime4k-s --pattern repaint --seconds 10

# 小脏块 + 截图；再跑 --no-dirty-rect 做逐像素对比
./tools/bench-m1b.sh --effect anime4k-s --pattern dirty --seconds 8 \
  --screenshot .agent/shots/m1b

# 全屏动画 A/B
./tools/bench-m1b.sh --effect anime4k-s --pattern animate --seconds 10
./tools/bench-m1b.sh --effect anime4k-s --pattern animate --seconds 10 --no-dirty-rect

# S/M/L 4K 数据（无 4K 屏时用 override）
for e in anime4k-s anime4k-m anime4k-l; do
  ./tools/bench-m1b.sh --effect "$e" --pattern animate --seconds 10 \
    --output-size 3840x2160
done
```

`tools/testpattern` 支持 `--static`、`--dirty`、`--animate`、`--repaint`、`--popup`、`--size`。完整验收清单见 `.agent/M1b验收记录.md`。

## 实现要点

- **捕获**：`XShmGetImage` 直接读 client window；KWin 已做合成重定向，被遮挡窗口仍能读到自己的画面。M1b 起主路径直接从 shm 指针做分块哈希/分块上传，不再整帧拷贝到托管数组；`XGetImage` 回退仍保留。
- **脏矩形**：64px 分块 64-bit 哈希；只上传变化块，CNN 按 manifest 的 halo 扩展后只重算这些区域；大面积变化（≥50%）回退整帧。Lanczos 写入一张持久的 4K 合成图，脏块更新后再整图 copy 到轮转的 swapchain，避免丢帧/块缝。
- **多显示器**：用 XRandR 1.5 枚举物理像素矩形；overlay 和 Vulkan swapchain 只覆盖选中的一块屏，`DestRect` 按该屏尺寸计算，输入坐标按该屏原点换算。自动模式下窗口跨屏移动会触发 overlay/swapchain 重建。
- **呈现**：NVIDIA Vulkan + Xlib swapchain；overlay 是 override-redirect 窗口。`--effect bilinear` 走单次 `vkCmdBlitImage`；Anime4K 走 manifest 驱动的 N-pass compute（S=5、M=9、L=10 pass），最后用 graphics pass 做 Lanczos-2 并写入持久合成图。
- **指标**：CPU Stopwatch + Vulkan timestamp query（upload/effect/tail）+ NVML（整卡 GPU/显存）+ 进程 CPU；每秒输出平均/p95，并可选 CSV。
- **输入**：overlay 接收真实鼠标，按输出内容矩形反算到源窗口坐标，再用 `XSendEvent` 发给游戏，并补发进入/离开源窗口的 crossing 事件；不使用点击穿透，也不依赖 XTest。
- **弹窗**：轮询 `_NET_CLIENT_LIST`，发现源进程额外的 viewable 顶层窗口就隐藏 overlay；关闭后恢复并强制重绘一帧。
- **生命周期**：源窗口最小化/unmap 时收起，恢复时重新捕获；源窗口被销毁时引擎自行停止。单实例使用 `$XDG_RUNTIME_DIR` 下的 Unix socket；退出路径覆盖窗口关闭、`SIGTERM`/`SIGINT`、游戏关闭和引擎异常。
- **跨平台**：`ICaptureBackend` / `IPresentBackend` / `IInputBackend` / `IEffectBackend` 的边界按平台无关设计；Linux 实现集中在 `Core/Platform/Linux`。

## 已知限制

- 脏矩形哈希在 720p 约 1 ms/帧；4K 源约 4 倍，M1b 只承诺常见 VN 分辨率。
- 当前开发机合成器约 48 fps 上限；4K 真实外接屏/60Hz 帧率待验收。
- 4K 源（2× → 8K）未测；swapchain 显示模式 recreation 仍不完整。
- 零拷贝只完成“导出 + Vulkan 导入”可行性，未做 fence 同步和真实游戏端到端验证，**不进入默认链路**。详见 [.doc/zero-copy-spike.md](.doc/zero-copy-spike.md)。
- 多显示器完整热插拔/每屏 DPI、原生 Wayland、HDR/linear 色彩管理仍在 M2。
- 没有托盘/完整设置界面；第二实例不会激活已有窗口（M1c）。

## 文档

- [设计文档](.doc/design.md) —— 需求、架构、技术选型、实现细节
- [路线图](.doc/roadmap.md) —— M0/M1/M2 阶段划分与验收标准
- [兼容性](.doc/compatibility.md) —— 支持矩阵、已测试环境、已知限制
- [功能 backlog](.doc/backlog.md) —— 未排期功能与设置界面需求
- [更新记录](.doc/changelog.md) —— 各版本修复、功能变更与已知限制
- [零拷贝 spike](.doc/zero-copy-spike.md) —— DRI3/DMA-BUF 探针与 go/no-go
- [着色器说明](shaders/README.md) —— Anime4K S/M/L 来源、manifest 与重新生成方式

## 致谢

本项目在架构上参考了 [Magpie](https://github.com/Blinue/Magpie) 的 effect 管线设计，超分算法来自 [Anime4K](https://github.com/bloc97/Anime4K)，几何模型借鉴了 Magpie 的 `ScalingType` 方案。

## 许可证

GNU Affero General Public License v3.0 or later（AGPL-3.0-or-later）。见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。
