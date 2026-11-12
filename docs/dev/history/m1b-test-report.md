> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M1b 新增功能测试报告

- 日期：2026-11-05 19:25–20:30（Asia/Shanghai）
- 版本：`0.2.1`
- 代码基线：`079bea1` + `9264df9`（Vulkan 修复，见 §4.1）+ `c31c597`（4K 文档）；tag `v0.2.1`
- 范围：**只测 M1b 新增功能**；按你的要求不做 M1a 老功能回归（2.6 真实游戏 / fit / stretch / 点击透传等留到 1.0）
- 环境：RTX 4060 Laptop 8 GB + AMD 610M；KDE Wayland + XWayland；HDMI-A-1 **3840×2160@144（HDR、150% 缩放）**，DP-1 1800×2880；源窗口 `tools/testpattern` 1280×720；输出固定 4K
- 结论：**M1b 功能通过**；修复 1 个规范违规；4K/60 已由示例视觉小说 C 真实游戏确认（59.5–60.7 fps）。

---

## 1. 结论摘要

| 验收项 | 结果 | 说明 |
|---|---|---|
| 2.1 静止 / 重绘 | ✅ | 静止 0 采集 / 0 呈现 / `gpu=n/a`；重绘 59 采集 / 0 呈现 |
| 2.2 脏矩形正确性 | ✅ | 6 类场景与 `--no-dirty-rect` 逐像素 **AE=0**，含边角/1px/大面积回退/缩放/弹窗恢复 |
| 2.3 动态帧率 A/B | ✅ | 3 轮交替：脏矩形 37.81 fps vs 关闭 37.77 fps，差 **0.79%**（门槛 ±2%） |
| 2.4 S/M/L + 4K | ✅（功能） | 三档均能启动/切换/显示，默认 S；4K 数据见 §3.4 |
| 2.5 双线性 | ✅ | 画面正常，`vram≈688 MiB` |
| 指标 / CSV | ✅ | 92 个 CSV / 511 行 / 601 条 stats 全部过 schema 校验；异常路径不崩溃 |
| 零拷贝 spike | ✅ | 复现文档结果：vkcube 1 fd / modifier 0 / IMPORT OK；软件窗口 2 fd / DCC 不适用 |
| UI 下拉 | ✅* | UI L + 示例视觉小说 C 已实测；hotkey hide/show 日志生效；UI S/M 点击仍建议补一次 |
| 4K 60fps | ✅ | 示例视觉小说 C 1280×720@60 + L + 4K@144：应用 **fps=59.5–60.7、presented=60、capture 3.3–4.6 ms、gpuEffect 1.1–2.0 ms**；游戏与 overlay 两个 MangoHud 均 60 |

**tag `v0.2.1`：** 已在本地创建（`c31c597`，annotated），**未推送**；用户负责 `git push origin main` + `git push origin v0.2.1`。只剩 UI S/M 点击补测（L 已实测，热键日志已生效），不影响 tag。

---

## 2. 自动化基线

```text
dotnet build GemPrismUpscaler.slnx -c Release   0 warning / 0 error
dotnet test  GemPrismUpscaler.slnx -c Release   53/53
--version                                       GemPrismUpscaler v0.2.1
```

`--list` 当前：

```text
monitors:
  HDMI-A-1 3840x2160+1800+489 primary
  DP-1 1800x2880+0+0
windows:
  ... Typora ... monitor=HDMI-A-1
```

> KWin 有时会把新建的 `testpattern` 窗口放到当前活动屏（DP-1），应用“自动跟随”就会输出到 DP。凡是要 4K 数据的场景都显式加了 `--monitor HDMI-A-1`。这是窗口管理行为，不是应用缺陷。

---

## 3. 逐项结果

### 3.1 静止 / 重绘（2.1）

`static`（8–10 s）：

```text
fps=0.0 captured=0 presented=0 ... gpu=n/a cpu≈1–4% gpuUtil=0–1% vram=782/8188MiB
```

`repaint`（每帧重绘同像素，10 s）：

```text
fps=0.0 captured=59/s presented=0 ... gpu=n/a cpu≈7.7–8.4% gpuUtil≈1%
```

- 判定：像素未变时不上传、不跑 effect、不 present，符合预期。
- `captured=59/s` 是 XDamage 每帧触发后重新哈希的结果；`presented=0` 说明没有提交。

### 3.2 脏矩形正确性（2.2）

截图对比方法：`spectacle -f` 抓全桌面后，裁出 HDMI 输出区域 `5120×2880+2400+652`，再和 `--no-dirty-rect` 对比。

| 场景 | 变更内容 | AE（vs 关闭脏矩形） |
|---|---|---:|
| 标准 small dirty | 中心 64×64 块一次性变化 | **0** |
| 边/角 tile | 最右 1px 列 + 最下 1px 行 + 左上 64×64 块 | **0** |
| 1px 变化 | (63,63) 与 (64,64) 两个像素（跨 tile 角） | **0** |
| 大面积回退 | 全帧 100% 像素变化（>50% 阈值） | **0** |
| 源窗口 resize | 1280×720 → 960×540 | **0** |
| 弹窗让位/恢复 | popup 出现隐藏 overlay，5 s 后恢复 | **0** |

- 人工查看：64×64 块边缘干净，1px 网格线连续，文字无重影/缺口，无 64px 块缝。
- 证据：未随发布文档分发。
- 弹窗恢复那一帧 `capture≈77 ms`，是一次性的 unmapped→mapped 重建，不代表稳态。

> **测试口径注意**：全桌面截图里 DP-1 上的系统监视器每秒都在变，直接对整张 7520×3840 图做 AE 会得到 14294 个像素差（0.0005%），但差值全部在 HDMI 区域之外。本文所有 AE=0 都是裁到输出屏后的结果。验收文档 2.2 的 `magick compare` 命令在多屏 + 动态桌面下会误报，建议后续改成裁切或在 `bench-m1b.sh` 里自动裁到 `--monitor` 区域（见 §4.3）。

### 3.3 动态帧率 A/B（2.3）

4K 输出、1280×720 源、`animate`、每轮 10 s；为抵消 run-to-run 噪声，交替跑 3 轮脏矩形 / 3 轮关闭：

| 轮次 | 脏矩形 fps（中位） | 关闭脏矩形 fps（中位） |
|---|---:|---:|
| 1 | 36.9 | 37.8 |
| 2 | 37.9 | 38.4 |
| 3 | 38.4 | 38.2 |
| **合并中位** | **37.81** | **37.77** |

差值 **−0.30 fps（0.79%）**，满足 ±2%。

- 脏矩形：`diff≈1.15 ms`、`gpuEffect≈0.02 ms`、`gpuTail≈0.14 ms`、`gpuUpload≈0.01 ms`。
- 关闭：`diff=0`、`gpuEffect≈0.36 ms`、`gpuTail≈0.81 ms`。
- 两者 `capture≈23 ms`，说明这一轮 fps 由 capture/合成器等待主导，不是脏矩形或 effect 的差异。

### 3.4 Anime4K S/M/L + 真实 4K（2.4）

同一 4K 输出、`animate`、10 s、`--monitor HDMI-A-1`。

**脏矩形默认路径（小区域局部重算）**

| 档位 | fps | gpuEffect | gpuTail | upload CPU | vram（整卡） |
|---|---:|---:|---:|---:|---:|
| S | 36.9 | 0.02 ms | 0.14 ms | 0.00 ms | 782 MiB |
| M | 37.4 | 0.04 ms | 0.14 ms | 0.00 ms | 812 MiB |
| L | 37.6 | 0.05 ms | 0.14 ms | 0.00 ms | 820 MiB |

**`--no-dirty-rect` 全帧路径（每帧整帧 effect）**

| 档位 | fps | gpuEffect | gpuTail | upload CPU | gpuUpload | vram |
|---|---:|---:|---:|---:|---:|---:|
| S | 37.8 | 0.36 ms | 0.81 ms | 0.39 ms | 0.28 ms | 782 MiB |
| M | 37.9 | 0.79 ms | 0.81 ms | 0.39 ms | 0.57 ms | 812 MiB |
| L | 37.3 | 1.11 ms | 0.81 ms | 0.39 ms | 0.28 ms | 820 MiB |

- 三档都能启动、切换、正常显示；不传 `--effect` 时日志为 `effect=anime4k-s`（默认 S）。
- S/M/L 画面确实不同：S vs M 约 45107 像素差、S vs L 约 56063 像素差（14.7M 像素的 0.3–0.4%），无损坏。
- 4K 静止截图：`开发机归档/shots/m1b/effects/`（`dirty-anime4k-{s,m,l}.png`，均为 HDMI 4K）。
- 显存是 NVML 整卡口径，受其他进程影响；同一次会话里 S→M→L 约 +30/+8 MiB，增量稳定。
 - **帧率结论（已修正，见 §3.4.1）**：上表的 fps 绝对值是**软件 testpattern** 的 capture 限制，不代表真实游戏。GPU 侧 S/M/L 只占 0.36/0.79/1.11 ms（全帧）或更少（局部），余量很大；真实 Vulkan 源可到 ~55–60 fps。

### 3.4.1 关键修正（20:16 已由真实游戏确认）：4K/60 通过

先前 37–38 fps 来自软件 testpattern 的 capture 路径；真实游戏路径完全不同。20:16 的 UI 日志（`开发机归档/logs/endfield-4k-l.log`）里第二段 session 是：

```text
source=0x6200003 'Endfield' 1280x720 monitor=HDMI-A-1 effect=anime4k-l dest=3840x2160
```

稳定段 20:16:32–20:16:50（典型 59–60.7，20:16:42 有一次 54.9）：

| fps | presented | capture avg/p95 | diff | upload | effect | present | gpuUpload | gpuEffect | gpuTail |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **59.5–60.7**（典型；中位 59.9） | 60/s | 3.3–4.6 / 9–18 ms | 0.9–1.9 ms | 0.4–0.6 ms | 0.2–0.4 ms | 0.2–0.4 ms | 0.3–0.6 ms | 1.1–2.0 ms | 0.8–1.1 ms |

CPU 14–26%，gpuUtil 60–86%，vram 4796–4932 MiB（含游戏）。游戏窗口的 MangoHud 与 upscaler 的 MangoHud **都显示 60**，直接排除了“MangoHud 只测游戏”的歧义。

同机、同 4K 屏、同 L 档对照：

| 源 | capture | 4K L fps |
|---|---:|---:|
| **示例视觉小说 C**（Vulkan/DXVK，窗口在 HDMI） | **3.3–4.6 ms** | **59.5–60.7** |
| vkcube（Vulkan，窗口在 DP，跨屏 capture） | 11–14 ms | 50.9–59.8 |
| testpattern（软件 X11 窗口） | 23 ms | 37–38 |

- **结论：4K/60 达成**；GPU effect L 1.1–2.0 ms，远低于 16.7 ms 预算。
- 37–38 是软件 testpattern 的 capture 假象，已撤回；真实游戏（同屏、Vulkan）capture 只有 3–5 ms。
- 证据：未随发布文档分发。
### 3.5 双线性（2.5）

4K、`animate`（软件 testpattern，fps 绝对值受 §3.4.1 的 capture 限制；GPU 值仍有效）、10 s：

```text
fps≈38.5  gpuEffect≈0.09 ms  gpuTail=0.00  vram≈688 MiB
```

画面正常、可切换；`gpuTail=0` 符合双线性没有 CNN 尾段。

### 3.6 指标 / CSV

- 92 个 CSV、511 行数据、601 条 `stats` 日志，全部通过 `开发机归档/m1b-check.py`：
  - 23 列 schema 与 header 完全一致；
  - 数值可解析、时间戳单调、`presented ≤ captured`；
  - `gpu_valid=0` 时 GPU 三列为 0；`gpu_valid=1` 时有提交帧；
  - `nvml_available=1`、`vram_total=8188`、`gpuUtil` 合理；
  - `stats` 日志格式与 CSV 字段一一对应。
- 异常路径：
  - `GEMPRISM_STATS=/proc/1/nope.csv`（不可写）→ 应用继续渲染，4 条 stats，无异常；
  - `GEMPRISM_STATS=<目录>` → 应用继续渲染；
  - 多级不存在目录 → 自动创建并写 header；
  - 已存在 CSV → 追加，不重复 header；
  - 不设置 `GEMPRISM_STATS` → 不写文件。
- 一点符合设计但容易误读：GPU 时间戳要等下一帧 `WaitForFences` 才读回。
  - 静止只有 1 帧 → `gpu=n/a`（验收预期）；
  - 一次性脏块只有 2 帧 → 报告里显示的是**上一帧全帧**的 GPU 值，不是这 2 个 tile 的局部值；
  - 性能判断请用 `animate` 的稳态数据，不要用 one-shot 的 `gpuEffect`。

### 3.7 零拷贝 spike（2.7）

在本机重新编译并复跑 `tools/zero-copy/`：

```text
vkcube --wsi xcb 500×500:
  BuffersFromPixmap nfd=1 modifier=0x0 stride=2048
  NVIDIA RTX 4060: vkCreateImage / vkAllocateMemory(import fd) / vkBindImageMemory → IMPORT OK

tools/testpattern 软件窗口 1280×720:
  nfd=2 modifier=0x20000000056bb03 (NVIDIA DCC)
  DRI3BufferFromPixmap FAILED；线性导入不适用 → 回退 XShm
```

与 `docs/dev/history/zero-copy-spike.md` 一致：有条件 go，不进入默认链路；同步/真实游戏仍未验证。

### 3.8 UI

- 启动 v0.2.1 UI，截图确认下拉默认项是 **“Anime4K S 2× + Lanczos（默认）”**：`开发机归档/shots/m1b/ui-active.png`。
- “发现 1 个窗口 · 2 台显示器”、显示器列表/自动跟随正常。
- UI L + 示例视觉小说 C 已实测通过；`Alt+Shift+A` 的 hide/show 在 20:15 日志中生效（伴随一次非致命 BadMatch，见 §4.5）；UI S/M 点击仍建议补一次。步骤见 `开发机归档/M1b-用户验收步骤.md`。

---

## 4. 问题清单

### 4.1 已修复：持久合成图缺少 `TRANSFER_DST` usage（规范违规）

**现象**：每次“整帧 clear”路径都会打印 Vulkan 校验错误：

```text
VUID-VkImageMemoryBarrier-oldLayout-01213
  newLayout (TRANSFER_DST_OPTIMAL) is not compatible with usage
  TRANSFER_SRC|COLOR_ATTACHMENT
VUID-vkCmdClearColorImage-image-00002
  image was created without VK_IMAGE_USAGE_TRANSFER_DST_BIT
```

**影响面**：修复前统计到 231 行校验错误；每次全帧 clear 产出 3 条，`--no-dirty-rect` 整帧动画因重复消息上限表现为 20 条/轮。该问题在 00:38 和 13:25 的旧开发日志里同样存在，只是此前没有写进验收记录。NVIDIA 驱动容忍了它，画面 AE=0；但换驱动/平台可能出问题，属于发布前应修的移植性缺陷。

**根因**：`_composedImage` 创建时只有 `COLOR_ATTACHMENT | TRANSFER_SRC`，而 `PrepareComposedForRender(fullFrame)` 会把它转到 `TRANSFER_DST_OPTIMAL` 并 `vkCmdClearColorImage`。

**修复**（`src/GemPrismUpscaler.Core/Rendering/VulkanBlitter.cs:410`）：

```csharp
Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit |
        ImageUsageFlags.TransferDstBit,
```

**复测**：重建 0/0、53/53；`static` / `dirty` / `dirty --no-dirty-rect` / `popup` / `popup --no-dirty-rect` / `animate` 共 6 轮，**0 条 Validation Error**；dirty 与 popup 的 AE 仍为 **0**。

> 已提交为 `9264df9`，tag `v0.2.1` 指向 `c31c597`（含 4K 文档）。本 agent 未 push。

### 4.2 已解释并确认：软件 testpattern 37–38 vs 真实游戏 60

- 20:16 示例视觉小说 C 日志：**fps 59.5–60.7、presented 60/s、capture 3.3–4.6 ms、gpuEffect L 1.1–2.0 ms**；游戏和 overlay 两个 MangoHud 均 60。
- 同机同屏 L 档：示例视觉小说 C 59.5–60.7（capture 3.3–4.6 ms）、vkcube 50.9–59.8（11–14 ms）、testpattern 37–38（23 ms）。差异在**源窗口的 capture 路径**，不在 Anime4K。
- 此前“4K 未达成”的结论**撤回**；4K 60 在 144Hz + HDR + 150% 缩放下通过，**不需要改刷新率**。
- 推断：真实游戏窗口在 HDMI 上、Vulkan/DXVK、同屏 capture；vkcube 跨屏 capture 稍慢；testpattern 是软件 X11 窗口，XShm 读回要和合成器同步，测到的是工具链上限。
- 测试工具建议：4K fps 验收用真实游戏/Vulkan 源；`testpattern` 继续用于静止/重绘/脏矩形**正确性**。

### 4.3 测试工具/文档：多屏全桌面截图会误报 AE

`M1b验收记录.md` 2.2 的 `magick compare` 对整张全桌面截图做 AE。本机 DP-1 上有每秒刷新的系统监视器，整图 AE=14294；裁到 HDMI 输出区域后 AE=0。建议：
- `bench-m1b.sh` 截图后自动按 `--monitor` 几何裁切，或直接用 `spectacle -m`/按输出抓；
- 验收文档注明“多屏时只比较输出屏区域”。

这不是应用缺陷，但会影响验收结论的可信度。

### 4.4 次要观察：Release 也强制启用 Vulkan 校验层

`VulkanBlitter` 无条件请求 `VK_LAYER_KHRONOS_validation`（失败才回退）。好处是开发期能抓到 §4.1 这类问题；代价是 Release 也有额外开销和日志噪声。建议 M2 用环境变量/构建配置门控，或至少在发布构建关闭。本次未改。

### 4.5 次要：停止/重启会话时出现非致命 X error

示例视觉小说 C 日志 20:15:51–20:16:59 有 6 条（两次 session 停止各一对，另有 focus/重启各一次）：

```text
20:15:53  code=8   request=42.0   # XSetInputFocus（hotkey 显示时）
20:15:57  code=3   request=3.0    # XDestroyWindow（session 停止）
20:15:57  code=152 request=143.2  # XDamage 相关（session 停止）
20:16:04  code=9   request=14.0   # XGetImage / BadDrawable（重启期间）
20:16:59  code=3   request=3.0    # XDestroyWindow（第二次停止，重复）
20:16:59  code=152 request=143.2  # XDamage 相关（第二次停止，重复）
```

- 应用继续运行，下一次 session 正常；热键 hide/show 按日志生效（`hotkey: overlay hidden/shown`），下一帧是完整的全帧 reset。
- 判断为清理/焦点竞态，不是 M1b 新增功能缺陷；建议 M2 顺手加状态保护（viewable 再 `XSetInputFocus`；停止时先停 Damage 再 destroy）。本次未改。

---

## 5. 复现命令

```bash
cd ~/Projects/GemPrismUpscaler

# 基线
dotnet build GemPrismUpscaler.slnx -c Release
dotnet test  GemPrismUpscaler.slnx -c Release

# 2.1
./tools/bench-m1b.sh --monitor HDMI-A-1 --effect anime4k-s --pattern static  --seconds 8
./tools/bench-m1b.sh --monitor HDMI-A-1 --effect anime4k-s --pattern repaint --seconds 8

# 2.2（截图后裁到 4K 输出区域再比）
mkdir -p 开发机归档/shots/manual
./tools/bench-m1b.sh --monitor HDMI-A-1 --effect anime4k-s --pattern dirty --seconds 8 --screenshot 开发机归档/shots/manual
./tools/bench-m1b.sh --monitor HDMI-A-1 --effect anime4k-s --pattern dirty --seconds 8 --no-dirty-rect --screenshot 开发机归档/shots/manual
magick 开发机归档/shots/manual/dirty-anime4k-s.png -crop 5120x2880+2400+652 +repage /tmp/a.png
magick 开发机归档/shots/manual/dirty-anime4k-s-nodirty.png -crop 5120x2880+2400+652 +repage /tmp/b.png
magick compare -metric AE /tmp/a.png /tmp/b.png null:   # 期望 0

# 2.3
./tools/bench-m1b.sh --monitor HDMI-A-1 --effect anime4k-s --pattern animate --seconds 10
./tools/bench-m1b.sh --monitor HDMI-A-1 --effect anime4k-s --pattern animate --seconds 10 --no-dirty-rect

# 2.4 / 2.5
for e in anime4k-s anime4k-m anime4k-l bilinear; do
  ./tools/bench-m1b.sh --monitor HDMI-A-1 --effect "$e" --pattern animate --seconds 10
done
```

---

## 6. 用户侧待办

只剩下 **UI S/M 各点击一次开始/停止**（L 已用示例视觉小说 C 实测通过；双线性 CLI 已覆盖，UI 可顺手点）。`Alt+Shift+A` 的 hide/show 已由 20:15 日志证实，不必重跑；如愿意再看一眼恢复后画面即可。步骤见 `开发机归档/M1b-用户验收步骤.md`。

Vulkan 修复已提交（`9264df9`），tag `v0.2.1` 已在本地创建（`c31c597`）；补完 UI S/M 即可由你推送。

---

## 7. 证据索引

- 日志/CSV：`开发机归档/logs/m1b/`（关键轮次 `20261105-1951*`–`20261105-1955*`）；真实游戏：`开发机归档/logs/endfield-4k-l.log`
- 4K 截图：`开发机归档/shots/m1b/fixed/`、`开发机归档/shots/m1b/effects/`
- 脏矩形补充场景：`开发机归档/shots/m1b/extra-*`、`resize-*`
- UI 默认态：`开发机归档/shots/m1b/ui-active.png`
- 校验脚本：`开发机归档/m1b-check.py`
- 补充测试工具（本地，不入项目）：`开发机归档/extra-pattern.c`、`开发机归档/xresize.c`、`开发机归档/xkey.c`
- 零拷贝探针：`开发机归档/dri3-export`、`开发机归档/dri3-vulkan-import`（源码在 `tools/zero-copy/`）
