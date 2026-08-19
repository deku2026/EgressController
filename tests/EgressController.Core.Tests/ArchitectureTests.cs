using System.Xml.Linq;

namespace EgressController.Core.Tests;

/// <summary>
/// Enforces the solution's dependency direction (plan §2.4) by scanning the real .csproj
/// files, not by trusting in-memory metadata. Breaking Rules→Proxy or pulling Avalonia into a
/// non-App project is a FAIL here, even if MSBuild would still link.
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] SrcProjects =
    {
        "EgressController.Core",
        "EgressController.Transport",
        "EgressController.Proxy",
        "EgressController.Windows",
        "EgressController.Rules",
        "EgressController.Launcher",
        "EgressController.Diagnostics",
        "EgressController.State",
        "EgressController.App",
    };

    // project(a) -> the set of src projects a is ALLOWED to reference.
    private static readonly Dictionary<string, HashSet<string>> AllowedRefs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EgressController.Core"] = new(StringComparer.OrdinalIgnoreCase),
        ["EgressController.Transport"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.Proxy"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core", "EgressController.Transport" },
        ["EgressController.Windows"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.Rules"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.Launcher"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.Diagnostics"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.State"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.App"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "EgressController.Core", "EgressController.Transport", "EgressController.Proxy",
            "EgressController.Windows", "EgressController.Rules", "EgressController.Launcher",
            "EgressController.Diagnostics", "EgressController.State",
        },
    };

    [Fact]
    public void Core_has_no_project_or_package_references()
    {
        var map = LoadProjectMap();
        Assert.Empty(map.ReferencesOf("EgressController.Core"));
        Assert.Empty(map.PackagesOf("EgressController.Core"));
    }

    [Theory]
    [MemberData(nameof(SrcProjectNames))]
    public void Project_references_respect_section_2_4_dependency_direction(string projectName)
    {
        var map = LoadProjectMap();
        var actual = map.ReferencesOf(projectName);
        var allowed = AllowedRefs[projectName];

        var disallowed = actual.Except(allowed, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(
            disallowed.Count == 0,
            $"{projectName} references projects outside its allowed set: {string.Join(", ", disallowed)}");
    }

    [Fact]
    public void Rules_must_never_reference_Proxy()
    {
        var map = LoadProjectMap();
        Assert.DoesNotContain(map.ReferencesOf("EgressController.Rules"), p => p == "EgressController.Proxy");
    }

    [Fact]
    public void Proxy_must_never_reference_Windows()
    {
        var map = LoadProjectMap();
        Assert.DoesNotContain(map.ReferencesOf("EgressController.Proxy"), p => p == "EgressController.Windows");
    }

    [Theory]
    [MemberData(nameof(NonAppProjectNames))]
    public void Only_App_may_reference_UI_assemblies(string projectName)
    {
        var map = LoadProjectMap();
        string[] uiPackages = map.PackagesOf(projectName)
            .Where(p => p.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
                        || p.Equals("CommunityToolkit.Mvvm", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(uiPackages.Length == 0, $"{projectName} references UI package(s): {string.Join(", ", uiPackages)}");
    }

    public static TheoryData<string> SrcProjectNames => new(SrcProjects);
    public static TheoryData<string> NonAppProjectNames
        => new(SrcProjects.Where(n => n != "EgressController.App"));

    private static ProjectMap LoadProjectMap() => ProjectMap.Load(FindSolutionRoot());

    private static string FindSolutionRoot()
    {
        // EgressController has no standalone solution file; its root is marked by the pair
        // Directory.Build.props + Directory.Packages.props that apply to all its projects.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the EgressController project root above test output.");
    }

    private sealed class ProjectMap
    {
        private readonly Dictionary<string, (HashSet<string> refs, HashSet<string> packages)> _byName =
            new(StringComparer.OrdinalIgnoreCase);

        public static ProjectMap Load(string root)
        {
            var map = new ProjectMap();
            string[] csprojs = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                            && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (string path in csprojs)
            {
                var name = Path.GetFileNameWithoutExtension(Path.GetFileName(path));
                var doc = XDocument.Load(path);
                XNamespace msb = "http://schemas.microsoft.com/developer/msbuild/2003";

                IEnumerable<string> refs = doc.Descendants(msb + "ProjectReference")
                    .Select(e => (string?)e.Attribute("Include"))
                    .Where(v => v is not null)
                    .Select(v => Path.GetFileNameWithoutExtension(Path.GetFileName(Path.GetFullPath(v!, root))))
                    .ToArray();

                IEnumerable<string> packages = doc.Descendants(msb + "PackageReference")
                    .Select(e => (string?)e.Attribute("Include"))
                    .Where(v => v is not null)
                    .Select(v => v!)
                    .ToArray();

                // XDocument reads the default namespace from the root element, but child
                // elements without an explicit prefix still live in it when the doc declares one.
                map._byName[name] = (new HashSet<string>(refs, StringComparer.OrdinalIgnoreCase),
                                     new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase));
            }

            return map;
        }

        public IReadOnlySet<string> ReferencesOf(string project)
            => _byName.TryGetValue(project, out var v) ? v.refs : throw new KeyNotFoundException(project);

        public IReadOnlySet<string> PackagesOf(string project)
            => _byName.TryGetValue(project, out var v) ? v.packages : throw new KeyNotFoundException(project);
    }
}