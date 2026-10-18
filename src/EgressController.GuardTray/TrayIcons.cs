using Avalonia.Controls;
using EgressController.Guardian;
using EgressController.Presentation;

namespace EgressController.GuardTray;

internal static class TrayIcons
{
    public static IReadOnlyDictionary<GuardVisualState, WindowIcon> Create()
        => Enum.GetValues<GuardVisualState>().ToDictionary(state => state, GuardianIcon.Create);
}
