// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using EgressController.Mac;

namespace EgressController.Mac.Tests;

public sealed class CatalogTests
{
    [Fact]
    public async Task DiscoveryDoesNotScanMembershipOrPublishNestedHelperAsAnApplication()
    {
        var (files, metadata, catalog) = Fixture();
        var result = await catalog.DiscoverAsync(TestContext.Current.CancellationToken);
        var app = Assert.Single(result.Applications);
        Assert.True(result.IsComplete);
        Assert.Equal("org.example.desktop", app.BundleIdentifier);
        Assert.Empty(app.ExecutablePaths);
        Assert.False(app.InventoryReady);
        Assert.Equal(0, files.MachOReads);
        Assert.Equal(1, metadata.ReadCount);
    }

    [Fact]
    public async Task ExplicitInventoryIncludesExactMainAndNestedMachOHelpersButNotTextOrNewSiblings()
    {
        var (files, _, catalog) = Fixture();
        var app = Assert.Single((await catalog.DiscoverAsync(TestContext.Current.CancellationToken)).Applications);
        string old = app.BundlePath + "/Contents/Frameworks/old-helper";
        var inventory = await catalog.BuildInventoryAsync(app, [old], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(inventory.IsComplete);
        Assert.Equal(3, inventory.ExecutablePaths.Length);
        Assert.Contains(app.PrimaryExecutablePath!, inventory.ExecutablePaths);
        Assert.Contains(app.BundlePath + "/Contents/Frameworks/Helper.app/Contents/MacOS/Helper", inventory.ExecutablePaths);
        Assert.Contains(old, inventory.ExecutablePaths);
        Assert.DoesNotContain(app.BundlePath + "/Contents/readme.txt", inventory.ExecutablePaths);
        var saved = new MacManagedApplication(app, "home", []).AcceptExplicitInventory(inventory);
        string newHelper = app.BundlePath + "/Contents/MacOS/new-helper";
        files.AddFile(newHelper, executable: true);
        Assert.False(saved.ContainsExecutable(newHelper));
        Assert.False(saved.ContainsExecutable(app.BundlePath + "/Contents/MacOS/ExampleExtra"));
        Assert.True(saved.ContainsExecutable(old));
    }

    [Fact]
    public async Task RouteAndMetadataChangesPreserveMissingOldPathsAndDoNotEnrollNewHelpers()
    {
        var (files, _, catalog) = Fixture();
        var app = Assert.Single((await catalog.DiscoverAsync(TestContext.Current.CancellationToken)).Applications);
        string old = app.BundlePath + "/Contents/MacOS/previous-version";
        var saved = new MacManagedApplication(app, "home", [old]).AcceptExplicitInventory(await catalog.BuildInventoryAsync(app, [old], cancellationToken: TestContext.Current.CancellationToken));
        string helper = app.BundlePath + "/Contents/MacOS/new-version";
        files.AddFile(helper, executable: true);
        var refreshed = Assert.Single((await catalog.DiscoverAsync(TestContext.Current.CancellationToken)).Applications) with { DisplayName = "Renamed Example" };
        var changed = saved.WithRoute("global-proxy").RefreshMetadata(refreshed);
        Assert.Equal(saved.SavedExecutablePaths, changed.SavedExecutablePaths);
        Assert.Contains(old, changed.SavedExecutablePaths);
        Assert.False(changed.ContainsExecutable(helper));
        var rescanned = changed.AcceptExplicitInventory(await catalog.BuildInventoryAsync(refreshed, changed.SavedExecutablePaths, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(rescanned.ContainsExecutable(helper));
        Assert.True(rescanned.ContainsExecutable(old));
        Assert.Equal("global-proxy", rescanned.RouteId);
    }

    [Fact]
    public async Task ExternalSymlinksMakeInventoryIncompleteWithoutEnrollingSharedTool()
    {
        var (files, _, catalog) = Fixture();
        var app = Assert.Single((await catalog.DiscoverAsync(TestContext.Current.CancellationToken)).Applications);
        files.AddLink(app.BundlePath + "/Contents/MacOS/shared-tool", "/usr/local/bin/shared-tool", false);
        var result = await catalog.BuildInventoryAsync(app, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Diagnostics, item => item.Code == "outside-bundle");
        Assert.DoesNotContain("/usr/local/bin/shared-tool", result.ExecutablePaths);
        Assert.Throws<InvalidOperationException>(() => new MacManagedApplication(app, "home", []).AcceptExplicitInventory(result));
    }

    [Fact]
    public async Task SharedRootsAndIdentityReplacementCannotBeEnrolled()
    {
        var (_, _, catalog) = Fixture();
        var app = Assert.Single((await catalog.DiscoverAsync(TestContext.Current.CancellationToken)).Applications);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.BuildInventoryAsync(app with { BundlePath = "/Applications" }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.BuildInventoryAsync(app with { BundlePath = "/usr/local/bin" }, cancellationToken: TestContext.Current.CancellationToken));
        var saved = new MacManagedApplication(app, "home", []);
        Assert.Throws<ArgumentException>(() => saved.RefreshMetadata(app with { Id = "bundle:other.publisher:/Applications/Example.app" }));
    }

    [Fact]
    public async Task DeniedSubdirectoryIsReportedAndCannotReplaceSavedSelection()
    {
        var (files, _, catalog) = Fixture();
        var app = Assert.Single((await catalog.DiscoverAsync(TestContext.Current.CancellationToken)).Applications);
        files.Denied.Add(app.BundlePath + "/Contents/Frameworks");
        var inventory = await catalog.BuildInventoryAsync(app, ["/Applications/Example.app/Contents/MacOS/older"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(inventory.IsComplete);
        Assert.Contains(inventory.Diagnostics, item => item.Code == "inventory-incomplete");
        Assert.Contains("/Applications/Example.app/Contents/MacOS/older", inventory.ExecutablePaths);
    }

    [Fact]
    public async Task EntryBudgetAndCancellationStopScansWithoutPublishingPartialMembership()
    {
        var (files, metadata, catalog) = Fixture();
        var app = Assert.Single((await catalog.DiscoverAsync(TestContext.Current.CancellationToken)).Applications);
        var limited = new MacApplicationCatalog(files, metadata, ["/Applications"], maximumEntries: 1);
        Assert.False((await limited.BuildInventoryAsync(app, cancellationToken: TestContext.Current.CancellationToken)).IsComplete);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.BuildInventoryAsync(app, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task ChangedPrimaryEntryBecomesPendingAndForgedInventoryCannotAddSharedTools()
    {
        var (_, _, catalog) = Fixture();
        var app = Assert.Single((await catalog.DiscoverAsync(TestContext.Current.CancellationToken)).Applications);
        var saved = new MacManagedApplication(app, "home", []).AcceptExplicitInventory(await catalog.BuildInventoryAsync(app, cancellationToken: TestContext.Current.CancellationToken));
        var metadata = app with { PrimaryExecutablePath = app.BundlePath + "/Contents/MacOS/new-main" };
        var pending = saved.RefreshMetadata(metadata);
        Assert.False(pending.Application.InventoryReady);
        Assert.Equal(saved.SavedExecutablePaths, pending.SavedExecutablePaths);
        var forged = new MacExecutableInventory(app.Id, app.BundlePath, ["/usr/local/bin/shared-tool"], true, []);
        Assert.Throws<ArgumentException>(() => saved.AcceptExplicitInventory(forged));
    }

    [Theory]
    [InlineData(0xfeedfacfu, true)]
    [InlineData(0xcffaedfeu, true)]
    [InlineData(0xcafebabeu, true)]
    [InlineData(0xcafebabfu, true)]
    [InlineData(0x4d5a0000u, false)]
    [InlineData(0x23212f62u, false)]
    public void MagicRecognitionIsOnlyAFormatHintNotAnExecutableMembershipVerdict(uint magic, bool expected)
        => Assert.Equal(expected, PhysicalMacCatalogFileSystem.IsMachOMagic(magic));

    [Fact]
    public async Task SafariApplicationAliasResolvesToOneCanonicalCryptexRowWithoutScanningHelpers()
    {
        var files = new MemoryFiles(); var metadata = new MemoryMetadata();
        const string root = "/System/Cryptexes/App/System/Applications/Safari.app";
        files.AddFile(root + "/Contents/MacOS/Safari", true);
        files.AddLink("/Applications/Safari.app", root, true);
        files.AddLink("/Applications/SharedDirectory", "/opt/shared", true);
        var info = new MacBundleMetadata("com.apple.Safari", "Safari", root, root + "/Contents/MacOS/Safari");
        metadata.Applications.Add(root, info); metadata.Applications.Add("/Applications/Safari.app", info);
        var catalog = new MacApplicationCatalog(files, metadata, MacApplicationCatalog.StandardRoots("/Users/fixture"));
        var discovery = await catalog.DiscoverAsync(TestContext.Current.CancellationToken);
        var app = Assert.Single(discovery.Applications);
        Assert.True(discovery.IsComplete); Assert.Equal(root, app.BundlePath); Assert.Equal(0, files.MachOReads);
        var inventory = await catalog.BuildInventoryAsync(app, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(inventory.IsComplete); Assert.Equal(info.PrimaryExecutablePath, Assert.Single(inventory.ExecutablePaths));
    }

    private static (MemoryFiles Files, MemoryMetadata Metadata, MacApplicationCatalog Catalog) Fixture()
    {
        var files = new MemoryFiles();
        files.AddFile("/Applications/Example.app/Contents/MacOS/Example", true);
        files.AddFile("/Applications/Example.app/Contents/Frameworks/Helper.app/Contents/MacOS/Helper", true);
        files.AddFile("/Applications/Example.app/Contents/readme.txt", false);
        var metadata = new MemoryMetadata();
        metadata.Applications.Add("/Applications/Example.app", new("org.example.desktop", "Example", "/Applications/Example.app", "/Applications/Example.app/Contents/MacOS/Example"));
        return (files, metadata, new(files, metadata, ["/Applications"]));
    }
    private sealed class MemoryMetadata : IMacBundleMetadataReader
    {
        public Dictionary<string, MacBundleMetadata> Applications { get; } = [];
        public int ReadCount;
        public MacBundleMetadata ReadBundle(string bundlePath) { ReadCount++; return Applications[bundlePath]; }
    }
    private sealed class MemoryFiles : IMacCatalogFileSystem
    {
        private readonly Dictionary<string, List<MacFileEntry>> _directories = new(StringComparer.Ordinal) { ["/"] = [] };
        private readonly HashSet<string> _executables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
        public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);
        public int MachOReads;
        public bool DirectoryExists(string path) => _directories.ContainsKey(path);
        public string Canonicalize(string path) => _links.GetValueOrDefault(path, path);
        public IReadOnlyList<MacFileEntry> Enumerate(string directory)
        {
            if (Denied.Contains(directory)) throw new UnauthorizedAccessException("Synthetic denied directory");
            return _directories[directory];
        }
        public bool IsExecutableMachO(string path) { MachOReads++; return _executables.Contains(path); }
        public void AddFile(string path, bool executable)
        {
            AddDirectory(Path.GetDirectoryName(path)!);
            _directories[Path.GetDirectoryName(path)!].Add(new(path, false, false));
            if (executable) _executables.Add(path);
        }
        public void AddLink(string path, string target, bool directory)
        {
            AddDirectory(Path.GetDirectoryName(path)!); _links.Add(path, target);
            _directories[Path.GetDirectoryName(path)!].Add(new(path, directory, true));
        }
        private void AddDirectory(string path)
        {
            if (_directories.ContainsKey(path)) return;
            string parent = Path.GetDirectoryName(path)!; AddDirectory(parent);
            _directories.Add(path, []); _directories[parent].Add(new(path, true, false));
        }
    }
}
