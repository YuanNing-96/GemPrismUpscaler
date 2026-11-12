# 历史资料

这里的文档保留 M0–M3 的技术记录、问题排查和已归档方案。它们**描述当时的行为**，可能与 v1.0 的界面/命令不同；当前行为以 `docs/` 用户文档和[开发文档索引](../README.md)为准。

发布文档不包含开发机截图、日志、探针二进制和个人路径。原文中的证据链接已移除或改为文字说明。

## 阶段记录

| 文档 | 内容 |
|---|---|
| [里程碑摘要](milestones.md) | M0–M3 的目标、关键决策与结果 |
| [M0 可行性评估](m0-feasibility-review.md) | overlay 缩放、输入转发、范围收敛 |
| [M1b 测试报告](m1b-test-report.md) | 指标、脏矩形、Anime4K S/M/L、4K 数据 |
| [M1c 测试报告](m1c-test-report.md) | 设置/托盘/Profile/自定义 effect/VL |
| [M2a 开发记录](m2a-development.md) | Wayland layer-shell、XWayland 混合、portal |
| [M2a 崩溃排查](m2a-crash-investigation.md) | 拖拽/弹窗崩溃、热键恢复 |
| [M2b 单输出方案](m2b-single-output.md) | 单输出 + 按 toplevel 抓取 |
| [M2b 多输出设计](m2b-multi-output-design.md) | 早期“每窗口一个虚拟输出”方案（已归档） |
| [M2b 验收问题分组](m2b-acceptance-groups.md) | G1–G6 问题分类与结论 |
| [M2b 偶现问题](m2b-intermittent-issues.md) | 卡死/弹窗消失的排查记录 |
| [M2b SIGSEGV](m2b-sigsegv-dialog.md) | 关闭对话框时 NVIDIA WSI 崩溃与修复 |
| [M2b 已知问题](m2b-known-issues.md) | 发布前记录的边界问题 |
| [M2b 全屏策略](m2b-fullscreen-policy.md) | sway fullscreen 判定策略 |
| [M2b 自我最小化](m2b-self-minimize.md) | X11 黑帧处理、Wayland 归档 |
| [M2b 指针刷新](m2b-pointer-refresh.md) | 移除每秒绝对定位的兜底 |
| [M2b subsurface 评估](m2b-subsurface-kwin.md) | 对齐 KWin 的补丁成本 |
| [per-toplevel 抓取补丁](per-toplevel-capture-patch.md) | wlroots 补丁与构建方式 |
| [光标](cursor.md) | 命名形状、X11 跳转、图像光标 |
| [游戏内全屏](fullscreen.md) | X11/Wayland 全屏异常根因 |
| [自我最小化方案](minimize.md) | sway/wlroots 侧的彻底修复方案 |
| [指针跳转](pointer-warp.md) | `wp_pointer_warp_v1` 与坐标模型 |
| [零拷贝 spike](zero-copy-spike.md) | DRI3/DMA-BUF go/no-go |
| [逐版本更新记录](changelog.md) | v0.1–v0.5 的详细修复列表 |

## 说明

- 测试游戏名已匿名化：示例视觉小说 A / B / C。
- 机器路径、用户名、主机名已替换。
- 原文的 `开发机归档/` 证据路径不再随发布文档提供；需要时从开发机归档中查找。
- 旧文档中的链接与路径已更新为当前 `docs/` 结构。