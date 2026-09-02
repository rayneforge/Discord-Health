using DiscordHealth.Runtime.Changes;
using DiscordHealth.Runtime.DiscordAdapter;
using Xunit;

namespace DiscordHealth.Runtime.Tests;

public sealed class QuorumSelfProtectionPolicyTests
{
    private const ulong GuildId = 100;
    private const ulong BotUserId = 200;
    private static readonly ulong[] BotRoles = [300, 301];

    [Theory]
    [InlineData(ChangeActionType.ChangeRolePermissions, 300)]
    [InlineData(ChangeActionType.DeleteRole, 301)]
    [InlineData(ChangeActionType.ChangeRolePermissions, GuildId)]
    [InlineData(ChangeActionType.AssignRole, BotUserId)]
    [InlineData(ChangeActionType.RemoveRole, BotUserId)]
    [InlineData(ChangeActionType.TimeoutMember, BotUserId)]
    [InlineData(ChangeActionType.KickMember, BotUserId)]
    [InlineData(ChangeActionType.BanMember, BotUserId)]
    public void Self_targeting_actions_are_blocked(ChangeActionType action, ulong resourceId)
    {
        var exception = Assert.Throws<UnauthorizedAccessException>(() =>
            Validate(action, resourceId));

        Assert.Equal(QuorumSelfProtectionPolicy.RejectionMessage, exception.Message);
    }

    [Theory]
    [InlineData(ChangeActionType.SetRoleChannelOverwrite, "300")]
    [InlineData(ChangeActionType.RemoveRoleChannelOverwrite, "100")]
    public void Overwrites_for_bot_or_everyone_roles_are_blocked(ChangeActionType action, string roleId)
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            Validate(action, 400, new Dictionary<string, string> { ["role_id"] = roleId }));
    }

    [Fact]
    public void Unrelated_role_changes_remain_available()
    {
        Validate(ChangeActionType.ChangeRolePermissions, 999);
        Validate(
            ChangeActionType.SetRoleChannelOverwrite,
            400,
            new Dictionary<string, string> { ["role_id"] = "999" });
        Validate(ChangeActionType.AssignRole, 888, new Dictionary<string, string> { ["role_id"] = "999" });
    }

    private static void Validate(
        ChangeActionType action,
        ulong resourceId,
        IReadOnlyDictionary<string, string>? arguments = null) =>
        QuorumSelfProtectionPolicy.Validate(action, resourceId, arguments, GuildId, BotUserId, BotRoles);
}
