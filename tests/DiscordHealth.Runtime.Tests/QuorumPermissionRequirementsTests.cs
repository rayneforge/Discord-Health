using Discord;
using DiscordHealth.Runtime.Changes;
using DiscordHealth.Runtime.DiscordAdapter;
using Xunit;

namespace DiscordHealth.Runtime.Tests;

public sealed class QuorumPermissionRequirementsTests
{
    [Fact]
    public void Every_registered_change_action_has_an_explicit_permission_mapping()
    {
        foreach (var action in Enum.GetValues<ChangeActionType>())
            Assert.NotEmpty(QuorumPermissionRequirements.ForChange(action));
    }

    [Theory]
    [InlineData(ChangeActionType.CreateTextChannel, GuildPermission.ManageChannels)]
    [InlineData(ChangeActionType.ChangeRolePermissions, GuildPermission.ManageRoles)]
    [InlineData(ChangeActionType.TimeoutMember, GuildPermission.ModerateMembers)]
    [InlineData(ChangeActionType.BanMember, GuildPermission.BanMembers)]
    [InlineData(ChangeActionType.CreateScheduledEvent, GuildPermission.CreateEvents)]
    public void Change_actions_require_the_matching_discord_permission(
        ChangeActionType action,
        GuildPermission expected)
    {
        Assert.Contains(expected, QuorumPermissionRequirements.ForChange(action));
    }

    [Fact]
    public void Onboarding_requires_both_server_and_role_management()
    {
        Assert.Equal(
            [GuildPermission.ManageGuild, GuildPermission.ManageRoles],
            QuorumPermissionRequirements.ForChange(ChangeActionType.UpdateOnboarding));
        Assert.Equal("MANAGE_GUILD + MANAGE_ROLES", QuorumPermissionRequirements.ForChangeDisplay(ChangeActionType.UpdateOnboarding));
    }
}
