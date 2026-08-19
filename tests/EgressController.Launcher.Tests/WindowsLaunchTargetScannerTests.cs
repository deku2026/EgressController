using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public class WindowsLaunchTargetScannerTests
{
    [Fact]
    public void Scan_does_not_create_shortcut_catalog_entries_and_records_executable_membership()
    {
        IReadOnlyList<EgressController.Core.Models.LaunchTarget> targets = new WindowsLaunchTargetScanner().Scan();

        Assert.DoesNotContain(targets, target => target.Kind == EgressController.Core.Models.LaunchKind.Shortcut);
        Assert.DoesNotContain(targets, target => target.Kind is
            EgressController.Core.Models.LaunchKind.CliNative or
            EgressController.Core.Models.LaunchKind.CliWrapperResolved);
        Assert.DoesNotContain(targets, target => string.Equals(target.Source, "PATH", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target => target.CanonicalExecutable is not null && target.OwnedExecutables.Count > 0);
        foreach (EgressController.Core.Models.LaunchTarget packaged in targets.Where(target => target.Kind == EgressController.Core.Models.LaunchKind.PackagedAumid))
        {
            Assert.False(string.IsNullOrWhiteSpace(packaged.PackageFamily));
            Assert.False(string.IsNullOrWhiteSpace(packaged.Aumid));
            if (packaged.CanonicalExecutable is not null)
            {
                Assert.True(File.Exists(packaged.CanonicalExecutable));
                Assert.Contains(packaged.OwnedExecutables, path =>
                    string.Equals(path, packaged.CanonicalExecutable, StringComparison.OrdinalIgnoreCase));
            }
            if (packaged.IconPath is not null)
                Assert.True(File.Exists(packaged.IconPath), packaged.IconPath);
        }
        const string revo = @"C:\Program Files (x86)\Revo Uninstaller Pro\RevoUPPort.exe";
        if (File.Exists(revo))
        {
            EgressController.Core.Models.LaunchTarget? revoTarget = targets.FirstOrDefault(target => string.Equals(
                target.CanonicalExecutable,
                revo,
                StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(revoTarget);
            Console.WriteLine($"revo_found=true; revo_owned_executables={revoTarget!.OwnedExecutables.Count}");
        }

        Console.WriteLine($"targets={targets.Count}; icons={targets.Count(target => !string.IsNullOrWhiteSpace(target.IconPath))}; packaged={targets.Count(target => target.Kind == EgressController.Core.Models.LaunchKind.PackagedAumid)}");
    }
}
