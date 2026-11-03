using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using EgressController.Core.Protection;
using EgressController.Core.Routing;

namespace EgressController.Mac;

public sealed record MacApplication(string Id, string Name, string Path, bool Bundle, string? IconPath);
public sealed partial class MacDiscovery
{
    public async Task<IReadOnlyList<ResolvedAdapter>> AdaptersAsync(CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        string hardware = await MacCommand.ReadAsync("/usr/sbin/networksetup", ["-listallhardwareports"], token).ConfigureAwait(false);
        var ports = new Dictionary<string, (string Label, string Mac)>(StringComparer.Ordinal);
        string label = "", device = "";
        foreach (var line in hardware.Split('\n'))
        {
            if (line.StartsWith("Hardware Port: ", StringComparison.Ordinal)) label = line[15..].Trim();
            else if (line.StartsWith("Device: ", StringComparison.Ordinal)) device = line[8..].Trim();
            else if (line.StartsWith("Ethernet Address: ", StringComparison.Ordinal) && device.Length > 0)
                ports[device] = (label, line[18..].Trim().ToLowerInvariant());
        }
        return await Task.Run(() => NetworkInterface.GetAllNetworkInterfaces().Where(n => ports.ContainsKey(n.Name))
            .Select(n => new ResolvedAdapter("hardware:" + ports[n.Name].Mac, ports[n.Name].Label, n.Name,
                n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().UnicastAddresses.Any(a => !System.Net.IPAddress.IsLoopback(a.Address)),
                string.Join(" · ", n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.Address.ToString()))))
            .Where(n => n.Id.Length > "hardware:".Length && !n.Id.EndsWith("00:00:00:00:00:00", StringComparison.Ordinal)).ToArray(), token).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<MacApplication>> ApplicationsAsync(CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        var paths = await Task.Run(() => new[] { "/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"), "/System/Applications" }
            .Where(Directory.Exists).SelectMany(BundlePaths).ToArray(), token).ConfigureAwait(false);
        var apps = new List<MacApplication>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try { apps.Add(await InspectAsync(path, token).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { /* Unreadable bundles cannot become selected routes. */ }
        }
        foreach (string command in new[] { "claude", "codex", "gemini", "opencode" })
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified))
            {
                string path = Path.Combine(folder, command);
                if (!File.Exists(path)) continue;
                try
                {
                    string canonical = Canonical(path);
                    if (IsNative(canonical)) apps.Add(new("cli:" + canonical, command + " CLI", canonical, false, null));
                }
                catch (IOException) { }
                break;
            }
        }
        return apps.DistinctBy(a => a.Path, StringComparer.Ordinal).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task<MacApplication> InspectAsync(string path, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        string canonical = Canonical(path.TrimEnd('/'));
        if (Directory.Exists(canonical) && canonical.EndsWith(".app", StringComparison.Ordinal))
        {
            var plist = await MacCommand.ReadAsync("/usr/bin/plutil", ["-convert", "xml1", "-o", "-", Path.Combine(canonical, "Contents", "Info.plist")], token).ConfigureAwait(false);
            var items = XDocument.Parse(plist).Root?.Element("dict")?.Elements().ToArray() ?? [];
            string? Read(string key) { int i = Array.FindIndex(items, e => e.Name == "key" && e.Value == key); return i >= 0 && i + 1 < items.Length ? items[i + 1].Value : null; }
            string? icon = Read("CFBundleIconFile");
            if (icon is not null) icon = Path.Combine(canonical, "Contents", "Resources", icon.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ? icon : icon + ".icns");
            return new(Read("CFBundleIdentifier") ?? canonical, Read("CFBundleDisplayName") ?? Read("CFBundleName") ?? Path.GetFileNameWithoutExtension(canonical), canonical + "/", true, File.Exists(icon) ? icon : null);
        }
        if (!IsNative(canonical)) throw new InvalidDataException("请选择 .app 或原生 Mach-O 命令；共享解释器和脚本不会被加入。");
        EgressController.Surge.SurgeConfigCompiler.ValidatePath(canonical, false);
        return new("cli:" + canonical, Path.GetFileName(canonical) + " CLI", canonical, false, null);
    }
    private static IEnumerable<string> BundlePaths(string root)
    {
        IEnumerable<string> children;
        try { children = Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { yield break; }
        foreach (string child in children)
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            if (child.EndsWith(".app", StringComparison.Ordinal)) yield return child; // Never recurse into nested helper bundles.
            else foreach (var bundle in BundlePaths(child)) yield return bundle;
        }
    }
    public static bool IsNative(string path)
    {
        using var stream = File.OpenRead(path); Span<byte> bytes = stackalloc byte[4];
        if (stream.Read(bytes) != 4) return false;
        uint magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
    }
    public static string Canonical(string path)
    {
        if (!OperatingSystem.IsMacOS()) return Path.GetFullPath(path);
        nint resolved = RealPath(path, 0);
        if (resolved == 0) throw new IOException("路径不存在或无法解析。");
        try { return Marshal.PtrToStringUTF8(resolved) ?? throw new IOException("路径无法解析。"); }
        finally { Free(resolved); }
    }
    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)] private static partial nint RealPath(string path, nint buffer);
    [LibraryImport("libc", EntryPoint = "free")] private static partial void Free(nint buffer);
}
