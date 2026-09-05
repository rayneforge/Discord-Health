using Discord;
using Discord.Net;
using Discord.WebSocket;
using DiscordHealth.Runtime.Changes;

namespace DiscordHealth.Runtime.DiscordAdapter;

internal sealed class DiscordApprovedChangeExecutor(
    IDiscordClientAccessor accessor,
    ILogger<DiscordApprovedChangeExecutor> logger) : IApprovedChangeExecutor
{
    public async Task<ChangeSpecification> CreateSpecificationAsync(
        ulong guildId,
        ChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        var guild = GetGuild(guildId);
        var arguments = request.Arguments;
        EnforceSelfProtection(guild, request.Action, request.ResourceId, arguments);
        ChangeSpecification specification = request.Action switch
        {
            ChangeActionType.CreateTextChannel => CreateResource(
                request.Action, "text channel", Required(arguments, "name"), "MANAGE_CHANNELS", arguments),
            ChangeActionType.CreateCategory => CreateResource(
                request.Action, "category", Required(arguments, "name"), "MANAGE_CHANNELS", arguments),
            ChangeActionType.RenameChannel => Existing(
                request.Action, "channel", request.ResourceId, "name", GetChannel(guild, request.ResourceId).Name,
                Required(arguments, "name"), "MANAGE_CHANNELS", arguments),
            ChangeActionType.ChangeChannelTopic => Existing(
                request.Action, "channel", request.ResourceId, "topic", GetTextChannel(guild, request.ResourceId).Topic ?? string.Empty,
                arguments.GetValueOrDefault("topic") ?? string.Empty, "MANAGE_CHANNELS", arguments),
            ChangeActionType.ChangeChannelSlowMode => CreateSlowMode(guild, request, arguments),
            ChangeActionType.DeleteChannel => Existing(
                request.Action, "channel", request.ResourceId, "existence", GetChannel(guild, request.ResourceId).Name,
                "deleted", "MANAGE_CHANNELS", arguments),
            ChangeActionType.CreateRole => CreateResource(
                request.Action, "role", Required(arguments, "name"), "MANAGE_ROLES", arguments),
            ChangeActionType.ChangeRolePermissions => Existing(
                request.Action, "role", request.ResourceId, "permissions", GetRole(guild, request.ResourceId).Permissions.RawValue.ToString(),
                Required(arguments, "permissions"), "MANAGE_ROLES", arguments),
            ChangeActionType.DeleteRole => Existing(
                request.Action, "role", request.ResourceId, "existence", GetRole(guild, request.ResourceId).Name,
                "deleted", "MANAGE_ROLES", arguments),
            ChangeActionType.AssignRole => Membership(guild, request, arguments, assign: true),
            ChangeActionType.RemoveRole => Membership(guild, request, arguments, assign: false),
            ChangeActionType.TimeoutMember => new(
                request.Action, "member", request.ResourceId, "timeout",
                (await GetUserAsync(guild, request.ResourceId)).TimedOutUntil > DateTimeOffset.UtcNow ? "timed_out" : "not_timed_out",
                "timed_out", "MODERATE_MEMBERS", $"member {request.ResourceId}", arguments),
            ChangeActionType.KickMember => new(
                request.Action, "member", request.ResourceId, "membership", "member_present", "removed",
                "KICK_MEMBERS", $"member {request.ResourceId}", arguments),
            ChangeActionType.BanMember => new(
                request.Action, "member", request.ResourceId, "ban", await BanStateAsync(guild, request.ResourceId), "banned",
                "BAN_MEMBERS", $"member {request.ResourceId}", arguments),
            ChangeActionType.UnbanMember => new(
                request.Action, "member", request.ResourceId, "ban", await BanStateAsync(guild, request.ResourceId), "not_banned",
                "BAN_MEMBERS", $"member {request.ResourceId}", arguments),
            ChangeActionType.DeleteWebhook => await WebhookDeleteSpecificationAsync(guild, request, arguments),
            ChangeActionType.RevokeInvite => await InviteRevokeSpecificationAsync(guild, request, arguments),
            ChangeActionType.CreateScheduledEvent => CreateEventSpecification(request, arguments),
            ChangeActionType.SetRoleChannelOverwrite => RoleOverwriteSpecification(guild, request, arguments, remove: false),
            ChangeActionType.RemoveRoleChannelOverwrite => RoleOverwriteSpecification(guild, request, arguments, remove: true),
            ChangeActionType.SetThreadLocked => ThreadSpecification(request, arguments, "locked", GetThread(guild, request.ResourceId).IsLocked.ToString(), Required(arguments, "locked")),
            ChangeActionType.SetThreadArchived => ThreadSpecification(request, arguments, "archived", GetThread(guild, request.ResourceId).IsArchived.ToString(), Required(arguments, "archived")),
            ChangeActionType.CreateAutoModKeywordRule => CreateAutoModSpecification(request, arguments),
            ChangeActionType.SetAutoModRuleEnabled => await AutoModEnabledSpecificationAsync(guild, request, arguments),
            ChangeActionType.DeleteAutoModRule => await AutoModDeleteSpecificationAsync(guild, request, arguments),
            ChangeActionType.UpdateWelcomeScreen => await WelcomeScreenSpecificationAsync(guild, request, arguments),
            ChangeActionType.UpdateOnboarding => await OnboardingSpecificationAsync(guild, request, arguments),
            _ => throw new NotSupportedException($"Unsupported action {request.Action}.")
        };
        return specification with
        {
            RequiredDiscordPermission = QuorumPermissionRequirements.ForChangeDisplay(request.Action)
        };
    }

    public async Task<string> ObserveAsync(ulong guildId, ChangeSpecification change, CancellationToken cancellationToken = default)
    {
        var guild = GetGuild(guildId);
        return change.Action switch
        {
            ChangeActionType.CreateTextChannel => guild.TextChannels.Any(x => x.Name == Required(change.Arguments, "name")) ? "created" : "absent",
            ChangeActionType.CreateCategory => guild.CategoryChannels.Any(x => x.Name == Required(change.Arguments, "name")) ? "created" : "absent",
            ChangeActionType.RenameChannel => GetChannel(guild, change.ResourceId).Name,
            ChangeActionType.ChangeChannelTopic => GetTextChannel(guild, change.ResourceId).Topic ?? string.Empty,
            ChangeActionType.ChangeChannelSlowMode => GetTextChannel(guild, change.ResourceId).SlowModeInterval.ToString(),
            ChangeActionType.DeleteChannel => guild.GetChannel(change.ResourceId) is null ? "deleted" : guild.GetChannel(change.ResourceId).Name,
            ChangeActionType.CreateRole => guild.Roles.Any(x => x.Name == Required(change.Arguments, "name")) ? "created" : "absent",
            ChangeActionType.ChangeRolePermissions => GetRole(guild, change.ResourceId).Permissions.RawValue.ToString(),
            ChangeActionType.DeleteRole => guild.GetRole(change.ResourceId) is null ? "deleted" : guild.GetRole(change.ResourceId).Name,
            ChangeActionType.AssignRole => (await GetUserAsync(guild, change.ResourceId)).RoleIds.Contains(ParseId(change.Arguments, "role_id")) ? "assigned" : "not_assigned",
            ChangeActionType.RemoveRole => (await GetUserAsync(guild, change.ResourceId)).RoleIds.Contains(ParseId(change.Arguments, "role_id")) ? "assigned" : "not_assigned",
            ChangeActionType.TimeoutMember => (await GetUserAsync(guild, change.ResourceId)).TimedOutUntil > DateTimeOffset.UtcNow ? "timed_out" : "not_timed_out",
            ChangeActionType.KickMember => await MemberStateAsync(guild, change.ResourceId),
            ChangeActionType.BanMember or ChangeActionType.UnbanMember => await BanStateAsync(guild, change.ResourceId),
            ChangeActionType.DeleteWebhook => (await guild.GetWebhooksAsync()).Any(x => x.Id == change.ResourceId) ? "present" : "deleted",
            ChangeActionType.RevokeInvite => (await guild.GetInvitesAsync()).Any(x => x.Code == Required(change.Arguments, "code")) ? "active" : "revoked",
            ChangeActionType.CreateScheduledEvent => guild.Events.Any(x => x.Name == Required(change.Arguments, "name")) ? "created" : "absent",
            ChangeActionType.SetRoleChannelOverwrite or ChangeActionType.RemoveRoleChannelOverwrite => ObserveRoleOverwrite(guild, change),
            ChangeActionType.SetThreadLocked => GetThread(guild, change.ResourceId).IsLocked.ToString(),
            ChangeActionType.SetThreadArchived => GetThread(guild, change.ResourceId).IsArchived.ToString(),
            ChangeActionType.CreateAutoModKeywordRule => (await guild.GetAutoModRulesAsync()).Any(x => x.Name == Required(change.Arguments, "name")) ? "created" : "absent",
            ChangeActionType.SetAutoModRuleEnabled => (await GetAutoModRuleAsync(guild, change.ResourceId)).Enabled.ToString(),
            ChangeActionType.DeleteAutoModRule => (await guild.GetAutoModRulesAsync()).Any(x => x.Id == change.ResourceId) ? "present" : "deleted",
            ChangeActionType.UpdateWelcomeScreen => WelcomeValue(guild, await guild.GetWelcomeScreenAsync()),
            ChangeActionType.UpdateOnboarding => OnboardingValue(await guild.GetOnboardingAsync()),
            _ => throw new NotSupportedException($"Unsupported action {change.Action}.")
        };
    }

    public async Task ExecuteAsync(ulong guildId, ChangeSpecification change, CancellationToken cancellationToken = default)
    {
        var guild = GetGuild(guildId);
        EnforceSelfProtection(guild, change.Action, change.ResourceId, change.Arguments);
        EnsurePermissions(guild, change.Action);
        var requestOptions = new RequestOptions { AuditLogReason = $"Quorum approved action {change.Action}" };

        switch (change.Action)
        {
            case ChangeActionType.CreateTextChannel:
                await guild.CreateTextChannelAsync(
                    Required(change.Arguments, "name"),
                    properties =>
                    {
                        var categoryId = ResolveCategoryId(guild, change.Arguments);
                        if (categoryId.HasValue) properties.CategoryId = categoryId;
                        if (change.Arguments?.TryGetValue("topic", out var topic) == true) properties.Topic = topic;
                        if (change.Arguments?.TryGetValue("nsfw", out var nsfw) == true && bool.TryParse(nsfw, out var isNsfw)) properties.IsNsfw = isNsfw;
                    },
                    requestOptions);
                break;
            case ChangeActionType.CreateCategory:
                await ((IGuild)guild).CreateCategoryAsync(Required(change.Arguments, "name"), options: requestOptions);
                break;
            case ChangeActionType.RenameChannel:
                await GetChannel(guild, change.ResourceId).ModifyAsync(x => x.Name = change.After, requestOptions);
                break;
            case ChangeActionType.ChangeChannelTopic:
                await GetTextChannel(guild, change.ResourceId).ModifyAsync(x => x.Topic = change.After, requestOptions);
                break;
            case ChangeActionType.ChangeChannelSlowMode:
                await GetTextChannel(guild, change.ResourceId).ModifyAsync(
                    x => x.SlowModeInterval = int.Parse(change.After, System.Globalization.CultureInfo.InvariantCulture),
                    requestOptions);
                break;
            case ChangeActionType.DeleteChannel:
                await GetChannel(guild, change.ResourceId).DeleteAsync(requestOptions);
                break;
            case ChangeActionType.CreateRole:
                var permissions = change.Arguments?.TryGetValue("permissions", out var raw) == true
                    ? new GuildPermissions(ulong.Parse(raw, System.Globalization.CultureInfo.InvariantCulture))
                    : GuildPermissions.None;
                await guild.CreateRoleAsync(
                    Required(change.Arguments, "name"),
                    permissions,
                    isHoisted: false,
                    options: requestOptions);
                break;
            case ChangeActionType.ChangeRolePermissions:
                await GetRole(guild, change.ResourceId).ModifyAsync(
                    x => x.Permissions = new GuildPermissions(ulong.Parse(change.After, System.Globalization.CultureInfo.InvariantCulture)),
                    requestOptions);
                break;
            case ChangeActionType.DeleteRole:
                await GetRole(guild, change.ResourceId).DeleteAsync(requestOptions);
                break;
            case ChangeActionType.AssignRole:
                await (await GetUserAsync(guild, change.ResourceId)).AddRoleAsync(ParseId(change.Arguments, "role_id"), requestOptions);
                break;
            case ChangeActionType.RemoveRole:
                await (await GetUserAsync(guild, change.ResourceId)).RemoveRoleAsync(ParseId(change.Arguments, "role_id"), requestOptions);
                break;
            case ChangeActionType.TimeoutMember:
                await (await GetUserAsync(guild, change.ResourceId)).SetTimeOutAsync(
                    TimeSpan.FromMinutes(int.Parse(Required(change.Arguments, "minutes"), System.Globalization.CultureInfo.InvariantCulture)),
                    requestOptions);
                break;
            case ChangeActionType.KickMember:
                await (await GetUserAsync(guild, change.ResourceId)).KickAsync(change.Arguments?.GetValueOrDefault("reason"), requestOptions);
                break;
            case ChangeActionType.BanMember:
                await guild.AddBanAsync(
                    change.ResourceId,
                    int.Parse(change.Arguments?.GetValueOrDefault("delete_message_days") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    change.Arguments?.GetValueOrDefault("reason"),
                    requestOptions);
                break;
            case ChangeActionType.UnbanMember:
                await guild.RemoveBanAsync(change.ResourceId, requestOptions);
                break;
            case ChangeActionType.DeleteWebhook:
                var webhook = (await guild.GetWebhooksAsync()).SingleOrDefault(x => x.Id == change.ResourceId)
                    ?? throw new InvalidOperationException("Webhook no longer exists.");
                await webhook.DeleteAsync(requestOptions);
                break;
            case ChangeActionType.RevokeInvite:
                var invite = (await guild.GetInvitesAsync()).SingleOrDefault(x => x.Code == Required(change.Arguments, "code"))
                    ?? throw new InvalidOperationException("Invite no longer exists.");
                await invite.DeleteAsync(requestOptions);
                break;
            case ChangeActionType.CreateScheduledEvent:
                await guild.CreateEventAsync(
                    Required(change.Arguments, "name"),
                    DateTimeOffset.Parse(Required(change.Arguments, "start"), System.Globalization.CultureInfo.InvariantCulture),
                    GuildScheduledEventType.External,
                    GuildScheduledEventPrivacyLevel.Private,
                    change.Arguments?.GetValueOrDefault("description"),
                    DateTimeOffset.Parse(Required(change.Arguments, "end"), System.Globalization.CultureInfo.InvariantCulture),
                    location: Required(change.Arguments, "location"),
                    options: requestOptions);
                break;
            case ChangeActionType.SetRoleChannelOverwrite:
                await GetChannel(guild, change.ResourceId).AddPermissionOverwriteAsync(
                    GetRole(guild, ParseId(change.Arguments, "role_id")),
                    new OverwritePermissions(
                        ulong.Parse(Required(change.Arguments, "allow"), System.Globalization.CultureInfo.InvariantCulture),
                        ulong.Parse(Required(change.Arguments, "deny"), System.Globalization.CultureInfo.InvariantCulture)),
                    requestOptions);
                break;
            case ChangeActionType.RemoveRoleChannelOverwrite:
                await GetChannel(guild, change.ResourceId).RemovePermissionOverwriteAsync(
                    GetRole(guild, ParseId(change.Arguments, "role_id")),
                    requestOptions);
                break;
            case ChangeActionType.SetThreadLocked:
                await GetThread(guild, change.ResourceId).ModifyAsync(x => x.Locked = bool.Parse(change.After), requestOptions);
                break;
            case ChangeActionType.SetThreadArchived:
                await GetThread(guild, change.ResourceId).ModifyAsync(x => x.Archived = bool.Parse(change.After), requestOptions);
                break;
            case ChangeActionType.CreateAutoModKeywordRule:
                await guild.CreateAutoModRuleAsync(properties =>
                {
                    properties.Name = Required(change.Arguments, "name");
                    properties.EventType = AutoModEventType.MessageSend;
                    properties.TriggerType = AutoModTriggerType.Keyword;
                    properties.KeywordFilter = Required(change.Arguments, "keywords").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    properties.Actions = new[]
                    {
                        new AutoModRuleActionProperties
                        {
                            Type = AutoModActionType.BlockMessage,
                            CustomMessage = change.Arguments?.GetValueOrDefault("custom_message")
                        }
                    };
                    properties.Enabled = bool.Parse(change.Arguments?.GetValueOrDefault("enabled") ?? "true");
                }, requestOptions);
                break;
            case ChangeActionType.SetAutoModRuleEnabled:
                await (await GetAutoModRuleAsync(guild, change.ResourceId)).ModifyAsync(x => x.Enabled = bool.Parse(change.After), requestOptions);
                break;
            case ChangeActionType.DeleteAutoModRule:
                await (await GetAutoModRuleAsync(guild, change.ResourceId)).DeleteAsync(requestOptions);
                break;
            case ChangeActionType.UpdateWelcomeScreen:
                var welcome = await guild.GetWelcomeScreenAsync();
                await ((IGuild)guild).ModifyWelcomeScreenAsync(
                    bool.Parse(Required(change.Arguments, "enabled")),
                    welcome?.Channels.Select(x => new WelcomeScreenChannelProperties(x.Id, x.Description, x.Emoji)).ToArray() ?? [],
                    change.Arguments?.GetValueOrDefault("description") ?? welcome?.Description,
                    requestOptions);
                break;
            case ChangeActionType.UpdateOnboarding:
                var onboarding = await guild.GetOnboardingAsync();
                await onboarding.ModifyAsync(x =>
                {
                    if (change.Arguments?.TryGetValue("enabled", out var enabled) == true) x.IsEnabled = bool.Parse(enabled);
                    if (change.Arguments?.TryGetValue("mode", out var mode) == true) x.Mode = Enum.Parse<GuildOnboardingMode>(mode, true);
                }, requestOptions);
                break;
            default:
                throw new NotSupportedException($"Unsupported action {change.Action}.");
        }
    }

    private static ChangeSpecification CreateResource(
        ChangeActionType action,
        string resourceType,
        string name,
        string permission,
        IReadOnlyDictionary<string, string> arguments) =>
        new(action, resourceType, 0, "existence", "absent", "created", permission, $"{resourceType} {name}", arguments);

    private static ChangeSpecification Existing(
        ChangeActionType action,
        string resourceType,
        ulong resourceId,
        string property,
        string before,
        string after,
        string permission,
        IReadOnlyDictionary<string, string> arguments) =>
        new(action, resourceType, resourceId, property, before, after, permission, $"<{resourceType}:{resourceId}>", arguments);

    private static ChangeSpecification CreateSlowMode(SocketGuild guild, ChangeRequest request, IReadOnlyDictionary<string, string> arguments)
    {
        var seconds = int.Parse(Required(arguments, "seconds"), System.Globalization.CultureInfo.InvariantCulture);
        if (seconds is < 0 or > 21600) throw new ArgumentOutOfRangeException(nameof(seconds), "Discord slowmode must be between 0 and 21600 seconds.");
        var channel = GetTextChannel(guild, request.ResourceId);
        return Existing(request.Action, "channel", request.ResourceId, "slow_mode_seconds", channel.SlowModeInterval.ToString(), seconds.ToString(), "MANAGE_CHANNELS", arguments);
    }

    private static ChangeSpecification Membership(SocketGuild guild, ChangeRequest request, IReadOnlyDictionary<string, string> arguments, bool assign)
    {
        var role = GetRole(guild, ParseId(arguments, "role_id"));
        return new(
            request.Action,
            "member",
            request.ResourceId,
            "role_membership",
            assign ? "not_assigned" : "assigned",
            assign ? "assigned" : "not_assigned",
            "MANAGE_ROLES",
            $"member {request.ResourceId} / role {role.Name}",
            arguments);
    }

    private static async Task<ChangeSpecification> WebhookDeleteSpecificationAsync(
        SocketGuild guild,
        ChangeRequest request,
        IReadOnlyDictionary<string, string> arguments)
    {
        var webhook = (await guild.GetWebhooksAsync()).SingleOrDefault(x => x.Id == request.ResourceId)
            ?? throw new InvalidOperationException("Webhook does not exist or is inaccessible.");
        return new(request.Action, "webhook", request.ResourceId, "existence", "present", "deleted", "MANAGE_WEBHOOKS", $"webhook {webhook.Name ?? request.ResourceId.ToString()}", arguments);
    }

    private static async Task<ChangeSpecification> InviteRevokeSpecificationAsync(
        SocketGuild guild,
        ChangeRequest request,
        IReadOnlyDictionary<string, string> arguments)
    {
        var invite = (await guild.GetInvitesAsync()).SingleOrDefault(x => x.Code == Required(arguments, "code"))
            ?? throw new InvalidOperationException("Invite does not exist or is inaccessible.");
        return new(request.Action, "invite", invite.ChannelId, "existence", "active", "revoked", "MANAGE_GUILD", $"invite for channel {invite.ChannelId}", arguments);
    }

    private static ChangeSpecification CreateEventSpecification(ChangeRequest request, IReadOnlyDictionary<string, string> arguments)
    {
        _ = DateTimeOffset.Parse(Required(arguments, "start"), System.Globalization.CultureInfo.InvariantCulture);
        _ = DateTimeOffset.Parse(Required(arguments, "end"), System.Globalization.CultureInfo.InvariantCulture);
        return CreateResource(request.Action, "scheduled event", Required(arguments, "name"), "CREATE_EVENTS", arguments);
    }

    private static ChangeSpecification RoleOverwriteSpecification(
        SocketGuild guild,
        ChangeRequest request,
        IReadOnlyDictionary<string, string> arguments,
        bool remove)
    {
        var channel = GetChannel(guild, request.ResourceId);
        var role = GetRole(guild, ParseId(arguments, "role_id"));
        var before = FormatOverwrite(channel.GetPermissionOverwrite(role));
        var after = remove ? "absent" : $"{Required(arguments, "allow")}:{Required(arguments, "deny")}";
        return new(request.Action, "channel role overwrite", request.ResourceId, "permission_overwrite", before, after,
            "MANAGE_ROLES", $"channel {channel.Name} / role {role.Name}", arguments);
    }

    private static ChangeSpecification ThreadSpecification(
        ChangeRequest request,
        IReadOnlyDictionary<string, string> arguments,
        string property,
        string before,
        string after)
    {
        _ = bool.Parse(after);
        return new(request.Action, "thread", request.ResourceId, property, before, after, "MANAGE_THREADS", $"thread {request.ResourceId}", arguments);
    }

    private static ChangeSpecification CreateAutoModSpecification(ChangeRequest request, IReadOnlyDictionary<string, string> arguments)
    {
        _ = Required(arguments, "keywords");
        return CreateResource(request.Action, "AutoMod rule", Required(arguments, "name"), "MANAGE_GUILD", arguments);
    }

    private static async Task<ChangeSpecification> AutoModEnabledSpecificationAsync(
        SocketGuild guild,
        ChangeRequest request,
        IReadOnlyDictionary<string, string> arguments)
    {
        var rule = await GetAutoModRuleAsync(guild, request.ResourceId);
        var after = bool.Parse(Required(arguments, "enabled")).ToString();
        return new(request.Action, "AutoMod rule", request.ResourceId, "enabled", rule.Enabled.ToString(), after,
            "MANAGE_GUILD", $"AutoMod rule {rule.Name}", arguments);
    }

    private static async Task<ChangeSpecification> AutoModDeleteSpecificationAsync(
        SocketGuild guild,
        ChangeRequest request,
        IReadOnlyDictionary<string, string> arguments)
    {
        var rule = await GetAutoModRuleAsync(guild, request.ResourceId);
        return new(request.Action, "AutoMod rule", request.ResourceId, "existence", "present", "deleted",
            "MANAGE_GUILD", $"AutoMod rule {rule.Name}", arguments);
    }

    private static async Task<ChangeSpecification> WelcomeScreenSpecificationAsync(
        SocketGuild guild,
        ChangeRequest request,
        IReadOnlyDictionary<string, string> arguments)
    {
        var welcome = await guild.GetWelcomeScreenAsync();
        return new(request.Action, "welcome screen", 0, "settings", WelcomeValue(guild, welcome),
            $"{Required(arguments, "enabled")}|{arguments.GetValueOrDefault("description") ?? welcome?.Description}",
            "MANAGE_GUILD", "server welcome screen", arguments);
    }

    private static async Task<ChangeSpecification> OnboardingSpecificationAsync(
        SocketGuild guild,
        ChangeRequest request,
        IReadOnlyDictionary<string, string> arguments)
    {
        var onboarding = await guild.GetOnboardingAsync();
        return new(request.Action, "onboarding", 0, "settings", OnboardingValue(onboarding),
            $"{arguments.GetValueOrDefault("enabled") ?? onboarding.IsEnabled.ToString()}|{arguments.GetValueOrDefault("mode") ?? onboarding.Mode.ToString()}",
            "MANAGE_GUILD", "server onboarding", arguments);
    }

    private static string ObserveRoleOverwrite(SocketGuild guild, ChangeSpecification change)
    {
        var channel = GetChannel(guild, change.ResourceId);
        var role = GetRole(guild, ParseId(change.Arguments, "role_id"));
        return FormatOverwrite(channel.GetPermissionOverwrite(role));
    }

    private static string FormatOverwrite(OverwritePermissions? overwrite) =>
        overwrite is null ? "absent" : $"{overwrite.Value.AllowValue}:{overwrite.Value.DenyValue}";

    private static SocketThreadChannel GetThread(SocketGuild guild, ulong channelId) =>
        guild.GetThreadChannel(channelId)
        ?? throw new InvalidOperationException("The target thread no longer exists or is inaccessible.");

    private static async Task<IAutoModRule> GetAutoModRuleAsync(SocketGuild guild, ulong ruleId) =>
        (await guild.GetAutoModRulesAsync()).SingleOrDefault(x => x.Id == ruleId)
        ?? throw new InvalidOperationException("AutoMod rule no longer exists or is inaccessible.");

    private static string WelcomeValue(SocketGuild guild, WelcomeScreen? welcome) =>
        $"{guild.Features.HasFeature(GuildFeature.WelcomeScreenEnabled)}|{welcome?.Description}";

    private static string OnboardingValue(IGuildOnboarding onboarding) =>
        $"{onboarding.IsEnabled}|{onboarding.Mode}";

    private static async Task<string> MemberStateAsync(SocketGuild guild, ulong userId)
    {
        try { return await ((IGuild)guild).GetUserAsync(userId, CacheMode.AllowDownload) is null ? "removed" : "member_present"; }
        catch (HttpException exception) when ((int)exception.HttpCode == 404) { return "removed"; }
    }

    private static async Task<string> BanStateAsync(SocketGuild guild, ulong userId)
    {
        try { return await guild.GetBanAsync(userId) is null ? "not_banned" : "banned"; }
        catch (HttpException exception) when ((int)exception.HttpCode == 404) { return "not_banned"; }
    }

    private SocketGuild GetGuild(ulong guildId) =>
        accessor.Client.GetGuild(guildId) ?? throw new InvalidOperationException("Guild is unavailable.");

    private static SocketGuildChannel GetChannel(SocketGuild guild, ulong channelId) =>
        guild.GetChannel(channelId) ?? throw new InvalidOperationException("The target channel no longer exists or is inaccessible.");

    private static SocketTextChannel GetTextChannel(SocketGuild guild, ulong channelId) =>
        guild.GetTextChannel(channelId) ?? throw new InvalidOperationException("The target must be a visible text channel.");

    private static SocketRole GetRole(SocketGuild guild, ulong roleId) =>
        guild.GetRole(roleId) ?? throw new InvalidOperationException("The target role no longer exists or is inaccessible.");

    private static async Task<IGuildUser> GetUserAsync(SocketGuild guild, ulong userId) =>
        await ((IGuild)guild).GetUserAsync(userId, CacheMode.AllowDownload) ?? throw new InvalidOperationException("The target member is unavailable.");

    private static string Required(IReadOnlyDictionary<string, string>? arguments, string key) =>
        arguments?.TryGetValue(key, out var value) == true && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required argument '{key}' is missing.");

    private static ulong ParseId(IReadOnlyDictionary<string, string>? arguments, string key) =>
        ulong.TryParse(Required(arguments, key), out var value)
            ? value
            : throw new ArgumentException($"Argument '{key}' must be a Discord ID.");

    private static bool TryId(IReadOnlyDictionary<string, string>? arguments, string key, out ulong value)
    {
        value = 0;
        return arguments?.TryGetValue(key, out var text) == true && ulong.TryParse(text, out value);
    }

    private static ulong? ResolveCategoryId(SocketGuild guild, IReadOnlyDictionary<string, string>? arguments)
    {
        if (arguments?.TryGetValue("category_id", out var idText) == true && !string.IsNullOrWhiteSpace(idText))
        {
            if (!ulong.TryParse(idText, out var id)) throw new ArgumentException("category_id must be a Discord ID.");
            return guild.GetCategoryChannel(id)?.Id
                ?? throw new InvalidOperationException("The selected category does not exist in this server.");
        }
        if (arguments?.TryGetValue("category_name", out var name) != true || string.IsNullOrWhiteSpace(name)) return null;

        var matches = guild.CategoryChannels.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].Id,
            0 => throw new InvalidOperationException($"Category '{name}' does not exist yet. Approve its category proposal before approving this channel proposal."),
            _ => throw new InvalidOperationException($"More than one category is named '{name}'. Use category_id instead.")
        };
    }

    private static void EnsurePermissions(SocketGuild guild, ChangeActionType action)
    {
        if (guild.CurrentUser.GuildPermissions.Administrator) return;
        var missing = QuorumPermissionRequirements.ForChange(action)
            .Where(permission => !guild.CurrentUser.GuildPermissions.Has(permission))
            .ToArray();
        if (missing.Length > 0)
            throw new UnauthorizedAccessException(
                $"Quorum lacks {string.Join(" + ", missing.Select(QuorumPermissionRequirements.Display))}.");
    }

    private void EnforceSelfProtection(
        SocketGuild guild,
        ChangeActionType action,
        ulong resourceId,
        IReadOnlyDictionary<string, string>? arguments)
    {
        try
        {
            QuorumSelfProtectionPolicy.Validate(
                action,
                resourceId,
                arguments,
                guild.Id,
                guild.CurrentUser.Id,
                guild.CurrentUser.Roles.Select(role => role.Id).ToArray());
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Blocked Quorum self-targeting action {Action} in guild {GuildId} for resource {ResourceId}.",
                action,
                guild.Id,
                resourceId);
            throw;
        }
    }
}
