using DiscordHealth.Runtime.Changes;

namespace DiscordHealth.Runtime.DiscordAdapter;

internal static class QuorumSelfProtectionPolicy
{
    internal const string RejectionMessage =
        "Quorum cannot propose or execute permission, role-membership, or moderation changes that target its own bot account, an assigned bot role, or @everyone. An administrator must change Quorum's access directly in Discord.";

    public static void Validate(
        ChangeActionType action,
        ulong resourceId,
        IReadOnlyDictionary<string, string>? arguments,
        ulong guildId,
        ulong botUserId,
        IReadOnlyCollection<ulong> botRoleIds)
    {
        var protectedRole = resourceId == guildId || botRoleIds.Contains(resourceId);
        if (action is ChangeActionType.ChangeRolePermissions or ChangeActionType.DeleteRole && protectedRole)
            throw new UnauthorizedAccessException(RejectionMessage);

        if (action is ChangeActionType.AssignRole or ChangeActionType.RemoveRole && resourceId == botUserId)
            throw new UnauthorizedAccessException(RejectionMessage);

        if (action is ChangeActionType.TimeoutMember or ChangeActionType.KickMember or ChangeActionType.BanMember && resourceId == botUserId)
            throw new UnauthorizedAccessException(RejectionMessage);

        if (action is ChangeActionType.SetRoleChannelOverwrite or ChangeActionType.RemoveRoleChannelOverwrite &&
            arguments?.TryGetValue("role_id", out var roleText) == true &&
            ulong.TryParse(roleText, out var roleId) &&
            (roleId == guildId || botRoleIds.Contains(roleId)))
            throw new UnauthorizedAccessException(RejectionMessage);
    }
}
