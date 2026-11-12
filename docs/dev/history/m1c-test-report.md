> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# M1c 测试报告

- 日期：2026-11-05
- 版本：`0.3.0`
- 范围：M1c 新增功能的主要路径；**按用户约定不做完整回归**，边界留 v1.0
- 环境：KDE Plasma 6 Wayland + XWayland；HDMI-A-1 3840×2160@144（HDR/150%）；RTX 4060 Laptop 8 GB

## 1. 自动化

```text
dotnet build GemPrismUpscaler.slnx -c Release   0 code warning / 0 error（3 条 NU1900 为只读 NuGet 缓存环境警告）
dotnet test  GemPrismUpscaler.slnx -c Release   91/91
--version                                       GemPrismUpscaler v0.3.0
```

新增测试：

- `M1cConfigTests`：默认值、TOML 往返、手写 TOML、损坏回退、非法值回退、缺失文件。
- `M1cHotkeyTests`：热键解析/规范化/非法输入。
- `M1cProfileTests`：通配符、class/exe 匹配、先匹配先赢、空配置。
- `M1cEffectTests`：合法自定义 effect、缺 shader、未知输入、错误 abi。
- `M1bShaderTests`：更新为 S/M/L/VL manifest、pass/纹理/radius、push constant 上限（VL 176B）。

## 2. 自定义 effect 端到端

样例：`docs/examples/effects/sharpen-2x/`（2× + unsharp mask + Lanczos-2）。

```bash
cp -r docs/examples/effects/sharpen-2x 开发机归档/m1c-tmp/effects/
GEMPRISM_CONFIG=开发机归档/m1c-tmp/config.toml \
GEMPRISM_LOG=开发机归档/m1c-tmp/custom.log \
XDG_RUNTIME_DIR=开发机归档/m1c-tmp/run-cli \
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler \
  --window 0x2000001 --effect sharpen-2x --monitor HDMI-A-1
```

结果：

```text
custom effect loaded: sharpen-2x (.../effect.toml)
engine started version=0.3.0 effect=sharpen-2x ... device='NVIDIA GeForce RTX 4060 Laptop GPU'
stats fps=1.0 captured=1 presented=1 ... effect=28.05ms ... vram=752MiB
```

- 无 Vulkan 校验错误；静态测试图案只呈现一帧，随后 0 capture/0 present。
- 说明：`effect=28.05ms` 含首次 pipeline 创建，不是稳态。

## 3. Anime4K VL 端到端

```bash
GEMPRISM_CONFIG=开发机归档/m1c-tmp/config.toml \
GEMPRISM_LOG=开发机归档/m1c-tmp/vl.log \
XDG_RUNTIME_DIR=开发机归档/m1c-tmp/run-cli \
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler \
  --window 0x2000001 --effect anime4k-vl --monitor HDMI-A-1
```

结果：

```text
engine started version=0.2.2 effect=anime4k-vl ... device='NVIDIA GeForce RTX 4060 Laptop GPU'
stats fps=1.0 captured=1 presented=1 ... effect=466.79/466.79ms ... vram=880/8188MiB
```

- manifest：VL = 18 pass / 19 纹理 / radius 8 / push constant 176B。
- 无 Vulkan 校验错误；静态图案只呈现一帧。
- 首帧 466ms 主要来自 18 个 pipeline 的创建；按用户约定 VL 帧率不作为验收门槛。

## 4. 托盘与 UI

- `tray created (StatusNotifierItem/AppIndicator)`。
- 通过 `org.kde.StatusNotifierItem.Activate` 模拟托盘点击：
  - 应用日志：`tray: show settings (visible=True active=False state=Normal)`；
  - 普通 `Activate()` 后 `active=False`；
  - KWin 兜底脚本启动后，KWin 活动窗口从 DSH 变为 `宝石棱镜 GemPrismUpscaler v0.2.2`。
- 左侧 5 Tab 截图：`开发机归档/m1c-tmp/ui-final.png`。
- 未用输入注入测试 ComboBox 交互；KDE Wayland 4K/150% 下的下拉框拖动问题按约定不修，记录 backlog §7。

## 5. 未覆盖 / 已知问题

- M1b 完整回归（static/repaint/dirty AE/4K S/M/L/弹窗/焦点）未跑，留 v1.0。
- 动画未做。
- VL 稳态帧率未测；不设门槛。
- ComboBox 下拉列表在 4K/150% 拖动窗口时停留在旧位置；DP 100% 正常；不修。
- 构建时 3 条 NU1900 为只读 NuGet 缓存环境问题。
- 自定义 effect 的 graphics 最终 pass、Anime4K UL 未做。

## 6. 证据索引

- 日志：未随发布文档分发。
- 截图：未随发布文档分发。
- 样例：`docs/examples/effects/sharpen-2x/`
- ABI 文档：`docs/dev/effects.md`
- 用户手测：`开发机归档/M1c-用户验收步骤.md`
