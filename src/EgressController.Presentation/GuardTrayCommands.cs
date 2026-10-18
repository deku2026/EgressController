using EgressController.Guardian;

namespace EgressController.Presentation;

/// <summary>The actual companion menu, reused verbatim by the isolated preview.</summary>
public static class GuardTrayCommands
{
    public static IReadOnlyList<TrayCommand> Create(GuardManagementViewModel state, Action openMain, Action show, Action exit)
        => [
            new("打开主程序", null, "open", () => { openMain(); return Task.CompletedTask; }),
            new("守护状态与设置", state.StateLabel, "guardian", () => { show(); return Task.CompletedTask; }),
            new("启动守护", null, "protection", async () => { await state.StartCommand.ExecuteAsync(null); show(); }, () => state.CanStart),
            new("停止守护", "本次开机不自动拉起", "power", async () => { await state.StopCommand.ExecuteAsync(null); show(); }, () => state.CanStop, Danger: true),
            new("退出小托盘", "守护服务继续运行", "power", () => { exit(); return Task.CompletedTask; })
        ];
}
