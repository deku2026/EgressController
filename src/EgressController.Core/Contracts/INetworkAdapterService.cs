using EgressController.Core.Models;

namespace EgressController.Core.Contracts;

/// <summary>
/// Enumerates the machine's network adapters and can resolve an adapter by its stable
/// identity or by a current interface index. Implemented by the Windows project against
/// GetAdaptersAddresses.
/// </summary>
public interface INetworkAdapterService
{
    /// <summary>All relevant physical + virtual adapters at this moment (UI can filter later).</summary>
    IReadOnlyList<NetworkAdapterInfo> EnumerateAll();

    /// <summary>Resolve an adapter by its stable GUID, or null if not currently present.</summary>
    NetworkAdapterInfo? GetByGuid(Guid guid);

    /// <summary>Resolve an adapter by a current interface index, or null.</summary>
    NetworkAdapterInfo? GetByIfIndex(int ifIndex);
}