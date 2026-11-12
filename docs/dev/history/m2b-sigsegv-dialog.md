> 历史资料：本文描述当时的行为，可能与 v1.0 当前版本不同。

# 修复：关闭对话框宿主窗口时 SIGSEGV（NVIDIA Wayland WSI）

> 日期：2026-11-11
> 现象：示例视觉小说 A（Lutris id=3）Wayland 模式，点“退出游戏”→ 弹出“确认结束”→ 点“否” →
> 应用 SIGSEGV，终端结束但游戏音乐还在响。
> 结论：**每个宿主窗口各自创建/销毁一个 `VkInstance`**，销毁时把 NVIDIA Wayland WSI 中
> 另一个窗口仍在用的状态一起拆了。现在全进程共享一个 `VkInstance`。

## 1. 崩溃证据

- `coredumpctl`：`SIGSEGV`，`nested-upscale` 线程（渲染线程）。
- 内核：`segfault at 0 ip 0000000000000000 error 14` → 取指地址为 0，即跳到了空函数指针
  （error 14 = user + instruction fetch + not present）。
- 栈（gdb，无符号）：`#1 0x…eb883`、`#2 0x…951b2`，两者都在
  `libnvidia-glcore.so.615.78.08` 的同一偏移；两次崩溃（游戏 + 合成复现）偏移完全一致。
- 日志最后几行：

```text
01:20:33.481 wayland host: buffer=196x82 ... dialog=False   ← 对话框宿主窗口
01:20:36.403 toplevel capture 'khimedl_chs.exe' session stopped
01:20:37.461 nested window removed; windows=1               ← 对话框状态销毁
01:20:37.461 nested host focus id=6 focused=true            ← 焦点回到主窗口
01:20:37.639 stats ... windows=1
（01:20:38 SIGSEGV）
```

主窗口在对话框销毁后要继续 present，下一次 `vkQueuePresentKHR` 就跳空指针了。

## 2. 不做游戏的稳定复现

不需要启动游戏，也不需要点游戏菜单：

1. 启动一个常驻 nested 会话（`GEMPRISM_TOPLEVEL_CAPTURE=1`）；
2. 在嵌套里开 `alacritty`（持续输出，保证一直有帧）；
3. 再开 `zenity --question`（第二个宿主窗口）；
4. 杀掉 zenity（等于用户点“否”关掉对话框）；
5. 约 5 秒内必崩，core 与游戏那次同偏移。

脚本见 `/tmp/repro.sh`、`/tmp/repro2.sh`、`/tmp/repro3.sh`（临时文件，未提交）。

## 3. 定位：销毁了什么才崩

临时加了 `GEMPRISM_VK_RETAIN`（已删除）来保留部分对象，逐个跑上面的复现：

| 保留对象 | 销毁的 | 结果 |
|---|---|---|
| 无（原行为） | swapchain、device、surface、instance | **CRASH** |
| surface + instance | swapchain、device | ALIVE |
| instance（只销毁 surface） | surface、swapchain、device | ALIVE |
| device + surface + instance | swapchain、其余 device 对象 | ALIVE |

结论：**单独销毁 surface 安全，单独销毁 instance 安全；两者都销毁，下一次另一个窗口
present 就崩**。所以每窗口一个 instance 的模型在 NVIDIA Wayland WSI 上不可用。

（顺带记录：`vkDestroySwapchainKHR`、`vkDestroyDevice` 不是触发点。）

## 4. 修复

`Rendering/VulkanInstanceCache.cs`：按平台扩展集（wayland / xlib）各缓存一个
`VkInstance`，进程内共享、永不销毁。`VulkanBlitter.CreateInstance` 从缓存取；
`Dispose` 仍然销毁自己的 swapchain / device / surface，但**不再销毁 instance**。

- 每个窗口仍有自己的 `VkDevice`、swapchain、surface，互不影响。
- 实例只在第一次需要时创建一次（日志 `vulkan instance created for [...]`）。
- 进程退出时由系统回收实例，不再走“销毁最后一份”的路径。

## 5. 验证

- 5 轮 `zenity --question` 开关（期间 alacritty 持续 5 fps present）：
  `ALIVE`，日志 `vulkan instance created` 1 次、`nested window removed` 5 次，无 core。
- 对话框出现在超分全屏状态、关闭后再退出超分：`ALIVE`，
  `reconfigured logical=2560x1440 buffer=3840x2160 fullscreen=True` → `mode=upscale`，
  返回 `mode=original client=800x540`，无异常。
- 单窗口路径（zenity、游戏）行为不变。

## 6. 待用户复验

用示例视觉小说 A 原步骤（退出游戏 → 确认框 → 点“否”）再跑一次；如果仍崩，把 `/tmp/gp-b1.log`
和 `coredumpctl info` 一起发来。注意崩溃分析需要 core dump：
`coredumpctl list --since ...` / `coredumpctl gdb <pid>`。