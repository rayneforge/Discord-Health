namespace DiscordHealth.Runtime.ServerConfiguration;

public enum CollectorStatus
{
    Available,
    PermissionDenied,
    IntentDisabled,
    Unsupported,
    Partial,
    RateLimited,
    TransientFailure,
    Stale,
    NotApplicable
}

public sealed record CollectorResult<T>(
    CollectorStatus Status,
    T? Data,
    string? Reason,
    string? RequiredPermission,
    DateTimeOffset CollectedAt,
    bool IsComplete = true)
{
    public bool IsAvailable => Status is CollectorStatus.Available or CollectorStatus.Partial;

    public static CollectorResult<T> Available(T data, DateTimeOffset at, bool complete = true) =>
        new(complete ? CollectorStatus.Available : CollectorStatus.Partial, data, null, null, at, complete);

    public static CollectorResult<T> Unavailable(CollectorStatus status, string reason, string? permission, DateTimeOffset at) =>
        new(status, default, reason, permission, at, false);
}

public sealed record ServerConfigurationSnapshot(
    int SchemaVersion,
    Guid SnapshotId,
    ulong GuildId,
    string Name,
    DateTimeOffset CapturedAt,
    CollectorResult<GuildConfiguration> Guild,
    CollectorResult<IReadOnlyList<RoleConfiguration>> Roles,
    CollectorResult<IReadOnlyList<ChannelConfiguration>> Channels,
    CollectorResult<IReadOnlyList<EmojiConfiguration>> Emojis,
    CollectorResult<IReadOnlyList<StickerConfiguration>> Stickers,
    CollectorResult<IReadOnlyList<ScheduledEventConfiguration>> ScheduledEvents,
    CollectorResult<IReadOnlyList<VoiceStateConfiguration>> VoiceStates,
    CollectorResult<IReadOnlyList<BanConfiguration>> Bans,
    CollectorResult<IReadOnlyList<InviteConfiguration>> Invites,
    CollectorResult<IReadOnlyList<IntegrationConfiguration>> Integrations,
    CollectorResult<IReadOnlyList<WebhookConfiguration>> Webhooks,
    CollectorResult<IReadOnlyList<AutoModRuleConfiguration>> AutoModRules,
    CollectorResult<IReadOnlyList<AuditEventConfiguration>> AuditLog,
    CollectorResult<OnboardingConfiguration> Onboarding,
    CollectorResult<WelcomeScreenConfiguration> WelcomeScreen,
    IReadOnlyList<SecurityFinding> Findings)
{
    public double CoveragePercent
    {
        get
        {
            var sections = new[]
            {
                Guild.Status, Roles.Status, Channels.Status, Emojis.Status, Stickers.Status,
                ScheduledEvents.Status, VoiceStates.Status, Bans.Status, Invites.Status,
                Integrations.Status, Webhooks.Status, AutoModRules.Status, AuditLog.Status,
                Onboarding.Status, WelcomeScreen.Status
            };
            return Math.Round(sections.Count(status => status is CollectorStatus.Available or CollectorStatus.NotApplicable) * 100d / sections.Length, 1);
        }
    }
}

public sealed record GuildConfiguration(
    ulong Id,
    string Name,
    string? Description,
    ulong OwnerId,
    DateTimeOffset CreatedAt,
    string VerificationLevel,
    string MfaLevel,
    string ExplicitContentFilter,
    string DefaultNotifications,
    string NsfwLevel,
    string PreferredLocale,
    ulong? AfkChannelId,
    int AfkTimeoutSeconds,
    ulong? WidgetChannelId,
    bool WidgetEnabled,
    ulong? SystemChannelId,
    ulong? RulesChannelId,
    ulong? PublicUpdatesChannelId,
    ulong? SafetyAlertsChannelId,
    string SystemChannelFlags,
    string PremiumTier,
    int PremiumSubscriptions,
    int? MaxMembers,
    int? MaxVideoUsers,
    int? MaxStageVideoUsers,
    bool BoostProgressBarEnabled,
    string Features,
    DateTimeOffset? InvitesDisabledUntil,
    DateTimeOffset? DmsDisabledUntil,
    ulong QuorumBotUserId,
    ulong QuorumPermissions,
    IReadOnlyList<string> QuorumSensitivePermissions);

public sealed record RoleConfiguration(
    ulong Id,
    string Name,
    int Position,
    ulong Permissions,
    IReadOnlyList<string> SensitivePermissions,
    bool IsEveryone,
    bool IsManaged,
    bool IsHoisted,
    bool IsMentionable,
    uint PrimaryColor,
    uint? SecondaryColor,
    uint? TertiaryColor,
    string? IconUrl,
    string? UnicodeEmoji,
    ulong? ManagedByBotId,
    ulong? ManagedByIntegrationId,
    bool IsPremiumSubscriberRole,
    ulong? SubscriptionListingId);

public sealed record ChannelConfiguration(
    ulong Id,
    string Name,
    string Type,
    int Position,
    ulong? CategoryId,
    string? Topic,
    bool? IsNsfw,
    int? SlowModeSeconds,
    int? DefaultThreadSlowModeSeconds,
    int? DefaultAutoArchiveMinutes,
    int? Bitrate,
    int? UserLimit,
    string? RtcRegion,
    string? VideoQualityMode,
    string? Status,
    IReadOnlyList<ForumTagConfiguration> ForumTags,
    IReadOnlyList<PermissionOverwriteConfiguration> PermissionOverwrites,
    bool HasDirectMemberOverwrites);

public sealed record ForumTagConfiguration(ulong Id, string Name, bool IsModerated, string? Emoji);
public sealed record PermissionOverwriteConfiguration(ulong TargetId, string TargetType, ulong Allowed, ulong Denied);

public sealed record EmojiConfiguration(ulong Id, string Name, bool Animated, bool Available, IReadOnlyList<ulong> RestrictedRoleIds);
public sealed record StickerConfiguration(ulong Id, string Name, string? Description, string Tags, bool Available);
public sealed record ScheduledEventConfiguration(ulong Id, string Name, string? Description, ulong? ChannelId, ulong? CreatorId, DateTimeOffset Start, DateTimeOffset? End, string Status, string Type, string? Location, int? UserCount, string? Recurrence);
public sealed record VoiceStateConfiguration(ulong ChannelId, ulong UserId, bool Muted, bool Deafened, bool Suppressed, bool Streaming, bool Videoing);
public sealed record BanConfiguration(ulong UserId, string Username, string? Reason);
public sealed record InviteConfiguration(string Fingerprint, ulong? ChannelId, ulong? InviterId, DateTimeOffset? CreatedAt, int? MaxAgeSeconds, int? MaxUses, int? Uses, bool Temporary);
public sealed record IntegrationConfiguration(ulong Id, string Name, string Type, bool Enabled, bool? Syncing, ulong? RoleId, DateTimeOffset? SyncedAt, bool? Revoked, ulong? ApplicationId, string? ApplicationName);
public sealed record WebhookConfiguration(ulong Id, string? Name, ulong? ChannelId, ulong? CreatorId, ulong? ApplicationId, string Type);
public sealed record AutoModRuleConfiguration(ulong Id, string Name, ulong CreatorId, bool Enabled, string TriggerType, string EventType, IReadOnlyList<string> Keywords, IReadOnlyList<string> RegexPatterns, IReadOnlyList<string> AllowList, int? MentionLimit, bool? MentionRaidProtection, IReadOnlyList<ulong> ExemptRoleIds, IReadOnlyList<ulong> ExemptChannelIds, IReadOnlyList<string> Actions);
public sealed record AuditEventConfiguration(ulong Id, DateTimeOffset CreatedAt, string Action, ulong? ActorId, string? Reason, string DataType);
public sealed record OnboardingConfiguration(bool Enabled, string Mode, bool BelowRequirements, IReadOnlyList<ulong> DefaultChannelIds, IReadOnlyList<OnboardingPromptConfiguration> Prompts);
public sealed record OnboardingPromptConfiguration(string Title, string Type, bool Required, bool SingleSelect, bool InOnboarding, int OptionCount);
public sealed record WelcomeScreenConfiguration(string? Description, IReadOnlyList<WelcomeChannelConfiguration> Channels);
public sealed record WelcomeChannelConfiguration(ulong ChannelId, string Description, string? Emoji);

public enum FindingSeverity { Notice, Low, Medium, High, Critical }
public enum FindingStatus { Pass, Fail, Review, Unknown, NotApplicable }
public sealed record SecurityFinding(string Id, string Category, FindingSeverity Severity, FindingStatus Status, string Title, string Observation, string Risk, string Recommendation, string EvidenceSource, string EvidenceField, string EvidenceValue, double Confidence, bool ReadOnly = true);
