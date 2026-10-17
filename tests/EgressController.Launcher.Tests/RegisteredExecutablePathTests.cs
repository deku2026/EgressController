using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public sealed class RegisteredExecutablePathTests
{
    [Theory]
    [InlineData(@"C:\Program Files\AI App\app.exe", false, @"C:\Program Files\AI App\app.exe")]
    [InlineData("\"C:\\Program Files\\AI App\\app.exe\"", false, @"C:\Program Files\AI App\app.exe")]
    [InlineData(@"C:\Program Files\AI App\app.exe,0", true, @"C:\Program Files\AI App\app.exe")]
    [InlineData("\"C:\\Program Files\\AI App\\app.exe\", -3", true, @"C:\Program Files\AI App\app.exe")]
    [InlineData(@"C:\Apps\one,two\app.exe,1", true, @"C:\Apps\one,two\app.exe")]
    [InlineData(@"%SystemRoot%\System32\app.exe", false, @"%SystemRoot%\System32\app.exe")]
    public void File_paths_preserve_spaces_and_only_strip_an_icon_index(string value, bool icon, string expected)
        => Assert.Equal(Environment.ExpandEnvironmentVariables(expected), RegisteredExecutablePath.Parse(value, icon));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.exe")]
    [InlineData("@resource.dll,-1")]
    [InlineData("\"C:\\Apps\\app.exe")]
    [InlineData("\"C:\\Apps\\app.exe\" --flag")]
    public void Malformed_and_resource_entries_are_not_interpreted_as_files(string? value)
        => Assert.Null(RegisteredExecutablePath.Parse(value, icon: true));
}
