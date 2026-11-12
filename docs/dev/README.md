# 开发文档

> v1.0 的用户界面、命令行和限制以 `docs/` 下的用户文档为准。本目录中的计划/历史文档保留开发过程信息，可能提到已废弃的界面或旧版本行为。

## 1. 当前文档

| 文档 | 内容 |
|---|---|
| [架构设计](architecture.md) | 目标、数据流、X11/Wayland/嵌套后端、关键决策 |
| [路线图](roadmap.md) | M0–M3 阶段、验收标准、版本对应 |
| [M2 计划](m2-plan.md) | M2a/M2b/M2c 的范围、实现与验收（历史+现状） |
| [M2c 计划](m2c-plan.md) | 嵌套 DMA-BUF 零拷贝与色彩收尾（v1.0 后可选） |
| [v1.0 后计划](post-v1.0.md) | 推迟项、重启条件 |
| [功能 backlog](backlog.md) | 未排期功能与明确不做项 |
| [自定义 effect ABI](effects.md) | TOML/SPIR-V/绑定/push constant |
| [诊断与日志](diagnostics.md) | 日志、轮转、诊断包、脱敏、崩溃快照 |
| [开发与测试](testing.md) | 构建、测试、着色器、嵌套运行时、手工冒烟 |
| [发布清单](release-checklist.md) | v1.0 发布物结构与验收项 |
| [第三方与许可](third-party.md) | 依赖、许可证、源码说明 |
| [历史资料](history/README.md) | M0–M3 技术记录、问题排查、归档方案 |

## 2. 快速构建

```bash
dotnet build GemPrismUpscaler.slnx -c Release
dotnet test  GemPrismUpscaler.slnx -c Release
```

当前基线：0 warning / 0 error，测试 150/150。

运行：

```bash
./src/GemPrismUpscaler/bin/Release/net10.0/GemPrismUpscaler
```

## 3. 代码结构

```text
src/GemPrismUpscaler/             Avalonia GUI、CLI 入口、托盘
src/GemPrismUpscaler.Core/        捕获/呈现/输入/超分/配置/诊断
  Engine/                         会话生命周期（X11/portal/嵌套）
  Platform/Linux/                 X11、Wayland、portal、嵌套合成器
  Rendering/                      Vulkan、Anime4K、Lanczos、effect 注册
  Configuration/                  TOML 配置、路径、Profile
  Diagnostics/                    日志、指标、错误总线、诊断包
tests/GemPrismUpscaler.Tests/     单元测试与几何/解析测试
shaders/                          Anime4K GLSL、生成的 SPIR-V、Lanczos
tools/                            着色器构建、嵌套运行时、探针、发布脚本
docs/                             用户文档、开发文档、示例
```

## 4. 阅读顺序

1. 先看[架构设计](architecture.md)的 §2–§3 和 §6.9。
2. 再看[诊断与日志](diagnostics.md)和[开发与测试](testing.md)。
3. 需要改嵌套路径时看[历史：单输出方案](history/m2b-single-output.md)和
   [历史：per-toplevel 抓取补丁](history/per-toplevel-capture-patch.md)。
4. 需要重启推迟项时看[v1.0 后计划](post-v1.0.md)。