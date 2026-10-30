// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using EgressController.Mac;

namespace EgressController.Mac.Tests;

public sealed class MachOInspectorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ThinExecutableSupportsBothWidthsAndByteOrdersAndRestoresPosition(bool is64, bool bigEndian)
    {
        using var stream = new MemoryStream(Thin(is64, bigEndian)); stream.Position = 3;
        Assert.True(MachOInspector.IsExecutable(stream)); Assert.Equal(3, stream.Position);
    }

    [Theory]
    [InlineData(1u)] // MH_OBJECT
    [InlineData(3u)] // MH_FVMLIB
    [InlineData(6u)] // MH_DYLIB
    [InlineData(7u)] // MH_DYLINKER
    [InlineData(8u)] // MH_BUNDLE
    [InlineData(9u)] // MH_DYLIB_STUB
    [InlineData(10u)] // MH_DSYM
    [InlineData(13u)] // GPU executable is not a process executable.
    public void NonProcessMachOFilesAreRejectedRegardlessOfExecuteMode(uint fileType)
    {
        foreach (bool is64 in new[] { false, true })
        foreach (bool bigEndian in new[] { false, true })
            Assert.False(Inspect(Thin(is64, bigEndian, fileType)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UniversalExecutableValidatesEveryArchitectureAndSupportsFat32AndFat64(bool fat64, bool bigEndian)
    {
        Assert.True(Inspect(Fat(fat64, bigEndian, Thin(), Thin(false, true, cpu: 18))));
        Assert.False(Inspect(Fat(fat64, bigEndian, Thin(fileType: 6), Thin(false, true, fileType: 6, cpu: 18))));
        Assert.False(Inspect(Fat(fat64, bigEndian, Thin(), Thin(false, true, fileType: 6, cpu: 18))));
    }

    [Theory]
    [InlineData("java-class")]
    [InlineData("zero-architectures")]
    [InlineData("excessive-architectures")]
    [InlineData("truncated-table")]
    [InlineData("tiny-slice")]
    [InlineData("outside-slice")]
    [InlineData("overlaps-table")]
    [InlineData("overlapping-slices")]
    [InlineData("slice-bad-magic")]
    [InlineData("cpu-mismatch")]
    [InlineData("bad-alignment")]
    [InlineData("misalignment")]
    [InlineData("fat64-offset-overflow")]
    [InlineData("fat64-size-overflow")]
    [InlineData("fat64-reserved")]
    [InlineData("duplicate-architecture")]
    public void MalformedUniversalAndJavaMagicCannotBecomeInventoryMembers(string problem)
    {
        byte[] bytes = Fat(false, true, Thin(), Thin(false, true, cpu: 18));
        int firstOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16));
        switch (problem)
        {
            case "java-class": bytes = [0xca, 0xfe, 0xba, 0xbe, 0, 0, 0, 61, 0, 10, 7, 0, 1, 2]; break;
            case "zero-architectures": Write32(bytes, 4, 0, true); break;
            case "excessive-architectures": Write32(bytes, 4, MachOInspector.MaximumArchitectures + 1, true); break;
            case "truncated-table": bytes = bytes[..25]; break;
            case "tiny-slice": Write32(bytes, 20, 4, true); break;
            case "outside-slice": Write32(bytes, 16, uint.MaxValue, true); break;
            case "overlaps-table": Write32(bytes, 16, 8, true); break;
            case "overlapping-slices": Write32(bytes, 36, (uint)firstOffset, true); break;
            case "slice-bad-magic": Write32(bytes, firstOffset, 0xdeadbeef, true); break;
            case "cpu-mismatch": Write32(bytes, 8, 7, true); break;
            case "bad-alignment": Write32(bytes, 24, 40, true); break;
            case "misalignment": Write32(bytes, 24, 8, true); break;
            case "fat64-offset-overflow": bytes = Fat(true, true, Thin()); Write64(bytes, 16, ulong.MaxValue, true); break;
            case "fat64-size-overflow": bytes = Fat(true, true, Thin()); Write64(bytes, 24, ulong.MaxValue, true); break;
            case "fat64-reserved": bytes = Fat(true, true, Thin()); Write32(bytes, 36, 1, true); break;
            case "duplicate-architecture": bytes = Fat(false, true, Thin(), Thin()); break;
        }
        Assert.False(Inspect(bytes), problem);
    }

    [Theory]
    [InlineData("truncated-header")]
    [InlineData("truncated-command")]
    [InlineData("too-many-commands")]
    [InlineData("commands-outside-slice")]
    [InlineData("zero-command-size")]
    [InlineData("unaligned-command")]
    [InlineData("reserved-header")]
    [InlineData("invalid-cpu")]
    public void TruncatedOrMalformedThinHeadersAndCommandsAreRejected(string problem)
    {
        byte[] bytes = Thin();
        switch (problem)
        {
            case "truncated-header": bytes = bytes[..31]; break;
            case "truncated-command": bytes = bytes[..39]; break;
            case "too-many-commands": Write32(bytes, 16, uint.MaxValue, false); break;
            case "commands-outside-slice": Write32(bytes, 20, 64, false); break;
            case "zero-command-size": Write32(bytes, 36, 0, false); break;
            case "unaligned-command": Write32(bytes, 36, 7, false); break;
            case "reserved-header": Write32(bytes, 28, 1, false); break;
            case "invalid-cpu": Write32(bytes, 4, 0, false); break;
        }
        Assert.False(Inspect(bytes), problem);
    }

    [Fact]
    public async Task InventoryUsesBothExecutableModeAndFileTypeWithoutReadingInstalledApplications()
    {
        var files = new SyntheticBundle();
        var catalog = new MacApplicationCatalog(files, files, ["/Applications"]);
        var app = Assert.Single((await catalog.DiscoverAsync(TestContext.Current.CancellationToken)).Applications);
        var inventory = await catalog.BuildInventoryAsync(app, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(inventory.IsComplete);
        Assert.Equal(2, inventory.ExecutablePaths.Length);
        Assert.Contains(SyntheticBundle.Main, inventory.ExecutablePaths);
        Assert.Contains(SyntheticBundle.Helper, inventory.ExecutablePaths);
        Assert.DoesNotContain(inventory.ExecutablePaths, path => path.Contains("framework", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Plugin", StringComparison.Ordinal) || path.EndsWith(".class", StringComparison.Ordinal) || path.EndsWith("no-mode", StringComparison.Ordinal));
    }

    private static bool Inspect(byte[] bytes) { using var stream = new MemoryStream(bytes); return MachOInspector.IsExecutable(stream); }
    private static byte[] Thin(bool is64 = true, bool bigEndian = false, uint fileType = 2, uint? cpu = null)
    {
        int header = is64 ? 32 : 28;
        byte[] bytes = new byte[header + 8];
        Write32(bytes, 0, is64 ? 0xfeedfacf : 0xfeedface, bigEndian);
        Write32(bytes, 4, cpu ?? (is64 ? 0x0100000cu : 12u), bigEndian);
        Write32(bytes, 12, fileType, bigEndian);
        Write32(bytes, 16, 1, bigEndian); Write32(bytes, 20, 8, bigEndian);
        Write32(bytes, header, 8, bigEndian); Write32(bytes, header + 4, 8, bigEndian);
        return bytes;
    }
    private static byte[] Fat(bool is64, bool bigEndian, params byte[][] slices)
    {
        int recordSize = is64 ? 32 : 20, next = Align(8 + slices.Length * recordSize);
        var offsets = new List<int>();
        foreach (var slice in slices) { offsets.Add(next); next = Align(next + slice.Length); }
        byte[] bytes = new byte[next];
        Write32(bytes, 0, is64 ? 0xcafebabf : 0xcafebabe, bigEndian); Write32(bytes, 4, (uint)slices.Length, bigEndian);
        for (int index = 0; index < slices.Length; index++)
        {
            var slice = slices[index]; int record = 8 + index * recordSize;
            uint magic = BinaryPrimitives.ReadUInt32BigEndian(slice);
            bool sliceBigEndian = magic is 0xfeedface or 0xfeedfacf;
            uint cpu = sliceBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(slice.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(slice.AsSpan(4));
            uint subtype = sliceBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(slice.AsSpan(8)) : BinaryPrimitives.ReadUInt32LittleEndian(slice.AsSpan(8));
            Write32(bytes, record, cpu, bigEndian); Write32(bytes, record + 4, subtype, bigEndian);
            if (is64) { Write64(bytes, record + 8, (ulong)offsets[index], bigEndian); Write64(bytes, record + 16, (ulong)slice.Length, bigEndian); }
            else { Write32(bytes, record + 8, (uint)offsets[index], bigEndian); Write32(bytes, record + 12, (uint)slice.Length, bigEndian); }
            Write32(bytes, record + (is64 ? 24 : 16), 3, bigEndian);
            slice.CopyTo(bytes, offsets[index]);
        }
        return bytes;
    }
    private static int Align(int value) => (value + 7) / 8 * 8;
    private static void Write32(byte[] bytes, int offset, uint value, bool bigEndian)
    { if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value); else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value); }
    private static void Write64(byte[] bytes, int offset, ulong value, bool bigEndian)
    { if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), value); else BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value); }

    private sealed class SyntheticBundle : IMacCatalogFileSystem, IMacBundleMetadataReader
    {
        internal const string Main = "/Applications/Synthetic.app/Contents/MacOS/Main";
        internal const string Helper = "/Applications/Synthetic.app/Contents/Helpers/helper";
        private readonly Dictionary<string, (byte[] Bytes, bool ExecuteBit)> _files = new(StringComparer.Ordinal)
        {
            [Main] = (Thin(), true), [Helper] = (Fat(false, true, Thin(), Thin(false, true, cpu: 18)), true),
            ["/Applications/Synthetic.app/Contents/MacOS/no-mode"] = (Thin(), false),
            ["/Applications/Synthetic.app/Contents/Frameworks/Thing.framework/Versions/A/Thing"] = (Thin(fileType: 6), true),
            ["/Applications/Synthetic.app/Contents/Plugins/Plugin.bundle/Contents/MacOS/Plugin"] = (Thin(fileType: 8), true),
            ["/Applications/Synthetic.app/Contents/Java/Main.class"] = ([0xca, 0xfe, 0xba, 0xbe, 0, 0, 0, 61], true),
        };
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "/Applications" };
        internal SyntheticBundle()
        {
            foreach (string path in _files.Keys)
                for (string? parent = Path.GetDirectoryName(path); parent is not null && parent.StartsWith("/Applications", StringComparison.Ordinal); parent = Path.GetDirectoryName(parent)) _directories.Add(parent);
        }
        public bool DirectoryExists(string path) => _directories.Contains(path);
        public string Canonicalize(string path) => path;
        public IReadOnlyList<MacFileEntry> Enumerate(string directory) => _directories.Where(path => Path.GetDirectoryName(path) == directory).Select(path => new MacFileEntry(path, true, false))
            .Concat(_files.Keys.Where(path => Path.GetDirectoryName(path) == directory).Select(path => new MacFileEntry(path, false, false))).ToArray();
        public bool IsExecutableMachO(string path) => _files[path].ExecuteBit && Inspect(_files[path].Bytes);
        public MacBundleMetadata ReadBundle(string bundlePath) => new("org.example.synthetic", "Synthetic", bundlePath, Main);
    }
}
