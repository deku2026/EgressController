using EgressController.Launcher.Discovery;
using EgressController.Core.Models;

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
            var complete = ExecutableInventory.CollectChecked([root], token: TestContext.Current.CancellationToken);
            Assert.Equal(2, complete.Count);
            Assert.Contains(helper, complete);
            Assert.Throws<OperationCanceledException>(() => ExecutableInventory.CollectChecked([root], null, new CancellationToken(true)));
            string gameRoot = Path.Combine(root, "steamapps", "common", "Example");
            Directory.CreateDirectory(gameRoot); string game = Path.Combine(gameRoot, "game.exe"); File.WriteAllBytes(game, [0]);
            var client = new LaunchTarget { Id = "steam", Name = "Steam", CanonicalExecutable = Path.Combine(root, "steam.exe"), OwnedRoots = [root] };
            var excluded = ApplicationInventoryBoundaries.ExcludedRoots(client, []);
            Assert.DoesNotContain(game, ExecutableInventory.CollectChecked([root], null, TestContext.Current.CancellationToken, excluded));
            Assert.Contains(game, ExecutableInventory.CollectChecked([gameRoot], token: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected fixture directory");
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_directory_cannot_be_saved_as_a_complete_inventory()
        => Assert.Throws<IOException>(() => ExecutableInventory.CollectChecked([Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"))], token: TestContext.Current.CancellationToken));
}
