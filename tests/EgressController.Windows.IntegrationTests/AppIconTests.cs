using EgressController.App;
using System.Drawing;

namespace EgressController.Windows.IntegrationTests;

public class AppIconTests
{
    [Fact]
    public void System_drawing_can_extract_the_same_icon()
    {
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        using Icon? icon = Icon.ExtractAssociatedIcon(executable);
        Assert.NotNull(icon);
        using Bitmap bitmap = icon.ToBitmap();
        Assert.True(bitmap.Width > 0);
    }
}
