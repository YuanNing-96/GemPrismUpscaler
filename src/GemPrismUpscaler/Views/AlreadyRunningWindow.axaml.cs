using Avalonia.Controls;
using GemPrismUpscaler.Core.Platform.Linux;
using System;

namespace GemPrismUpscaler.Views;

public partial class AlreadyRunningWindow : Window
{
    public AlreadyRunningWindow()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close();
    }

    public AlreadyRunningWindow(InstanceInfo info) : this()
    {
        MessageText.Text =
            $"已有实例正在运行：{info.DisplayText}。\n\n" +
            "本次启动不会创建第二个覆盖层，也不会激活已有窗口。\n" +
            "请关闭已有实例，或直接使用它。\n\n" +
            $"如果覆盖层正在显示，可能遮住此窗口；请按 Alt+Shift+A 隐藏覆盖层，或查看桌面通知。\n\n" +
            $"启动时间：{info.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }
}
