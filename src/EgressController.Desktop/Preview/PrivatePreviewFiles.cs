using System.Text;

namespace EgressController.Desktop.Preview;

/// <summary>A preview can contain pasted credentials: secure permissions precede the first byte.</summary>
public static class PrivatePreviewFiles
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureSessionDirectory()
    {
        PreviewEnvironment.RequireIsolated();
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(PreviewEnvironment.SessionDirectory);
        else
        {
            Directory.CreateDirectory(PreviewEnvironment.SessionDirectory, DirectoryMode);
            File.SetUnixFileMode(PreviewEnvironment.SessionDirectory, DirectoryMode);
        }
    }

    public static string WriteText(string name, string text)
    {
        if (Path.GetFileName(name) != name || name is "." or "..") throw new ArgumentException("A preview filename cannot contain a directory.", nameof(name));
        EnsureSessionDirectory();
        string destination = Path.Combine(PreviewEnvironment.SessionDirectory, name);
        string staged = destination + "." + Guid.NewGuid().ToString("N") + ".staged";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
            using (var stream = new FileStream(staged, options))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(text);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(staged, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
}
