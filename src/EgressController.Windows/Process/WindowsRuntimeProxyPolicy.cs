namespace EgressController.Windows.Process;

/// <summary>
/// Adapts managed launches to the proxy controls used by common Windows desktop web runtimes.
/// Environment proxy variables cover Node/curl-style clients, but Chromium does not consume
/// them for its browser network service. WebView2 has a separate environment contract.
/// </summary>
public static class WindowsRuntimeProxyPolicy
{
    private static readonly string[] ChromiumRuntimeFiles =
    [
        "chrome_100_percent.pak",
        "chrome_200_percent.pak",
        "libcef.dll",
    ];

    public static string ChromiumArguments(int localPort)
    {
        ValidatePort(localPort);
        return $"--proxy-server=http://127.0.0.1:{localPort} "
            + "--proxy-bypass-list=\"localhost;127.0.0.1;[::1]\" --disable-quic";
    }

    public static string AppendChromiumArguments(string? existingArguments, int localPort)
    {
        string proxyArguments = ChromiumArguments(localPort);
        return string.IsNullOrWhiteSpace(existingArguments)
            ? proxyArguments
            : existingArguments.Trim() + " " + proxyArguments;
    }

    /// <summary>
    /// Detects Electron, CEF, and Chromium-family launchers by their runtime payload rather than
    /// app/vendor names. Versioned Chrome/Edge layouts keep the payload one directory below the
    /// launcher, while Electron/CEF normally keep it beside the executable.
    /// </summary>
    public static bool UsesChromiumCommandLine(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        string? directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        if (HasChromiumPayload(directory))
            return true;

        try
        {
            foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                if (Version.TryParse(Path.GetFileName(child), out _)
                    && HasChromiumPayload(child))
                    return true;
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return false;
    }

    private static bool HasChromiumPayload(string directory)
    {
        bool hasCoreData = File.Exists(Path.Combine(directory, "icudtl.dat"))
            && File.Exists(Path.Combine(directory, "resources.pak"));
        if (!hasCoreData)
            return false;

        if (ChromiumRuntimeFiles.Any(file => File.Exists(Path.Combine(directory, file))))
            return true;

        string resources = Path.Combine(directory, "resources");
        return File.Exists(Path.Combine(resources, "app.asar"))
            || File.Exists(Path.Combine(resources, "default_app.asar"));
    }

    private static void ValidatePort(int localPort)
    {
        if (localPort is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(localPort));
    }
}
