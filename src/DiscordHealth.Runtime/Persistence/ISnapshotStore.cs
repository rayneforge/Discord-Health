using DiscordHealth.Runtime.ServerConfiguration;

namespace DiscordHealth.Runtime.Persistence;

public interface ISnapshotStore
{
    Task SaveAsync(ServerConfigurationSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<ServerConfigurationSnapshot?> GetLatestAsync(ulong guildId, Guid? excludingSnapshotId = null, CancellationToken cancellationToken = default);
}
