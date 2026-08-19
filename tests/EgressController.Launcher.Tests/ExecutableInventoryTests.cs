using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public class ExecutableInventoryTests
{
    [Fact]
    public void Collect_walks_application_root_recursively_and_only_keeps_exe_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "egress-inventory-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "runtime", "bin");
        Directory.CreateDirectory(nested);
        try
        {
            string main = Path.Combine(root, "app.exe");
            string helper = Path.Combine(nested, "helper.exe");
            string text = Path.Combine(nested, "readme.txt");
            File.WriteAllBytes(main, [0]);
            File.WriteAllBytes(helper, [0]);
            File.WriteAllText(text, "not an executable");

            IReadOnlyList<string> result = ExecutableInventory.Collect([root]);

            Assert.Contains(result, path => string.Equals(path, main, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result, path => string.Equals(path, helper, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result, path => string.Equals(path, text, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
