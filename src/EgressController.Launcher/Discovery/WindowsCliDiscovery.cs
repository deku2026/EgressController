using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EgressController.Core.Models;
using EgressController.Core.Protection;

namespace EgressController.Launcher.Discovery;

public sealed record CliCommandLocation(string Name, string Source);
public interface ICommandLocationSource { IReadOnlyList<CliCommandLocation> Find(IReadOnlyList<string> names); }

/// <summary>Queries command metadata only. Never invokes the discovered AI command.</summary>
public sealed class PowerShellCommandLocationSource : ICommandLocationSource
{
    public IReadOnlyList<CliCommandLocation> Find(IReadOnlyList<string> names)
    {
        RuntimeSafety.RequireLiveOperations();
        if (names.Any(name => name.Length is < 1 or > 40 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Invalid command identifier.", nameof(names));
        string script = "Import-Module ($PSHOME+'\\Modules\\Microsoft.PowerShell.Utility\\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop; $PSModuleAutoLoadingPreference='None'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); "
            + "$env:Path=[Environment]::GetEnvironmentVariable('Path','User')+';'+[Environment]::GetEnvironmentVariable('Path','Machine')+';'+$env:Path; "
            + "$found=@(foreach($n in @(" + string.Join(',', names.Select(name => "'" + name + "'")) + ")) { "
            + "$c=Get-Command -Name $n -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1; "
            + "if($c) { [pscustomobject]@{Name=$n;Source=$c.Source} } }); ConvertTo-Json -InputObject $found -Compress";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("无法查询 Windows CLI 路径。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Windows CLI 路径查询超时。");
        }
        if (process.ExitCode != 0) throw new IOException("Windows CLI 路径查询失败：" + error.GetAwaiter().GetResult());
        string json = output.GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize(json, CliDiscoveryJson.Default.CliCommandLocationArray) ?? [];
    }
}

public sealed class WindowsCliDiscovery(ICommandLocationSource commands, Func<string, bool>? exists = null,
    Func<string, IEnumerable<string>>? binaries = null)
{
    private readonly Func<string, bool> _exists = exists ?? IsNativeExecutable;
    private readonly Func<string, IEnumerable<string>> _binaries = binaries ?? EnumerateBinaries;
    private static readonly string[] Names = ["claude", "codex", "gemini", "copilot", "ollama", "opencode"];
    public IReadOnlyList<string> Notices { get; private set; } = [];

    public IReadOnlyList<LaunchTarget> Discover()
    {
        var results = new List<LaunchTarget>(); var notices = new List<string>();
        foreach (var command in commands.Find(Names))
        {
            if (!Names.Contains(command.Name, StringComparer.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(command.Source)) continue;
            string? path = Path.GetExtension(command.Source).Equals(".exe", StringComparison.OrdinalIgnoreCase) && _exists(command.Source)
                ? Path.GetFullPath(command.Source) : ResolveNativeWrapper(command);
            if (path is null)
            {
                notices.Add($"{command.Name}：{command.Source} 未找到独立的原生 EXE（可能是脚本或商店命令别名），未加入保护。"); continue;
            }
            var definition = SupportedApplicationCatalog.ForExecutable(path);
            if (definition is null) continue;
            results.Add(new()
            {
                Id = "cli:" + path.ToLowerInvariant(), SavedDiscoveryKey = "cli:" + path,
                RelatedDiscoveryKeys = ["exe:" + path], Name = definition.Name + " CLI", CatalogId = definition.Id,
                Kind = LaunchKind.CliNative, Command = path, CanonicalExecutable = path,
                OwnedExecutables = [path], OwnedRoots = [], IconPath = path, Source = "Windows CLI · Get-Command",
            });
        }
        Notices = notices;
        return results;
    }
    private string? ResolveNativeWrapper(CliCommandLocation command)
    {
        if (!new[] { ".cmd", ".ps1", ".bat" }.Contains(Path.GetExtension(command.Source), StringComparer.OrdinalIgnoreCase)) return null;
        string directory = Path.GetDirectoryName(command.Source)!;
        string? package = command.Name.ToLowerInvariant() switch
        { "codex" => @"node_modules\@openai\codex", "claude" => @"node_modules\@anthropic-ai\claude-code", "opencode" => @"node_modules\opencode-ai", _ => null };
        if (package is null) return null;
        string root = Path.GetFullPath(Path.Combine(directory, package));
        return _binaries(root).Where(path => Path.IsPathFullyQualified(path)
            && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).Equals(command.Name + ".exe", StringComparison.OrdinalIgnoreCase) && _exists(path))
            .Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }
    private static bool IsNativeExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static IEnumerable<string> EnumerateBinaries(string root)
    {
        if (!Directory.Exists(root)) return [];
        try { return Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(128).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CliCommandLocation[]))]
internal sealed partial class CliDiscoveryJson : JsonSerializerContext;
