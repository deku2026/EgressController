using Avalonia.Media;
using EgressController.Presentation;

namespace EgressController.App.ViewModels;

public sealed record NavigationItem(string Key, string Title, string Description)
{
    public Geometry Icon { get; } = Icons.For(Key);
}
