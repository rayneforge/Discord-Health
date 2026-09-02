using DiscordHealth.Runtime.Analysis;
using DiscordHealth.Runtime.ServerConfiguration;
using Xunit;

namespace DiscordHealth.Runtime.Tests;

public sealed class EffectivePermissionAnalyzerTests
{
    [Fact]
    public void Role_allow_overrides_everyone_channel_deny()
    {
        const ulong guildId = 1;
        const ulong roleId = 2;
        const ulong viewChannel = 1UL << 10;
        var everyone = Role(guildId, "@everyone", viewChannel, true);
        var moderator = Role(roleId, "Moderator", viewChannel, false);
        var channel = Channel([
            new(guildId, "Role", 0, viewChannel),
            new(roleId, "Role", viewChannel, 0)
        ]);

        var result = new EffectivePermissionAnalyzer().ExplainRole(guildId, [everyone, moderator], channel, roleId, "ViewChannel");

        Assert.True(result.Allowed);
        Assert.Contains(result.Steps, x => x.Source == "role:Moderator channel overwrite");
    }

    [Fact]
    public void Administrator_bypasses_channel_deny()
    {
        const ulong guildId = 1;
        const ulong roleId = 2;
        var everyone = Role(guildId, "@everyone", 0, true);
        var administrator = Role(roleId, "Admin", 1UL << 3, false);
        var channel = Channel([new(guildId, "Role", 0, ulong.MaxValue)]);

        var result = new EffectivePermissionAnalyzer().ExplainRole(guildId, [everyone, administrator], channel, roleId, "ViewChannel");

        Assert.True(result.Allowed);
        Assert.Contains(result.Steps, x => x.Source == "Administrator");
    }

    private static RoleConfiguration Role(ulong id, string name, ulong permissions, bool everyone) =>
        new(id, name, 1, permissions, [], everyone, false, false, false, 0, null, null, null, null, null, null, false, null);

    private static ChannelConfiguration Channel(IReadOnlyList<PermissionOverwriteConfiguration> overwrites) =>
        new(10, "staff", "SocketTextChannel", 1, null, null, false, 0, 0, 60, null, null, null, null, null, [], overwrites, false);
}
