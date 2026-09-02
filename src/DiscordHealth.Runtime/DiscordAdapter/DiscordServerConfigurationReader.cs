using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using DiscordHealth.Runtime.Analysis;
using DiscordHealth.Runtime.ServerConfiguration;

namespace DiscordHealth.Runtime.DiscordAdapter;

internal sealed class DiscordServerConfigurationReader(IDiscordClientAccessor accessor, ISecurityAnalyzer analyzer) : IServerConfigurationReader
{
    public async Task<ServerConfigurationSnapshot> CaptureAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var guild = accessor.Client.GetGuild(guildId)
            ?? throw new InvalidOperationException($"Guild {guildId} is not available to this bot.");
        var at = DateTimeOffset.UtcNow;

        var guildResult = CollectorResult<GuildConfiguration>.Available(MapGuild(guild), at);
        var roles = CollectorResult<IReadOnlyList<RoleConfiguration>>.Available(guild.Roles.OrderByDescending(x => x.Position).Select(MapRole).ToArray(), at);
        var channels = CollectorResult<IReadOnlyList<ChannelConfiguration>>.Available(guild.Channels.OrderBy(x => x.Position).Select(MapChannel).ToArray(), at);
        var emojis = CollectorResult<IReadOnlyList<EmojiConfiguration>>.Available(guild.Emotes.Select(x => new EmojiConfiguration(x.Id, x.Name, x.Animated, x.IsAvailable ?? false, x.RoleIds.ToArray())).ToArray(), at);
        var stickers = CollectorResult<IReadOnlyList<StickerConfiguration>>.Available(guild.Stickers.Select(x => new StickerConfiguration(x.Id, x.Name, x.Description, string.Join(",", x.Tags), x.IsAvailable ?? false)).ToArray(), at);
        var events = CollectorResult<IReadOnlyList<ScheduledEventConfiguration>>.Available(guild.Events.Select(MapEvent).ToArray(), at);
        var voice = CollectorResult<IReadOnlyList<VoiceStateConfiguration>>.Available(guild.VoiceChannels.SelectMany(channel => channel.ConnectedUsers.Select(user => new VoiceStateConfiguration(channel.Id, user.Id, user.IsMuted, user.IsDeafened, user.IsSuppressed, user.IsStreaming, user.IsVideoing))).ToArray(), at);

        var bans = await CollectAsync("BAN_MEMBERS", at, async () =>
            (IReadOnlyList<BanConfiguration>)(await guild.GetBansAsync().FlattenAsync()).Select(x => new BanConfiguration(x.User.Id, x.User.Username, x.Reason)).ToArray());
        var invites = await CollectAsync("VIEW_AUDIT_LOG or MANAGE_GUILD", at, async () =>
            (IReadOnlyList<InviteConfiguration>)(await guild.GetInvitesAsync()).Select(x => new InviteConfiguration(Fingerprint(x.Code), x.ChannelId, x.Inviter?.Id, x.CreatedAt, x.MaxAge, x.MaxUses, x.Uses, x.IsTemporary)).ToArray());
        var integrations = await CollectAsync("MANAGE_GUILD", at, async () =>
            (IReadOnlyList<IntegrationConfiguration>)(await guild.GetIntegrationsAsync()).Select(x => new IntegrationConfiguration(x.Id, x.Name, x.Type, x.IsEnabled, x.IsSyncing, x.RoleId, x.SyncedAt, x.IsRevoked, x.Application?.Id, x.Application?.Name)).ToArray());
        var webhooks = await CollectAsync("MANAGE_WEBHOOKS", at, async () =>
            (IReadOnlyList<WebhookConfiguration>)(await guild.GetWebhooksAsync()).Select(x => new WebhookConfiguration(x.Id, x.Name, x.ChannelId, x.Creator?.Id, x.ApplicationId, x.Type.ToString())).ToArray());
        var automod = await CollectAsync("MANAGE_GUILD", at, async () =>
            (IReadOnlyList<AutoModRuleConfiguration>)(await guild.GetAutoModRulesAsync()).Select(x => new AutoModRuleConfiguration(
                x.Id, x.Name, x.Creator.Id, x.Enabled, x.TriggerType.ToString(), x.EventType.ToString(),
                x.KeywordFilter.ToArray(), x.RegexPatterns.ToArray(), x.AllowList.ToArray(), x.MentionTotalLimit,
                x.MentionRaidProtectionEnabled, x.ExemptRoles.Select(role => role.Id).ToArray(),
                x.ExemptChannels.Select(channel => channel.Id).ToArray(), x.Actions.Select(action => action.Type.ToString()).ToArray())).ToArray());
        var audit = await CollectAsync("VIEW_AUDIT_LOG", at, async () =>
            (IReadOnlyList<AuditEventConfiguration>)(await guild.GetAuditLogsAsync(100).FlattenAsync()).Select(x => new AuditEventConfiguration(x.Id, x.CreatedAt, x.Action.ToString(), x.User?.Id, x.Reason, x.Data.GetType().Name)).ToArray());
        var onboarding = await CollectAsync("Guild membership; MANAGE_GUILD may be required by configuration", at, async () =>
        {
            var x = await guild.GetOnboardingAsync();
            return new OnboardingConfiguration(x.IsEnabled, x.Mode.ToString(), x.IsBelowRequirements, x.DefaultChannelIds.ToArray(), x.Prompts.Select(prompt => new OnboardingPromptConfiguration(prompt.Title, prompt.Type.ToString(), prompt.IsRequired, prompt.IsSingleSelect, prompt.IsInOnboarding, prompt.Options.Count)).ToArray());
        });
        var welcome = await CollectAsync("MANAGE_GUILD when disabled", at, async () =>
        {
            var x = await guild.GetWelcomeScreenAsync();
            if (x is null) return new WelcomeScreenConfiguration(null, []);
            return new WelcomeScreenConfiguration(x.Description, x.Channels.Select(channel => new WelcomeChannelConfiguration(channel.Id, channel.Description, channel.Emoji?.ToString())).ToArray());
        });

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new ServerConfigurationSnapshot(2, Guid.NewGuid(), guild.Id, guild.Name, at, guildResult, roles, channels, emojis, stickers, events, voice, bans, invites, integrations, webhooks, automod, audit, onboarding, welcome, []);
        return snapshot with { Findings = analyzer.Analyze(snapshot) };
    }

    private static GuildConfiguration MapGuild(SocketGuild guild)
    {
        var permissions = guild.CurrentUser.GuildPermissions;
        return new GuildConfiguration(
            guild.Id, guild.Name, guild.Description, guild.OwnerId, guild.CreatedAt,
            guild.VerificationLevel.ToString(), guild.MfaLevel.ToString(), guild.ExplicitContentFilter.ToString(),
            guild.DefaultMessageNotifications.ToString(), guild.NsfwLevel.ToString(), guild.PreferredLocale,
            guild.AFKChannel?.Id, (int)guild.AFKTimeout, guild.WidgetChannel?.Id, guild.IsWidgetEnabled,
            guild.SystemChannel?.Id, guild.RulesChannel?.Id, guild.PublicUpdatesChannel?.Id, guild.SafetyAlertsChannel?.Id,
            guild.SystemChannelFlags.ToString(), guild.PremiumTier.ToString(), guild.PremiumSubscriptionCount,
            guild.MaxMembers, guild.MaxVideoChannelUsers, guild.MaxStageVideoChannelUsers, guild.IsBoostProgressBarEnabled,
            guild.Features.ToString() ?? string.Empty, guild.IncidentsData?.InvitesDisabledUntil, guild.IncidentsData?.DmsDisabledUntil,
            guild.CurrentUser.Id, permissions.RawValue, SensitivePermissions(permissions));
    }

    private static RoleConfiguration MapRole(SocketRole role)
    {
        var tags = role.Tags;
        return new RoleConfiguration(
            role.Id, role.Name, role.Position, role.Permissions.RawValue, SensitivePermissions(role.Permissions),
            role.IsEveryone, role.IsManaged, role.IsHoisted, role.IsMentionable, role.Colors.PrimaryColor.RawValue,
            role.Colors.SecondaryColor?.RawValue, role.Colors.TertiaryColor?.RawValue, role.Icon, role.Emoji?.ToString(),
            tags?.BotId, tags?.IntegrationId, tags?.IsPremiumSubscriberRole ?? false, tags?.SubscriptionListingId);
    }

    private static ChannelConfiguration MapChannel(SocketGuildChannel channel)
    {
        ulong? categoryId = channel switch
        {
            SocketForumChannel x => x.CategoryId,
            SocketVoiceChannel x => x.CategoryId,
            SocketTextChannel x => x.CategoryId,
            _ => null
        };
        string? topic = channel switch { SocketForumChannel x => x.Topic, SocketTextChannel x => x.Topic, _ => null };
        bool? nsfw = channel switch { SocketForumChannel x => x.IsNsfw, SocketTextChannel x => x.IsNsfw, _ => null };
        int? slowMode = channel switch
        {
            SocketNewsChannel => null,
            SocketForumChannel x => x.ThreadCreationInterval,
            SocketTextChannel x => x.SlowModeInterval,
            _ => null
        };
        int? defaultSlowMode = channel switch
        {
            SocketNewsChannel => null,
            SocketForumChannel x => x.DefaultSlowModeInterval,
            SocketTextChannel x => x.DefaultSlowModeInterval,
            _ => null
        };
        int? archive = channel switch
        {
            SocketNewsChannel => null,
            SocketForumChannel x => (int)x.DefaultAutoArchiveDuration,
            SocketTextChannel x => (int)x.DefaultArchiveDuration,
            _ => null
        };
        int? bitrate = channel is SocketVoiceChannel voice ? voice.Bitrate : null;
        int? userLimit = channel is SocketVoiceChannel voice2 ? voice2.UserLimit : null;
        string? rtc = channel is SocketVoiceChannel voice3 ? voice3.RTCRegion : null;
        string? quality = channel is SocketVoiceChannel voice4 ? voice4.VideoQualityMode.ToString() : null;
        string? status = channel is SocketVoiceChannel voice5 ? voice5.Status : null;
        var tags = channel is SocketForumChannel forum ? forum.Tags.Select(x => new ForumTagConfiguration(x.Id, x.Name, x.IsModerated, x.Emoji?.ToString())).ToArray() : [];
        var overwrites = channel.PermissionOverwrites.Select(x => new PermissionOverwriteConfiguration(x.TargetId, x.TargetType.ToString(), x.Permissions.AllowValue, x.Permissions.DenyValue)).ToArray();
        return new ChannelConfiguration(channel.Id, channel.Name, channel.GetType().Name, channel.Position, categoryId, topic, nsfw, slowMode, defaultSlowMode, archive, bitrate, userLimit, rtc, quality, status, tags, overwrites, overwrites.Any(x => x.TargetType == PermissionTarget.User.ToString()));
    }

    private static ScheduledEventConfiguration MapEvent(SocketGuildEvent x) => new(
        x.Id, x.Name, x.Description, x.Channel?.Id, x.Creator?.Id, x.StartTime, x.EndTime,
        x.Status.ToString(), x.Type.ToString(), x.Location, x.UserCount, x.RecurrenceRule?.Frequency.ToString());

    private static IReadOnlyList<string> SensitivePermissions(GuildPermissions x)
    {
        var values = new List<string>();
        if (x.Administrator) values.Add(nameof(x.Administrator));
        if (x.ManageGuild) values.Add(nameof(x.ManageGuild));
        if (x.ManageRoles) values.Add(nameof(x.ManageRoles));
        if (x.ManageChannels) values.Add(nameof(x.ManageChannels));
        if (x.ManageWebhooks) values.Add(nameof(x.ManageWebhooks));
        if (x.BanMembers) values.Add(nameof(x.BanMembers));
        if (x.KickMembers) values.Add(nameof(x.KickMembers));
        if (x.ModerateMembers) values.Add(nameof(x.ModerateMembers));
        if (x.ManageMessages) values.Add(nameof(x.ManageMessages));
        if (x.MentionEveryone) values.Add(nameof(x.MentionEveryone));
        if (x.ManageEvents) values.Add(nameof(x.ManageEvents));
        if (x.ManageThreads) values.Add(nameof(x.ManageThreads));
        return values;
    }

    private static async Task<CollectorResult<T>> CollectAsync<T>(string permission, DateTimeOffset at, Func<Task<T>> read)
    {
        try { return CollectorResult<T>.Available(await read(), at); }
        catch (HttpException exception) when ((int)exception.HttpCode == 403) { return CollectorResult<T>.Unavailable(CollectorStatus.PermissionDenied, "Discord returned 403 Forbidden.", permission, at); }
        catch (HttpException exception) when ((int)exception.HttpCode == 429) { return CollectorResult<T>.Unavailable(CollectorStatus.RateLimited, "Discord rate-limited this collector.", permission, at); }
        catch (HttpException exception) { return CollectorResult<T>.Unavailable(CollectorStatus.TransientFailure, $"Discord returned {(int)exception.HttpCode} ({exception.HttpCode}).", permission, at); }
        catch (NotSupportedException exception) { return CollectorResult<T>.Unavailable(CollectorStatus.Unsupported, exception.Message, permission, at); }
        catch (Exception exception) { return CollectorResult<T>.Unavailable(CollectorStatus.TransientFailure, exception.Message, permission, at); }
    }

    private static string Fingerprint(string code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
