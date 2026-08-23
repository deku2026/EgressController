using System.Text.Json;
using EgressController.Core.Profile;
using EgressController.State.Json;
using EgressController.State.Storage;

namespace EgressController.State.Profile;

public sealed class EgressProfileStore
{
    public EgressProfileStore(string baseDirectory)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory);
        ProfilePath = Path.Combine(BaseDirectory, "profile.json");
    }

    public string BaseDirectory { get; }
    public string ProfilePath { get; }

    public EgressProfileDocument Load()
    {
        if (!File.Exists(ProfilePath))
            return EgressProfileDocument.Default;

        try
        {
            byte[] bytes = File.ReadAllBytes(ProfilePath);
            EgressProfileDocument? document = JsonSerializer.Deserialize(
                bytes,
                EgressStateJsonContext.Default.EgressProfileDocument);
            return (document ?? throw new JsonException("Profile JSON 是 null。"))
                .NormalizeAndValidate();
        }
        catch (ProfileSchemaException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ProfileStoreException($"无法读取 Profile：{ProfilePath}", ex);
        }
    }

    public void Save(EgressProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EgressProfileDocument normalized = document.NormalizeAndValidate();
        AtomicJsonFile.Write(
            ProfilePath,
            normalized,
            EgressStateJsonContext.Default.EgressProfileDocument);
    }
}

public sealed class ProfileStoreException(string message, Exception innerException)
    : IOException(message, innerException);
