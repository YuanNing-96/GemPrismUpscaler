# 开发与测试

## 1. 构建与单元测试

```bash
dotnet build GemPrismUpscaler.slnx -c Release
dotnet test  GemPrismUpscaler.slnx -c Release
```

基线要求：

- 0 warning / 0 error；
- 测试全部通过（v1.0 基线 150/150）。

测试覆盖几何/坐标换算、配置校验、命令行 argv 解析、嵌套状态、日志/诊断辅助等；不包含真实 GPU/合成器的自动化回归。

## 2. 运行

```bash
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler
```

常用 CLI 冒烟：

```bash
./GemPrismUpscaler --version
./GemPrismUpscaler --help
./GemPrismUpscaler --list
./GemPrismUpscaler --doctor
./GemPrismUpscaler --config-check
./GemPrismUpscaler --diagnose
```

退出码：0 成功、1 运行/配置失败、2 用法错误、3 环境检查失败、4 已有实例。

## 3. 着色器

内置 Anime4K S/M/L/VL 的 SPIR-V 和 manifest 已嵌入 Core；运行时不依赖 glslang。

重新生成（需要 `glslangValidator`、`spirv-val`）：

```bash
python3 tools/build_shaders.py
```

来源、pass 数和 manifest 说明见 [shaders/README.md](../../shaders/README.md)。

## 4. 嵌套运行时

产品发布包带固定版本：

```text
runtime/sway/sway
runtime/sway/swaymsg
runtime/sway/libwlroots-0.20.so
runtime/sway/libliftoff.so.0
runtime/sway/libdisplay-info.so.3
runtime/sway/libseat.so.1
runtime/sway/liblua5.4.so.5.4
runtime/sway/xdg-decoration-v2-shim.so
runtime/gemprism-vkeyboard
```

从源码构建（开发用）：

```bash
tools/build-nested-sway.sh
tools/build-xdg-decoration-v2-shim.sh
tools/build-gemprism-vkeyboard.sh
```

`GEMPRISM_SWAY`、`GEMPRISM_SWAYMSG`、`GEMPRISM_DECORATION_SHIM`、`GEMPRISM_VKEYBOARD` 可覆盖路径。

## 5. 手工冒烟

v1.0 不做完整回归矩阵，但每次发布前至少确认：

1. 无参数启动 GUI；三个页签；深色主题。
2. 嵌套启动：关闭 Lutris，填命令，开始；画面/鼠标/键盘/滚轮正常。
3. 暂停/继续：切回原窗口再切回超分；不结束进程。
4. 停止：退出会话，无 sway/XWayland/宿主残留。
5. 关闭游戏窗口：显示“游戏已退出”，不误报启动失败。
6. 已有 X11 窗口：列表选择、开始、暂停、停止。
7. 设置保存、重复打开行为、关闭到托盘/直接退出。
8. 日志页、打开日志目录、导出诊断包。
9. `--doctor` 无 `[FAIL]`；`--config-check` 对示例配置 exit 0。
10. 新用户目录解压即用（见[发布清单](release-checklist.md)）。

## 6. 手工排查工具

| 工具 | 用途 |
|---|---|
| `tools/testpattern` | 静态/脏块/动画测试图案 |
| `tools/bench-m1b.sh` | 性能与脏矩形 A/B |
| `tools/gemprism-nested-run` | 向常驻嵌套会话启动命令 |
| `tools/gp-forensics.sh` | 进程/窗口/日志取证 |
| `tools/zero-copy/` | DRI3/DMA-BUF 探针（历史） |

这些是开发工具，不随发布包分发。