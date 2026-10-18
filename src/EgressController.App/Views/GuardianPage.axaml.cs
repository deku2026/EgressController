using Avalonia.Controls;
namespace EgressController.App.Views;

public sealed partial class GuardianPage : PageView
{
    public GuardianPage()
    {
        InitializeComponent();
        Surface.OpenDirectoryRequested += path => (TopLevel.GetTopLevel(this) as MainWindow)?.OpenLogDirectoryPath(path);
    }
}
