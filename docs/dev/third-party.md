# 第三方与许可

本项目以 **AGPL-3.0-or-later** 发布；完整文本见仓库根目录 `LICENSE`。第三方组件与参考项目如下。

## 1. 运行时与库

| 组件 | 用途 | 许可证 |
|---|---|---|
| .NET / Avalonia / Avalonia.Wayland | 桌面 UI 与 Wayland 后端 | MIT |
| Avalonia.Fonts.Inter | 界面字体 | Inter 字体 SIL OFL 1.1 |
| Silk.NET.Vulkan | Vulkan 绑定 | MIT |
| CommunityToolkit.Mvvm | MVVM 辅助 | MIT |
| Tomlyn | TOML 解析/序列化 | BSD-2-Clause |
| NWayland / NWayland.Protocols.Wlr | Wayland 协议绑定 | MIT |
| Tmds.DBus.Protocol | D-Bus / portal | MIT |
| Anime4K | 超分着色器 | MIT（见 `shaders/anime4k/LICENSE`） |

## 2. 打包的嵌套运行时

| 组件 | 许可证 | 说明 |
|---|---|---|
| sway 1.12 | MIT | 固定版本，随包分发。 |
| wlroots 0.20.2 | MIT | sway 依赖；包含本项目的小补丁，源码与补丁见发布源码。 |
| xdg-decoration-v2-shim | AGPL-3.0-or-later（本项目） | 本项目编写。 |
| gemprism-vkeyboard | AGPL-3.0-or-later（本项目）；协议文本 MIT | 基于 `virtual-keyboard-unstable-v1.xml` 的 helper。 |
| libliftoff | MIT | wlroots 的 DRM 输出依赖，随包分发。 |
| libdisplay-info | MIT | wlroots 的显示信息解析依赖。 |
| libseat / seatd | MIT | wlroots 的会话后端依赖。 |
| Lua 5.4 | MIT | wlroots 的配置/脚本依赖。 |

> 发布包包含 `SOURCE.txt` 与 `licenses/` 下的 sway/wlroots/Anime4K/可选库许可证，并在发布页提供源码获取方式。

## 3. 设计与参考

- [Magpie](https://github.com/Blinue/Magpie)：effect 管线和几何模型参考，GPL-3.0；**未复制代码**。
- [Anime4K](https://github.com/bloc97/Anime4K)：算法与 GLSL 来源，MIT；生成的 SPIR-V 是其衍生作品。
- FSR/FidelityFX：v1.0 **未包含**。

## 4. 发行注意

- `NOTICE` 必须与本文一致；当前 `NOTICE` 中 Tomlyn 的许可证应标为 BSD-2-Clause。
- `shaders/anime4k/LICENSE` 必须随包。
- 若后续加入新组件，更新本表与 `NOTICE`，并在发布清单中勾选。