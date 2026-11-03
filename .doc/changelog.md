# 更新记录

## v0.2.0（2026-11-04）· M1a：单实例、退出审计与多显示器基础

M1a 不改变 effect 管线，仍使用 Anime4K S + Lanczos-2 和双线性回退；重点是把“能退出、能选屏、不会跨屏”做扎实。

### 新增

- **单实例**：`$XDG_RUNTIME_DIR` 下的 Unix socket；第二个 GUI 实例弹出“已在运行”错误提示并退出，不激活已有窗口；第二个 CLI 返回非 0；新增 `--status` 查询 PID/模式/会话/显示器。
- **退出审计**：窗口关闭、退出按钮、`SIGTERM`/`SIGINT`、游戏窗口关闭、引擎异常都会记录日志并销毁 overlay；引擎停止最多等待 3 秒。
- **多显示器基础**：XRandR 1.5 枚举物理显示器矩形；overlay/swapchain 只覆盖选中屏；支持“自动跟随游戏窗口”和 `--monitor <name>` 手动选屏；窗口跨屏移动时自动重建；选屏失效时回退自动。
- **输入坐标修正**：`XQueryPointer` 的 root 坐标在同步指针时减去显示器原点，修复多屏下悬停位置偏移。
- **窗口选择**：过滤面板/桌面窗口；列表显示 PID、类名、分辨率、窗口 ID 和所在显示器；刷新后保持原选择；单实例状态接入 UI。
- **文档与许可**：README 改为支持矩阵/当前验证环境/系统要求；新增 `compatibility.md`、`backlog.md`；roadmap 重排为 M1a/M1b/M1c；删除时间盒表述；许可证改为 **AGPL-3.0-or-later**，新增 `AUTHORS`。

### 验证

- `dotnet build -c Release`：0 warning / 0 error。
- `dotnet test -c Release`：32/32（新增显示器选择、竖屏布局、单实例状态）。
- 1280×720 → DP-1（1800×2880）：overlay 1800×2880+0+0，内容 1800×1012+0+934，合成测试约 46–50 fps。
- 1280×720 → HDMI（3840×2160）：overlay 3840×2160+1800+489，内容 3840×2160，合成测试约 41–44 fps。
- 自动模式窗口跨 DP-1/HDMI 移动，overlay 在约 0.3–0.7 秒内重建；手动 `--monitor` 固定不跟随。
- 第二 CLI 返回 4；第二 GUI 显示错误提示；`SIGTERM` 后无残留进程/overlay。

### 注意事项

- M1a 的第二实例**不会**激活已有窗口；该行为作为 M1c 设置项。
- 多显示器只做基础版：热插拔、每屏 DPI、负坐标、跨屏拼接的完整处理在 M2。
- 没有托盘和完整设置界面；见 `.doc/backlog.md`。
- 版本号升到 0.2.0；已在验收通过后打本地 `v0.2.0` tag，push 由用户手动执行。

## v0.1.2（2026-11-02）· M0b：Anime4K 2× + Lanczos

**M0 完成**：M0a（捕获/呈现/输入/弹窗/生命周期）与 M0b（Anime4K 2× + Lanczos-2）均通过用户真实游戏验收。混合 GL+Vulkan 管线按降级方案不做，效果全部在 Vulkan 中执行；M2 再评估。

M0b 把 M0a 的“双线性放大”升级为真正的超分管线，并保留双线性作为对比/回退。

### 新增

- **Anime4K 2× CNN（S）**：`Upscale_CNN_x2_S` 展开为 5 个 Vulkan compute pass（4 层 3×3 CNN + Depth-to-Space 残差），中间纹理 `rgba16f`。
- **Lanczos-2 收尾**：4×4 taps 从 2× 结果重采样到最终内容矩形；graphics pass 直接写入 swapchain，render pass 清黑边。
- **算法切换**：UI“超分算法”下拉框、CLI `--effect anime4k|bilinear`；默认 Anime4K。
- **版本号**：`Directory.Build.props` 统一 `0.1.2`；UI 标题、`--version`、日志都显示 v0.1.2。
- **离线着色器构建**：`tools/build_shaders.py` 解析 mpv user-shader 指令、生成 GLSL、`glslangValidator` 编译、`spirv-val` 校验；SPIR-V 嵌入程序，运行时不依赖 glslang。

### 技术方案

- `Anime4KChain`：5 个 compute pipeline、每 pass 独立 descriptor set、48 字节 push constants（输出尺寸 + 5 张纹理尺寸）；pass 间用 image barrier 做 `ShaderWrite → ShaderRead`。
- `LanczosPresenter`：全屏三角形 + Lanczos-2 fragment shader；通过 viewport/scissor 把绘制限制在内容矩形，render pass 的 clear 负责黑边；swapchain 从 `PresentSrcKHR`/`Undefined` 转入 `ColorAttachmentOptimal`。
- 源图仍由 XShm → staging → `B8G8R8A8_UNORM` 上传；效果链读取源图，输出 2× 图像；最终 pass 直接写 swapchain，避免额外的 4K 中间纹理和 float→unorm blit 的格式兼容问题。
- 保留 M0a 的 `vkCmdBlitImage` 路径；`EffectMode.Bilinear` 可在运行时由 CLI/UI 选择。

### 验证

- `dotnet build -c Release`：0 warning / 0 error；`dotnet test -c Release`：23/23（新增 8 个：版本号 1 个 + 7 个着色器资源）。
- 4:3 fit（1024×768 → 2880×2160）：左右黑边像素均值 0，内容居中。
- 4:3 stretch：铺满 3840×2160，无黑边。
- 画质测试卡（1280×720 → 4K，8-bit PNG 截图）：梯度均值 Anime4K 3.43 vs 双线性 2.71（+26%）；Laplacian 均值 3.49 vs 1.79（+96%）。
- 1280×720 动态画面：42–51 fps；当前 HDR/144Hz 桌面合成器上限约 48–50 fps，基线同环境 49–51 fps。GPU 占用 Anime4K 约 35–46%，显存 515 MiB。
- 静止画面仍然 0 capture / 0 present。

### 注意事项

- 本版本只内置 Anime4K **S**；M/L 变体未接入。FSR1、`Clamp_Highlights`、混合 GL+Vulkan 管线不在 M0b 范围。
- 画质指标是合成测试卡上的客观锐度对比，真实游戏的观感仍需用户复测；Anime4K 对 2D/动漫风格效果最好。
- 帧率目前受本机 HDR/144Hz 合成器上限影响，不能据此判断 60Hz 下的上限；GPU 仍有约 60% 余量。
- 4K 输出下效果链显存约 515 MiB（RTX 4060 Laptop 8 GB 足够）；后续 4K 源/多显示器需要重新评估。
- HDR 仍是 SDR 直通，未做色彩管理。

## 2026-11-02 · M0a 验收整改（0.1.x）

对应 `.agent/M0a验收记录.md` 的 5 条实测问题。改动集中在捕获/呈现/输入/窗口生命周期，不涉及 M0b 的 Anime4K/FSR。用户已在腐姬 / 秽翼的尤斯蒂娅上复测通过。

### 修复的问题

| # | 问题 | 根因 | 处理 |
|---|---|---|---|
| 1 | 秽翼启动后，覆盖层上移动鼠标没有悬停反应，必须先点一下 | 启动会话时焦点留在配置界面；overlay 覆盖后源窗口又认为指针已离开 | 启动/点击/热键恢复时通过 EWMH `_NET_ACTIVE_WINDOW` + `XSetInputFocus` 把焦点交还游戏；补发 `EnterNotify` / `LeaveNotify`；显示时同步当前指针位置 |
| 2 | 秽翼按 Esc 最小化后，全屏画面卡死 | 引擎只在有 XDamage 时检查源窗口，最小化后没有新 Damage | 每 200ms 检查 `map_state` / `_NET_WM_STATE_HIDDEN`；不可见时收起 overlay，恢复时强制重绘；窗口销毁则停止引擎 |
| 3 | 腐姬退出确认框关闭后，恢复的全屏画面黑屏，点到有反应的位置才出现 | overlay unmap 后 Vulkan/X11 surface 内容未定义；弹窗关闭只恢复窗口，没有重新呈现 | 任何“隐藏 → 显示”都强制捕获+呈现一帧；弹窗恢复和热键恢复同样处理 |
| 4 | 腐姬退出游戏后，全屏界面黑屏无反应 | 源窗口销毁后，没有 Damage 就永远发现不了 | 同一套 200ms 生命周期轮询；`XGetWindowAttributes` 失败即停止引擎并销毁 overlay |
| 5 | 热键隐藏 overlay 后关闭游戏，UI 仍显示“正在超分” | overlay 隐藏时循环直接 `continue`，完全跳过源窗口检查 | 隐藏时仍轮询；引擎把退出原因写入 `ExitReason`；UI 在 500ms 内复位按钮并显示原因 |

### 新增 / 变更的功能

- **焦点跟随**：游戏失去焦点时自动收起覆盖层，回到游戏时恢复。配置界面新增“跟随焦点（切到其他程序时收起）”开关，默认开启；CLI 新增 `--no-follow-focus`。
- **窗口生命周期**：最小化/切工作区自动收起；恢复后重新捕获；游戏关闭时引擎自行退出，UI 复位。
- **弹窗判定收紧**：只把同 PID、当前 viewable、非 `_NET_WM_STATE_HIDDEN`、尺寸 ≥64×64 的顶层窗口算作弹窗，避免 Wine 的隐藏 helper/IME 窗口把 overlay 永久按下去。
- **输入边界**：拖拽到内容矩形（黑边）之外松手时补发 `ButtonRelease`，避免按键卡住；点击时若游戏没有焦点，先激活游戏。
- **UI 窗口列表**：过滤本进程自己的配置窗口，避免默认选中自己。

### 技术方案

- 新增 `OverlayVisibility` 纯状态机，把“用户隐藏 / 源窗口不可见 / 弹窗 / 失焦”四类原因分开，便于单元测试和日志定位。
- 新增 `X11WindowProbe`：`XGetWindowAttributes` + `_NET_WM_STATE_HIDDEN` 检查窗口是否可见；`XGetInputFocus` + `XQueryTree` 判断焦点是否在源窗口（或其子窗口）；EWMH `_NET_ACTIVE_WINDOW` 激活窗口。
- 覆盖层可见性统一由 `ApplyVisibility()` 处理；每次“隐藏 → 显示”都置 `_forceRedraw`，强制走一次捕获+呈现，解决 unmap 后 surface 内容未定义的问题。
- 输入转发在原有 `XSendEvent` 基础上补合成 `EnterNotify` / `LeaveNotify`，并在显示时用 `XQueryPointer` 同步一次指针位置。
- XDamage 的帧级跳过保持不变：静止画面不重复捕获/呈现。

### 验证

- `dotnet build GemPrismUpscaler.slnx -c Release`：0 warning / 0 error。
- `dotnet test GemPrismUpscaler.slnx -c Release`：15/15 通过（新增 8 个可见性状态机测试）。
- 合成 1280×720：点击 overlay (1920,1080) → 源窗口 (640,360)；静态画面 + 弹窗恢复非黑屏；最小化收起/恢复；关闭游戏退出并销毁 overlay；失焦收起/回焦恢复。
- 帧率：桌面 60Hz 时 58–60 fps。当前测试机处于 HDR / 3840×2160@144Hz 时合成测试约 47–50 fps，基线提交同环境约 49–51 fps，差异来自合成器/显示模式，不是本次改动引入。

### 注意事项

- **轮询 `GetCursorPos` / DirectInput 的引擎**：合成事件不会移动真实指针；这类引擎的悬停位置仍可能不跟随。本次秽翼已通过焦点/消息路径解决，但更底层的输入方案（uinput 虚拟指针、合成器集成）仍不在 M0 范围。
- **焦点跟随依赖 EWMH**：KWin 实测支持；不支持的 WM/合成器下只调用 `XSetInputFocus`，可能被焦点窃取防护忽略。
- **最小化恢复的第一帧**：可能先显示黑/旧帧，源窗口重绘后由 XDamage 更新；真实游戏会在 Expose 后重绘。
- **HDR**：开启 HDR 时仍可运行（SDR 直通），但 M0 不做色彩管理；linear/HDR 直通在 M2。
- **弹窗识别**：仍依赖 `_NET_WM_PID`；缺失 PID 的 Wine 版本需要后续补 XRes 兜底。
