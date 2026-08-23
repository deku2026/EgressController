using System.Xml.Linq;

namespace EgressController.Core.Tests;

/// <summary>Checks that the only product data plane is sing-box and that UI dependencies stay in App.</summary>
public class ArchitectureTests
{
    private static readonly string[] SrcProjects =
    [
        "EgressController.Core",
        "EgressController.Transport",
        "EgressController.Windows",
        "EgressController.Rules",
        "EgressController.Launcher",
        "EgressController.Diagnostics",
        "EgressController.State",
        "EgressController.SingBox",
        "EgressController.ElevatedHost",
        "EgressController.App",
    ];

    private static readonly Dictionary<string, HashSet<string>> AllowedRefs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EgressController.Core"] = new(StringComparer.OrdinalIgnoreCase),
        ["EgressController.Transport"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.Windows"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.Rules"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.Launcher"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.Diagnostics"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.State"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.SingBox"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "EgressController.Core", "EgressController.State", "EgressController.Transport",
        },
        ["EgressController.ElevatedHost"] = new(StringComparer.OrdinalIgnoreCase) { "EgressController.Core" },
        ["EgressController.App"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "EgressController.Core", "EgressController.Transport", "EgressController.Windows",
            "EgressController.Rules", "EgressController.Launcher", "EgressController.Diagnostics",
            "EgressController.State", "EgressController.SingBox",
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
    public void Project_references_respect_the_sing_box_dependency_direction(string projectName)
    {
        var map = LoadProjectMap();
        string[] disallowed = map.ReferencesOf(projectName)
            .Except(AllowedRefs[projectName], StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Empty(disallowed);
    }

    [Fact]
    public void No_legacy_proxy_project_or_system_proxy_symbols_remain()
    {
        string root = FindSolutionRoot();
        string[] forbidden =
        [
            "EgressController.Proxy",
            "LocalProxyServer",
            "SystemProxyManager",
            "ProxyStateStore",
            "StrictDomainListParser",
            "DomainMatcher",
            "LocalProxyEnvironment",
            "WindowsRuntimeProxyPolicy",
        ];
        string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !path.EndsWith("ArchitectureTests.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".slnx" or ".md" or ".ps1")
            .ToArray();

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (string symbol in forbidden)
                Assert.DoesNotContain(symbol, text, StringComparison.Ordinal);
        }
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
        Assert.Empty(uiPackages);
    }

    public static TheoryData<string> SrcProjectNames => new(SrcProjects);
    public static TheoryData<string> NonAppProjectNames
        => new(SrcProjects.Where(name => name != "EgressController.App"));

    private static ProjectMap LoadProjectMap() => ProjectMap.Load(FindSolutionRoot());

    private static string FindSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate the EgressController project root.");
    }

    private sealed class ProjectMap
    {
        private readonly Dictionary<string, (HashSet<string> refs, HashSet<string> packages)> _byName =
            new(StringComparer.OrdinalIgnoreCase);

        public static ProjectMap Load(string root)
        {
            var map = new ProjectMap();
            foreach (string path in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                         .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                     && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                XDocument doc = XDocument.Load(path);
                XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
                var references = doc.Descendants(msbuild + "ProjectReference")
                    .Select(item => (string?)item.Attribute("Include"))
                    .Where(value => value is not null)
                    .Select(value => Path.GetFileNameWithoutExtension(Path.GetFileName(Path.GetFullPath(value!, root))))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var packages = doc.Descendants(msbuild + "PackageReference")
                    .Select(item => (string?)item.Attribute("Include"))
                    .Where(value => value is not null)
                    .Select(value => value!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                map._byName[name] = (references, packages);
            }
            return map;
        }

        public IReadOnlySet<string> ReferencesOf(string project)
            => _byName.TryGetValue(project, out var value) ? value.refs : throw new KeyNotFoundException(project);

        public IReadOnlySet<string> PackagesOf(string project)
            => _byName.TryGetValue(project, out var value) ? value.packages : throw new KeyNotFoundException(project);
    }
}
