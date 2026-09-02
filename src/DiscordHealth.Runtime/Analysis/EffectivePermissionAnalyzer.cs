using DiscordHealth.Runtime.ServerConfiguration;

namespace DiscordHealth.Runtime.Analysis;

public sealed record PermissionDecisionStep(string Source, string Effect, ulong Before, ulong After);
public sealed record PermissionExplanation(string Permission, bool Allowed, bool Complete, IReadOnlyList<PermissionDecisionStep> Steps, string Summary);

public interface IEffectivePermissionAnalyzer
{
    PermissionExplanation ExplainRole(ulong guildId, IReadOnlyList<RoleConfiguration> roles, ChannelConfiguration channel, ulong roleId, string permission);
}

internal sealed class EffectivePermissionAnalyzer : IEffectivePermissionAnalyzer
{
    private static readonly IReadOnlyDictionary<string, ulong> PermissionBits = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
    {
        ["CreateInstantInvite"] = 1UL << 0, ["KickMembers"] = 1UL << 1, ["BanMembers"] = 1UL << 2,
        ["Administrator"] = 1UL << 3, ["ManageChannels"] = 1UL << 4, ["ManageGuild"] = 1UL << 5,
        ["ViewAuditLog"] = 1UL << 7, ["ViewChannel"] = 1UL << 10, ["SendMessages"] = 1UL << 11,
        ["ManageMessages"] = 1UL << 13, ["MentionEveryone"] = 1UL << 17, ["Connect"] = 1UL << 20,
        ["Speak"] = 1UL << 21, ["ManageRoles"] = 1UL << 28, ["ManageWebhooks"] = 1UL << 29,
        ["ManageEvents"] = 1UL << 33, ["ManageThreads"] = 1UL << 34, ["SendMessagesInThreads"] = 1UL << 38,
        ["ModerateMembers"] = 1UL << 40
    };

    public PermissionExplanation ExplainRole(ulong guildId, IReadOnlyList<RoleConfiguration> roles, ChannelConfiguration channel, ulong roleId, string permission)
    {
        if (!PermissionBits.TryGetValue(permission, out var bit)) throw new ArgumentException($"Unsupported permission name: {permission}.", nameof(permission));
        var everyone = roles.SingleOrDefault(x => x.IsEveryone) ?? throw new InvalidOperationException("The @everyone role is missing from the snapshot.");
        var role = roles.SingleOrDefault(x => x.Id == roleId) ?? throw new KeyNotFoundException($"Role {roleId} is missing from the snapshot.");
        var steps = new List<PermissionDecisionStep>();
        var value = everyone.Permissions;
        steps.Add(new("@everyone guild role", Has(value, bit) ? "allow" : "not granted", 0, value));
        var before = value;
        value |= role.Permissions;
        steps.Add(new($"role:{role.Name}", Has(role.Permissions, bit) ? "allow" : "no change", before, value));

        if (Has(value, PermissionBits["Administrator"]))
        {
            steps.Add(new("Administrator", "allow all permissions", value, ulong.MaxValue));
            return new(permission, true, true, steps, $"{role.Name} is allowed because Administrator bypasses channel overwrites.");
        }

        value = Apply(channel.PermissionOverwrites.SingleOrDefault(x => x.TargetType.Equals("Role", StringComparison.OrdinalIgnoreCase) && x.TargetId == guildId), "@everyone channel overwrite", value, steps);
        value = Apply(channel.PermissionOverwrites.SingleOrDefault(x => x.TargetType.Equals("Role", StringComparison.OrdinalIgnoreCase) && x.TargetId == roleId), $"role:{role.Name} channel overwrite", value, steps);

        var allowed = Has(value, bit);
        if (!permission.Equals("ViewChannel", StringComparison.OrdinalIgnoreCase) && !Has(value, PermissionBits["ViewChannel"]))
        {
            steps.Add(new("implicit ViewChannel dependency", "deny", value, value & ~bit));
            allowed = false;
        }
        if (permission is "MentionEveryone" && !Has(value, PermissionBits["SendMessages"]))
        {
            steps.Add(new("implicit SendMessages dependency", "deny", value, value & ~bit));
            allowed = false;
        }

        return new(permission, allowed, true, steps, $"{role.Name} is {(allowed ? "allowed" : "denied")} {permission} in #{channel.Name}.");
    }

    private static ulong Apply(PermissionOverwriteConfiguration? overwrite, string source, ulong value, ICollection<PermissionDecisionStep> steps)
    {
        if (overwrite is null) return value;
        var before = value;
        value &= ~overwrite.Denied;
        value |= overwrite.Allowed;
        steps.Add(new(source, $"deny={overwrite.Denied}; allow={overwrite.Allowed}", before, value));
        return value;
    }

    private static bool Has(ulong value, ulong bit) => (value & bit) == bit;
}
