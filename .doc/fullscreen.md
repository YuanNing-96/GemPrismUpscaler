# 游戏内全屏（已知问题）

**状态：v1.0 不解决，记录归档（2026-11-12 决策）。**

这份文档记录腐姬（`KhimeDL_CHS.exe`，Proton/Wine）在游戏内切“全屏模式”时出现的两类问题、
已经查到的根因、以及可选但**未实施**的修复方案。秽翼的全屏现象与 KDE 直连一致，不属于本问题。

---

## 1. 现象

### 1.1 腐姬 X11 全屏黑屏

- 游戏内从“窗口模式”切到“全屏模式”后，宿主窗口全屏了，但画面全黑。
- 在黑屏上点击能听到按钮音效，说明游戏在正常渲染和响应输入。
- 进入/退出一次超分模式（Alt+Shift+A）后画面会出现（见 2.3）。
- 截图：`.agent/M2b验收/腐姬x11全屏异常.png`。

### 1.2 腐姬 Wayland 全屏画面居左、点击错位

- 画面内容贴在左上，右侧一条黑带；鼠标点击位置和画面上的控件对不上。
- KDE 直连同样有“画面居左、点击区偏右（感觉在屏幕中间）”的现象，所以不是我们独有的问题。
- 截图：
  - 我们的：`.agent/M2b验收/20261112-腐姬wayland全屏-我们的.png`
  - KDE 直连：`.agent/M2b验收/腐姬wayland全屏异常-KDE直连的情况.png`
- 点击偏移证据（注入点击后“文字快進”行的高亮从右选项换到左选项）：
  `.agent/M2b验收/20261112-腐姬wayland全屏-点击偏移证据.png`

---

## 2. 根因分析

### 2.1 X11：游戏画在 override-redirect 窗口上

切全屏后，游戏把画面渲染到一个 **override-redirect** 的 X11 窗口：

```text
0xc00001 map=2 or=1 3838x2160+1+0     # 真正画内容的窗口
0x2a00001 map=2 or=0 3840x2160+0+0    # sway 管理的 toplevel，没有 buffer
```

我们的 per-toplevel 抓取（`ext-image-copy-capture` + `ext-foreign-toplevel-list`）只认**被管理的
toplevel**，而 OR 窗口按协议不在 `ext-foreign-toplevel-list` 里，所以永远抓不到；日志里持续出现
`has no buffer yet; retrying`。输出模式（不设 `GEMPRISM_TOPLEVEL_CAPTURE`）实测有画面、约 20fps，
证明游戏确实在画，只是画在另一个窗口上。

**KWin 为什么正常**：KWin 不是“挑一个窗口合成”，而是按当前输出的 stacking order 把**所有**
mapped 窗口（包括 override-redirect 的 X11 窗口）逐个合成；全屏的 OR 窗口在最上层，就盖住了
整个屏幕。“不受窗口管理”不等于“不参与合成”。

### 2.2 Wayland：游戏看到的屏幕几何和实际 surface 不一致

实测（2026-11-12 凌晨）：

- 嵌套输出 `HEADLESS-1` 是 `3840x2160 @ scale 1.0`（逻辑尺寸 = 物理尺寸）。
- 游戏 toplevel buffer 是 3840x2160，但内容只画在 **x=97..3276（3179x2160）**，右侧 564px 纯黑。
- KDE 直连（HDMI-A-1，3840x2160 @ scale 1.5，逻辑 2560x1440）截图里，窗口/内容约
  **3345x2160 贴左上，右侧露出桌面**——更像一个 2230x1440 逻辑的无边框窗口，而不是铺满
  2560x1440 的真全屏。

输入不是“整体偏移”，而是**有效输入区被压缩**：

| 注入点击 | 结果 |
| --- | --- |
| “返回標題”中心 `(2750,1955)` | 无反应 |
| `(3150,1955)` | 选中了 y≈1163 的“文字快進”左选项 |
| `(2688,390)`（想点“窗口模式”） | 无反应 |
| `(3839,924)`（想点效果音滑轨） | 无反应 |

按这一次命中的结果推算，游戏实际收到的坐标约在 surface 左上 60%（y 方向比例约 1/1.68）；x 方向
只确认了“落在左选项的文字范围内”，还不能唯一确定。这也解释了“点滑轨中间没反应”：点到的不是
滑轨（滑轨点击本身也没有响应，不能当坐标探针）。

运行时把嵌套输出改成 `scale 1.5` 后，游戏直接退出全屏（sway rect 回到 800x600），宿主仍停在
3840x2160，画面/输入都不可信。所以“对齐 KDE 逻辑几何”必须在**会话启动时**用 `SwayConfig`
建好，不能运行中改。

### 2.3 为什么“进出超分模式后就有画面”

不是回退逻辑。`SetMode` 在切换模式时会调用 `ToplevelCapture.RestartSession()`，销毁并重建抓取
会话；代码注释写明“新会话总会送第一帧，静态游戏不会黑屏”。日志对应：

```text
23:33:05  mode=upscale
23:33:06  stats captured=1 capture=50.88ms   # 重建会话补到一帧 3840x2160
23:33:07  mode=original                       # 继续显示最后一帧
23:33:08  rect=800x600                        # 退出全屏后抓取恢复正常
```

所以这是一次性补帧，不是稳定修复；静态菜单看不出来，画面一动就会露馅。

---

## 3. 可选方案（未实施，供后续版本评估）

### 3.1 X11：输出抓取回退

toplevel 抓取拿不到 buffer、且窗口矩形≈整个输出时，回退到输出抓取（`NestedCapture`）并按窗口
矩形裁剪；buffer 回来后切回 toplevel。这等价于 KWin“合成整个输出”的逻辑，不需要改 sway/wlroots。
成本约 0.5–1.5 人日，风险低。

### 3.2 Wayland A：对齐 KDE 逻辑几何 + 输入补偿

- 新会话按显示器的“逻辑尺寸 2560x1440 + scale 1.5”建嵌套输出，让游戏看到和 KDE 一样的桌面；
- 用 `GEMPRISM_TRACE_INPUT=1` + 一组标定点击，确认游戏把输入空间压成了多少；
- 在 `NestedInput` 里只对“全屏且画面有黑边”的窗口做反向补偿。

优点：保留游戏自己的全屏行为。风险：KDE 直连本身也是偏的，补偿系数要靠标定，换游戏/分辨率
可能失效。

### 3.3 Wayland B：不让游戏进它自己的全屏

游戏请求全屏时拦下来：让它保持窗口尺寸，宿主自己全屏显示 + 超分，输入走已验证的窗口路径。
对腐姬这种老游戏，实际效果可能是“画面正确、点击正确、还能超分”，比它自己的全屏更好。
代价：游戏内部会认为自己还在窗口模式（设置里的“全屏模式”会跳回窗口）。

---

## 4. 决策

**v1.0 发布不解决，记录为已知问题。**

理由：

- 只影响个别老游戏（腐姬）的游戏内全屏路径；秽翼等游戏正常，窗口模式和超分主路径不受影响。
- 根因在游戏/Wine 侧（KDE 直连同样异常）或 XWayland 的窗口类型，修复需要动输入模型、
  嵌套输出几何或增加输出抓取回退，风险和回归面都不小。
- v1.0 的验收重点是窗口化游戏 + 超分/还原的稳定性。

后续版本若重启这个问题，建议顺序：

1. 先做 X11 的输出抓取回退（独立、风险低）；
2. Wayland 按 3.2 做一次全新会话标定，再在 A/B 之间选；
3. 若只服务个别游戏，优先考虑 B（禁止游戏全屏、宿主全屏显示）。

---

## 5. 复现与取证

启动（腐姬，Lutris id 3）：

```bash
GEMPRISM_TOPLEVEL_CAPTURE=1 GEMPRISM_LOG=/tmp/gp-wl-fs.log GEMPRISM_LOG_LEVEL=Debug \
  ./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler \
  --nested --dev --monitor HDMI-A-1 --effect anime4k-vl -- \
  env LUTRIS_SKIP_INIT=1 lutris lutris:rungameid/3
```

抓取嵌套状态：

```bash
SOCK=$(grep -o 'swaysock=[^ ]*' /tmp/gp-wl-fs.log | tail -1 | cut -d= -f2)
swaymsg -s "$SOCK" -t get_outputs -r
swaymsg -s "$SOCK" -t get_tree -r
```

X11 全屏的窗口取证（腐姬 X11 配置，`PROTON_ENABLE_WAYLAND=0`）：

```bash
DISPLAY=:2 xwininfo -root -tree   # 或项目内的等价探针
```

---

## 6. 相关代码

| 位置 | 作用 |
| --- | --- |
| `src/GemPrismUpscaler.Core/Platform/Linux/Nested/ToplevelCapture.cs` | per-toplevel 抓取；只认 `ext-foreign-toplevel-list` |
| `src/GemPrismUpscaler.Core/Platform/Linux/Nested/NestedCapture.cs` | 整输出抓取（X11 回退方案的基础） |
| `src/GemPrismUpscaler.Core/Engine/NestedUpscaleSession.cs` | `SetMode` / `UpdateStateGeometry` / `RecreateCapture` |
| `src/GemPrismUpscaler.Core/Platform/Linux/Nested/NestedInput.cs` | 指针注入与坐标映射 |
| `src/GemPrismUpscaler.Core/Platform/Linux/Nested/SwayConfig.cs` | 嵌套输出分辨率/刷新率（目前不设 scale） |

相关记录：`.doc/minimize.md`（游戏自我最小化）、`.agent/M2b-新路线-验收问题分组-20261111.md` G4。