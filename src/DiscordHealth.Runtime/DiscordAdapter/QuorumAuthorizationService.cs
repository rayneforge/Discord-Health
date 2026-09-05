using Discord;
using Discord.WebSocket;
using DiscordHealth.Runtime.Changes;

namespace DiscordHealth.Runtime.DiscordAdapter;

internal enum QuorumReadCapability
{
    FullConfiguration,
    Findings,
    Overview,
    Roles,
    Channels,
    Expressions,
    Events,
    VoiceStates,
    Bans,
    Invites,
    Integrations,
    Webhooks,
    AutoMod,
    AuditLog,
    Onboarding,
    WelcomeScreen,
    Coverage,
    PermissionAnalysis
}

internal interface IQuorumAuthorizationService
{
    Task DemandReadAsync(ulong guildId, ulong requesterId, QuorumReadCapability capability, CancellationToken cancellationToken = default);
    Task DemandResourceLookupAsync(ulong guildId, ulong requesterId, string resourceType, CancellationToken cancellationToken = default);
    Task DemandChangeAsync(ulong guildId, ulong requesterId, ChangeRequest request, CancellationToken cancellationToken = default);
    Task DemandAdministratorAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default);
}

internal sealed class DiscordQuorumAuthorizationService(
    IDiscordClientAccessor accessor,
    ILogger<DiscordQuorumAuthorizationService> logger) : IQuorumAuthorizationService
{
    public async Task DemandReadAsync(
        ulong guildId,
        ulong requesterId,
        QuorumReadCapability capability,
        CancellationToken cancellationToken = default)
    {
        var guild = GetGuild(guildId);
        var requester = await GetMemberAsync(guild, requesterId, cancellationToken);
        IReadOnlyList<GuildPermission> alternatives = capability switch
        {
            QuorumReadCapability.Roles => [GuildPermission.ManageRoles, GuildPermission.ViewAuditLog],
            QuorumReadCapability.Channels or QuorumReadCapability.PermissionAnalysis =>
                [GuildPermission.ManageChannels, GuildPermission.ManageRoles, GuildPermission.ViewAuditLog],
            QuorumReadCapability.Events => [GuildPermission.CreateEvents, GuildPermission.ManageEvents, GuildPermission.ViewAuditLog],
            QuorumReadCapability.Bans => [GuildPermission.BanMembers],
            QuorumReadCapability.Invites => [GuildPermission.ManageGuild, GuildPermission.ViewAuditLog],
            QuorumReadCapability.Integrations or QuorumReadCapability.AutoMod or
                QuorumReadCapability.Onboarding or QuorumReadCapability.WelcomeScreen => [GuildPermission.ManageGuild],
            QuorumReadCapability.Webhooks => [GuildPermission.ManageWebhooks],
            _ => [GuildPermission.ViewAuditLog]
        };
        DemandAny(guild, requester, alternatives, $"read {capability}");
    }

    public async Task DemandResourceLookupAsync(
        ulong guildId,
        ulong requesterId,
        string resourceType,
        CancellationToken cancellationToken = default)
    {
        var capability = resourceType.Trim().ToLowerInvariant() switch
        {
            "role" or "roles" => QuorumReadCapability.Roles,
            "channel" or "channels" or "category" or "categories" => QuorumReadCapability.Channels,
            _ => throw new ArgumentException("resourceType must be role, channel, or category.", nameof(resourceType))
        };
        await DemandReadAsync(guildId, requesterId, capability, cancellationToken);
    }

    public async Task DemandChangeAsync(
        ulong guildId,
        ulong requesterId,
        ChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        var guild = GetGuild(guildId);
        var requester = await GetMemberAsync(guild, requesterId, cancellationToken);
        var bot = (IGuildUser)guild.CurrentUser;
        var required = QuorumPermissionRequirements.ForChange(request.Action);

        DemandAll(guild, requester, required, "requester", request.Action);
        DemandAll(guild, bot, required, "Quorum", request.Action);
        QuorumSelfProtectionPolicy.Validate(
            request.Action,
            request.ResourceId,
            request.Arguments,
            guild.Id,
            bot.Id,
            bot.RoleIds);

        await ValidateHierarchyAsync(guild, requester, bot, request, cancellationToken);
        ValidateDelegatedPermissionBits(guild, requester, bot, request);
        logger.LogDebug(
            "Authorized Quorum action {Action} in guild {GuildId} for requester {RequesterId}.",
            request.Action,
            guild.Id,
            requesterId);
    }

    public async Task DemandAdministratorAsync(
        ulong guildId,
        ulong userId,
        CancellationToken cancellationToken = default)
    {
        var guild = GetGuild(guildId);
        var user = await GetMemberAsync(guild, userId, cancellationToken);
        if (user.Id != guild.OwnerId && !user.GuildPermissions.Administrator)
            throw new UnauthorizedAccessException("Administrator is required to approve or reject Quorum changes.");
    }

    private static async Task ValidateHierarchyAsync(
        SocketGuild guild,
        IGuildUser requester,
        IGuildUser bot,
        ChangeRequest request,
        CancellationToken cancellationToken)
    {
        var roleId = request.Action switch
        {
            ChangeActionType.ChangeRolePermissions or ChangeActionType.DeleteRole => request.ResourceId,
            ChangeActionType.AssignRole or ChangeActionType.RemoveRole or
                ChangeActionType.SetRoleChannelOverwrite or ChangeActionType.RemoveRoleChannelOverwrite =>
                ParseId(request.Arguments, "role_id"),
            _ => (ulong?)null
        };
        if (roleId.HasValue)
        {
            var role = guild.GetRole(roleId.Value)
                ?? throw new InvalidOperationException("The target role does not exist in this server.");
            if (role.IsManaged) throw new UnauthorizedAccessException("Managed Discord roles cannot be changed by Quorum.");
            if (requester.Id != guild.OwnerId && !requester.GuildPermissions.Administrator && requester.RoleIds.Contains(role.Id))
                throw new UnauthorizedAccessException("A requester cannot use Quorum to alter a role currently assigned to themselves.");
            DemandRoleHierarchy(guild, requester, role, "requester");
            DemandRoleHierarchy(guild, bot, role, "Quorum");
        }

        var channelId = request.Action switch
        {
            ChangeActionType.RenameChannel or ChangeActionType.ChangeChannelTopic or
                ChangeActionType.ChangeChannelSlowMode or ChangeActionType.DeleteChannel or
                ChangeActionType.SetRoleChannelOverwrite or ChangeActionType.RemoveRoleChannelOverwrite or
                ChangeActionType.SetThreadLocked or ChangeActionType.SetThreadArchived => request.ResourceId,
            _ => (ulong?)null
        };
        if (channelId.HasValue)
        {
            var channel = (IGuildChannel?)guild.GetChannel(channelId.Value) ?? guild.GetThreadChannel(channelId.Value)
                ?? throw new InvalidOperationException("The target channel or thread does not exist in this server.");
            foreach (var permission in QuorumPermissionRequirements.ForChange(request.Action))
            {
                DemandChannelPermission(guild, requester, channel, permission, "requester");
                DemandChannelPermission(guild, bot, channel, permission, "Quorum");
            }
        }

        var targetsMember = request.Action is ChangeActionType.AssignRole or ChangeActionType.RemoveRole or
            ChangeActionType.TimeoutMember or ChangeActionType.KickMember or ChangeActionType.BanMember;
        if (!targetsMember) return;
        if (request.ResourceId == requester.Id && requester.Id != guild.OwnerId && !requester.GuildPermissions.Administrator)
            throw new UnauthorizedAccessException("A requester cannot use Quorum to change their own roles or moderate themselves.");
        if (request.ResourceId == guild.OwnerId)
            throw new UnauthorizedAccessException("The server owner cannot be targeted by this action.");

        var target = await TryGetMemberAsync(guild, request.ResourceId, cancellationToken);
        if (target is null)
        {
            if (request.Action is ChangeActionType.AssignRole or ChangeActionType.RemoveRole or ChangeActionType.TimeoutMember or ChangeActionType.KickMember)
                throw new InvalidOperationException("The target must be a current server member.");
            return; // Discord permits banning a user who is not currently a member.
        }
        DemandMemberHierarchy(guild, requester, target, "requester");
        DemandMemberHierarchy(guild, bot, target, "Quorum");
    }

    private static void ValidateDelegatedPermissionBits(
        SocketGuild guild,
        IGuildUser requester,
        IGuildUser bot,
        ChangeRequest request)
    {
        string? raw = request.Action switch
        {
            ChangeActionType.CreateRole => request.Arguments.GetValueOrDefault("permissions") ?? "0",
            ChangeActionType.ChangeRolePermissions => Required(request.Arguments, "permissions"),
            ChangeActionType.SetRoleChannelOverwrite => Required(request.Arguments, "allow"),
            _ => null
        };
        if (raw is null) return;
        if (!ulong.TryParse(raw, out var requested))
            throw new ArgumentException("The proposed permissions must be a normalized Discord permission bitset.");

        DemandPermissionSubset(guild, requester, requested, "requester");
        DemandPermissionSubset(guild, bot, requested, "Quorum");
    }

    private static void DemandPermissionSubset(SocketGuild guild, IGuildUser principal, ulong requested, string subject)
    {
        if (principal.Id == guild.OwnerId || principal.GuildPermissions.Administrator) return;
        var unauthorized = requested & ~principal.GuildPermissions.RawValue;
        if (unauthorized != 0)
            throw new UnauthorizedAccessException($"The {subject} cannot grant Discord permissions it does not possess (unauthorized bits: {unauthorized}).");
    }

    private static void DemandRoleHierarchy(SocketGuild guild, IGuildUser principal, SocketRole target, string subject)
    {
        if (principal.Id == guild.OwnerId) return;
        if (HighestRolePosition(guild, principal) <= target.Position)
            throw new UnauthorizedAccessException($"The {subject}'s highest role must be above role '{target.Name}'.");
    }

    private static void DemandMemberHierarchy(SocketGuild guild, IGuildUser principal, IGuildUser target, string subject)
    {
        if (principal.Id == guild.OwnerId) return;
        if (HighestRolePosition(guild, principal) <= HighestRolePosition(guild, target))
            throw new UnauthorizedAccessException($"The {subject}'s highest role must be above the target member's highest role.");
    }

    private static void DemandChannelPermission(
        SocketGuild guild,
        IGuildUser principal,
        IGuildChannel channel,
        GuildPermission permission,
        string subject)
    {
        if (principal.Id == guild.OwnerId || principal.GuildPermissions.Administrator) return;
        var bit = Convert.ToUInt64(permission, System.Globalization.CultureInfo.InvariantCulture);
        if ((principal.GetPermissions(channel).RawValue & bit) != bit)
            throw new UnauthorizedAccessException(
                $"The {subject} lacks {QuorumPermissionRequirements.Display(permission)} in the target channel.");
    }

    private static int HighestRolePosition(SocketGuild guild, IGuildUser user) =>
        user.RoleIds.Select(guild.GetRole).Where(role => role is not null).Select(role => role!.Position).DefaultIfEmpty(0).Max();

    private static void DemandAny(SocketGuild guild, IGuildUser user, IReadOnlyList<GuildPermission> alternatives, string operation)
    {
        if (user.Id == guild.OwnerId || user.GuildPermissions.Administrator || alternatives.Any(user.GuildPermissions.Has)) return;
        throw new UnauthorizedAccessException(
            $"You need one of these Discord permissions to {operation}: {string.Join(", ", alternatives.Select(QuorumPermissionRequirements.Display))}.");
    }

    private static void DemandAll(
        SocketGuild guild,
        IGuildUser user,
        IReadOnlyList<GuildPermission> required,
        string subject,
        ChangeActionType action)
    {
        if (user.Id == guild.OwnerId || user.GuildPermissions.Administrator) return;
        var missing = required.Where(permission => !user.GuildPermissions.Has(permission)).ToArray();
        if (missing.Length > 0)
            throw new UnauthorizedAccessException(
                $"The {subject} lacks {string.Join(" + ", missing.Select(QuorumPermissionRequirements.Display))} required for {action}.");
    }

    private SocketGuild GetGuild(ulong guildId) =>
        accessor.Client.GetGuild(guildId) ?? throw new InvalidOperationException("The invoking Discord server is unavailable.");

    private static async Task<IGuildUser> GetMemberAsync(SocketGuild guild, ulong userId, CancellationToken cancellationToken) =>
        await TryGetMemberAsync(guild, userId, cancellationToken)
        ?? throw new UnauthorizedAccessException("The requester is no longer a member of this server.");

    private static async Task<IGuildUser?> TryGetMemberAsync(SocketGuild guild, ulong userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return guild.GetUser(userId) ?? await ((IGuild)guild).GetUserAsync(userId, CacheMode.AllowDownload);
        }
        catch (Discord.Net.HttpException exception) when ((int)exception.HttpCode == 404)
        {
            return null;
        }
    }

    private static ulong ParseId(IReadOnlyDictionary<string, string>? arguments, string key) =>
        ulong.TryParse(Required(arguments, key), out var id)
            ? id
            : throw new ArgumentException($"{key} must be a Discord ID.");

    private static string Required(IReadOnlyDictionary<string, string>? arguments, string key) =>
        arguments?.TryGetValue(key, out var value) == true && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required argument '{key}' is missing.");
}

internal static class QuorumPermissionRequirements
{
    public static IReadOnlyList<GuildPermission> ForChange(ChangeActionType action) => action switch
    {
        ChangeActionType.CreateTextChannel or ChangeActionType.CreateCategory or ChangeActionType.RenameChannel or
            ChangeActionType.ChangeChannelTopic or ChangeActionType.ChangeChannelSlowMode or ChangeActionType.DeleteChannel =>
            [GuildPermission.ManageChannels],
        ChangeActionType.CreateRole or ChangeActionType.ChangeRolePermissions or ChangeActionType.DeleteRole or
            ChangeActionType.AssignRole or ChangeActionType.RemoveRole or ChangeActionType.SetRoleChannelOverwrite or
            ChangeActionType.RemoveRoleChannelOverwrite => [GuildPermission.ManageRoles],
        ChangeActionType.TimeoutMember => [GuildPermission.ModerateMembers],
        ChangeActionType.KickMember => [GuildPermission.KickMembers],
        ChangeActionType.BanMember or ChangeActionType.UnbanMember => [GuildPermission.BanMembers],
        ChangeActionType.DeleteWebhook => [GuildPermission.ManageWebhooks],
        ChangeActionType.RevokeInvite => [GuildPermission.ManageGuild],
        ChangeActionType.CreateScheduledEvent => [GuildPermission.CreateEvents],
        ChangeActionType.SetThreadLocked or ChangeActionType.SetThreadArchived => [GuildPermission.ManageThreads],
        ChangeActionType.CreateAutoModKeywordRule or ChangeActionType.SetAutoModRuleEnabled or
            ChangeActionType.DeleteAutoModRule or ChangeActionType.UpdateWelcomeScreen => [GuildPermission.ManageGuild],
        ChangeActionType.UpdateOnboarding => [GuildPermission.ManageGuild, GuildPermission.ManageRoles],
        _ => throw new NotSupportedException($"No Discord permission mapping exists for {action}.")
    };

    public static string ForChangeDisplay(ChangeActionType action) =>
        string.Join(" + ", ForChange(action).Select(Display));

    public static string Display(GuildPermission permission)
    {
        var name = permission.ToString();
        return string.Concat(name.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"_{character}" : char.ToUpperInvariant(character).ToString()));
    }
}
