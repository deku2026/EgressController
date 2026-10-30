// SPDX-License-Identifier: MIT
namespace EgressController.Mac;

/// <summary>ServiceId is the persistent SystemConfiguration identity; BSD names are current observations.</summary>
public sealed record MacNetworkInterface(string ServiceId, string BsdName, string DisplayName, string InterfaceType,
    string? HardwareAddress, bool IsActive, string[] Addresses);

/// <summary>Opaque plist preserves the exact configured DNS dictionary, including unknown keys.</summary>
public sealed record MacDnsServiceSnapshot(string ServiceId, bool ProtocolWasPresent, bool ProtocolWasEnabled,
    string? OriginalConfigurationPlistBase64);

public sealed record MacDnsRecoverySnapshot(int Version, Guid SessionId, DateTimeOffset CapturedAtUtc,
    MacDnsServiceSnapshot[] Services)
{
    public void Validate()
    {
        if (Version != 1 || SessionId == Guid.Empty) throw new ArgumentException("Unsupported DNS recovery snapshot.");
        if (Services.Length is 0 or > 128 || Services.Select(item => item.ServiceId).Distinct(StringComparer.Ordinal).Count() != Services.Length)
            throw new ArgumentException("DNS recovery services must be nonempty and unique.");
        foreach (var service in Services)
        {
            if (!Guid.TryParse(service.ServiceId, out _) || service.OriginalConfigurationPlistBase64?.Length > 1_400_000)
                throw new ArgumentException("Invalid DNS service snapshot.");
            if (!service.ProtocolWasPresent && (service.ProtocolWasEnabled || service.OriginalConfigurationPlistBase64 is not null))
                throw new ArgumentException("An absent DNS protocol cannot contain configured state.");
            if (service.OriginalConfigurationPlistBase64 is { } plist)
            {
                try { if (Convert.FromBase64String(plist).Length > 1_048_576) throw new ArgumentException("DNS snapshot exceeds its limit."); }
                catch (FormatException error) { throw new ArgumentException("Malformed DNS recovery property list.", error); }
            }
        }
    }
}
