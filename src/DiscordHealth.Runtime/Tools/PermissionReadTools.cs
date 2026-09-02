using DiscordHealth.Runtime.Analysis;
using DiscordHealth.Runtime.Persistence;

namespace DiscordHealth.Runtime.Tools;

public interface IPermissionReadTools
{
    Task<PermissionExplanation> ExplainRolePermissionAsync(ulong guildId, ulong roleId, ulong channelId, string permission, CancellationToken cancellationToken = default);
}

internal sealed class PermissionReadTools(ISnapshotStore snapshots, IEffectivePermissionAnalyzer analyzer) : IPermissionReadTools
{
    public async Task<PermissionExplanation> ExplainRolePermissionAsync(ulong guildId, ulong roleId, ulong channelId, string permission, CancellationToken cancellationToken = default)
    {
        var snapshot = await snapshots.GetLatestAsync(guildId, cancellationToken: cancellationToken) ?? throw new InvalidOperationException("Run a Quorum scan first.");
        if (!snapshot.Roles.IsAvailable || snapshot.Roles.Data is null || !snapshot.Channels.IsAvailable || snapshot.Channels.Data is null)
            throw new InvalidOperationException("Role or channel collection is unavailable in the latest snapshot.");
        var channel = snapshot.Channels.Data.SingleOrDefault(x => x.Id == channelId) ?? throw new KeyNotFoundException("Channel was not found in the latest snapshot.");
        return analyzer.ExplainRole(guildId, snapshot.Roles.Data, channel, roleId, permission);
    }
}
