using DiscordHealth.Runtime.Drift;
using DiscordHealth.Runtime.Persistence;
using DiscordHealth.Runtime.ServerConfiguration;

namespace DiscordHealth.Runtime.Tools;

public sealed record ServerReview(ServerConfigurationSnapshot Snapshot, IReadOnlyList<ConfigurationChange> ChangesFromPrevious);

public interface IQuorumReadTools
{
    Task<ServerReview> ScanAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<ServerConfigurationSnapshot> GetLatestAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecurityFinding>> ListFindingsAsync(ulong guildId, CancellationToken cancellationToken = default);
}

internal sealed class QuorumReadTools(IServerConfigurationReader reader, ISnapshotStore snapshots, ISnapshotDiffer differ) : IQuorumReadTools
{
    public async Task<ServerReview> ScanAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var current = await reader.CaptureAsync(guildId, cancellationToken);
        var previous = await snapshots.GetLatestAsync(guildId, cancellationToken: cancellationToken);
        var changes = previous is null ? [] : differ.Compare(previous, current);
        await snapshots.SaveAsync(current, cancellationToken);
        return new(current, changes);
    }

    public async Task<IReadOnlyList<SecurityFinding>> ListFindingsAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        return (await GetLatestAsync(guildId, cancellationToken)).Findings;
    }

    public async Task<ServerConfigurationSnapshot> GetLatestAsync(ulong guildId, CancellationToken cancellationToken = default) =>
        await snapshots.GetLatestAsync(guildId, cancellationToken: cancellationToken)
        ?? (await ScanAsync(guildId, cancellationToken)).Snapshot;
}
