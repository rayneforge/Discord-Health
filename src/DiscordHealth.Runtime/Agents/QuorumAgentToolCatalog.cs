using System.Diagnostics;
using System.Text.Json;
using Discord;
using DiscordHealth.Runtime.Changes;
using DiscordHealth.Runtime.DiscordAdapter;
using DiscordHealth.Runtime.ServerConfiguration;
using DiscordHealth.Runtime.Tools;
using Microsoft.Extensions.AI;

namespace DiscordHealth.Runtime.Agents;

public interface IQuorumAgentToolCatalog
{
    IReadOnlyList<AITool> GetTools(ulong guildId, ulong requesterId, ulong approvalChannelId, Guid? approvalBatchId = null);
}

internal sealed class QuorumAgentToolCatalog(
    IQuorumReadTools reads,
    IPermissionReadTools permissions,
    IApprovalPublisher approvals,
    IQuorumAuthorizationService authorization,
    ILogger<QuorumAgentToolCatalog> logger) : IQuorumAgentToolCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<AITool> GetTools(ulong guildId, ulong requesterId, ulong approvalChannelId, Guid? approvalBatchId = null) =>
    [
        AIFunctionFactory.Create(
            async () => await ExecuteAsync("scan_server_configuration", guildId, requesterId, async () =>
            {
                await authorization.DemandReadAsync(guildId, requesterId, QuorumReadCapability.FullConfiguration);
                var review = await reads.ScanAsync(guildId);
                return new
                {
                    review.Snapshot.Name,
                    review.Snapshot.CapturedAt,
                    review.Snapshot.CoveragePercent,
                    FindingCount = review.Snapshot.Findings.Count,
                    ChangesFromPrevious = review.ChangesFromPrevious
                };
            }),
            "scan_server_configuration",
            "Capture a fresh read-only server snapshot, persist it, and compare it with the previous snapshot. Use when current state matters."),

        AIFunctionFactory.Create(
            async (string section) => await ExecuteAsync("inspect_server_configuration", guildId, requesterId, async () =>
            {
                await authorization.DemandReadAsync(guildId, requesterId, ReadCapabilityForSection(section));
                var snapshot = await reads.GetLatestAsync(guildId);
                object result = section.Trim().ToLowerInvariant() switch
                {
                    "overview" or "guild" => snapshot.Guild,
                    "roles" => snapshot.Roles,
                    "channels" or "permissions" or "overwrites" => snapshot.Channels,
                    "emojis" => snapshot.Emojis,
                    "stickers" => snapshot.Stickers,
                    "events" or "scheduled_events" => snapshot.ScheduledEvents,
                    "voice" or "voice_states" => snapshot.VoiceStates,
                    "bans" => snapshot.Bans,
                    "invites" => snapshot.Invites,
                    "integrations" => snapshot.Integrations,
                    "webhooks" => snapshot.Webhooks,
                    "automod" or "automod_rules" => snapshot.AutoModRules,
                    "audit" or "audit_log" => snapshot.AuditLog,
                    "onboarding" => snapshot.Onboarding,
                    "welcome" or "welcome_screen" => snapshot.WelcomeScreen,
                    "coverage" => Coverage(snapshot),
                    _ => throw new ArgumentException($"Unknown section '{section}'. Valid sections: {string.Join(", ", SectionNames)}.")
                };
                return result;
            }),
            "inspect_server_configuration",
            "Read one segment of the latest snapshot. section must be one of: overview, roles, channels, emojis, stickers, events, voice, bans, invites, integrations, webhooks, automod, audit, onboarding, welcome, coverage. Collector status and permission gaps are included."),

        AIFunctionFactory.Create(
            async (string resourceType, string query) => await ExecuteAsync("find_server_resources", guildId, requesterId, async () =>
            {
                await authorization.DemandResourceLookupAsync(guildId, requesterId, resourceType);
                var snapshot = await reads.GetLatestAsync(guildId);
                var normalizedType = resourceType.Trim().ToLowerInvariant();
                var normalizedQuery = query.Trim();
                if (normalizedQuery.Length == 0) throw new ArgumentException("query cannot be empty.", nameof(query));
                return normalizedType switch
                {
                    "role" or "roles" => snapshot.Roles.Data?
                        .Where(x => x.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                        .Take(25)
                        .Select(x => new { Id = x.Id.ToString(), x.Name, Type = "role", x.Position, x.IsManaged })
                        .ToArray() ?? [],
                    "channel" or "channels" => snapshot.Channels.Data?
                        .Where(x => x.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                        .Take(25)
                        .Select(x => new { Id = x.Id.ToString(), x.Name, x.Type, x.CategoryId })
                        .ToArray() ?? [],
                    "category" or "categories" => snapshot.Channels.Data?
                        .Where(x => x.Type.Contains("Category", StringComparison.OrdinalIgnoreCase) &&
                                    x.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                        .Take(25)
                        .Select(x => new { Id = x.Id.ToString(), x.Name, x.Type })
                        .ToArray() ?? [],
                    _ => throw new ArgumentException("resourceType must be role, channel, or category.", nameof(resourceType))
                };
            }),
            "find_server_resources",
            "Find server-scoped roles, channels, or categories by a specific name fragment and return their exact Discord IDs. Use before tools that target an existing resource; never guess an ID."),

        AIFunctionFactory.Create(
            async (string? severity, string? status) => await ExecuteAsync("list_security_findings", guildId, requesterId, async () =>
            {
                await authorization.DemandReadAsync(guildId, requesterId, QuorumReadCapability.Findings);
                var findings = await reads.ListFindingsAsync(guildId);
                return findings
                    .Where(x => string.IsNullOrWhiteSpace(severity) || x.Severity.ToString().Equals(severity, StringComparison.OrdinalIgnoreCase))
                    .Where(x => string.IsNullOrWhiteSpace(status) || x.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }),
            "list_security_findings",
            "List current deterministic Quorum findings. Optional severity and status filters use values such as High and Fail."),

        AIFunctionFactory.Create(
            async (string roleId, string channelId, string permission) => await ExecuteAsync("explain_role_permission", guildId, requesterId, async () =>
            {
                await authorization.DemandReadAsync(guildId, requesterId, QuorumReadCapability.PermissionAnalysis);
                return await permissions.ExplainRolePermissionAsync(
                    guildId,
                    await ResolveRoleIdAsync(guildId, requesterId, roleId),
                    await ResolveChannelIdAsync(guildId, requesterId, channelId),
                    permission);
            }),
            "explain_role_permission",
            "Explain a role's effective permission in a channel, including overwrite resolution. Discord IDs must be passed as strings."),

        AIFunctionFactory.Create(
            async (string channelId, int seconds) => await ExecuteAsync("propose_channel_slowmode", guildId, requesterId, async () =>
            {
                return await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.ChangeChannelSlowMode,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string> { ["seconds"] = seconds.ToString() }));
            }),
            "propose_channel_slowmode",
            "WRITE-SHAPED TOOL. Create an approval request to change a text channel's slowmode. It never directly changes Discord. Use only when the user clearly asks for this exact change. channelId must be a string."),

        AIFunctionFactory.Create(
            async (string name, string? categoryId, string? categoryName, string? topic, bool? nsfw) => await ExecuteAsync("propose_create_text_channel", guildId, requesterId, async () =>
            {
                var arguments = new Dictionary<string, string> { ["name"] = name };
                Add(arguments, "category_id", categoryId);
                Add(arguments, "category_name", categoryName);
                if (!string.IsNullOrWhiteSpace(categoryId) && !string.IsNullOrWhiteSpace(categoryName))
                    throw new ArgumentException("Specify categoryId or categoryName, not both.");
                Add(arguments, "topic", topic);
                if (nsfw.HasValue) arguments["nsfw"] = nsfw.Value.ToString();
                return await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(ChangeActionType.CreateTextChannel, 0, arguments));
            }),
            "propose_create_text_channel",
            "Create a same-chat approval request for a new Discord text channel. No channel is created before approval. Use categoryId for an existing category or categoryName for an exact category name. categoryName may reference a category proposed in the same approval batch; category creation executes before channel creation."),

        AIFunctionFactory.Create(
            async (string name) => await ExecuteAsync("propose_create_category", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.CreateCategory, 0, new Dictionary<string, string> { ["name"] = name }))),
            "propose_create_category",
            "Create a same-chat approval request for a new Discord category."),

        AIFunctionFactory.Create(
            async (string channelId, string name) => await ExecuteAsync("propose_rename_channel", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.RenameChannel,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string> { ["name"] = name }))),
            "propose_rename_channel",
            "Create a same-chat approval request to rename an existing channel. channelId must be a Discord ID string."),

        AIFunctionFactory.Create(
            async (string channelId, string topic) => await ExecuteAsync("propose_change_channel_topic", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.ChangeChannelTopic,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string> { ["topic"] = topic }))),
            "propose_change_channel_topic",
            "Create a same-chat approval request to replace a text channel topic."),

        AIFunctionFactory.Create(
            async (string channelId) => await ExecuteAsync("propose_delete_channel", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.DeleteChannel,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string>()))),
            "propose_delete_channel",
            "CRITICAL WRITE-SHAPED TOOL. Create a same-chat approval request to permanently delete a channel. Never use for a rename, lock, or topic change."),

        AIFunctionFactory.Create(
            async (string name, string? permissions) => await ExecuteAsync("propose_create_role", guildId, requesterId, async () =>
            {
                var arguments = new Dictionary<string, string> { ["name"] = name };
                if (!string.IsNullOrWhiteSpace(permissions)) arguments["permissions"] = NormalizeGuildPermissions(permissions);
                return await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(ChangeActionType.CreateRole, 0, arguments));
            }),
            "propose_create_role",
            "Create a same-chat approval request for a Discord role. permissions accepts a raw bitset or comma-separated Discord permission names such as ViewChannel, SendMessages, or Administrator."),

        AIFunctionFactory.Create(
            async (string roleId, string permissions) => await ExecuteAsync("propose_change_role_permissions", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.ChangeRolePermissions,
                    await ResolveRoleIdAsync(guildId, requesterId, roleId),
                    new Dictionary<string, string> { ["permissions"] = NormalizeGuildPermissions(permissions) }))),
            "propose_change_role_permissions",
            "HIGH-RISK TOOL. Replace a role's entire permission set after approval. roleId accepts an exact role ID or exact role name; permissions accepts a raw bitset or comma-separated Discord permission names."),

        AIFunctionFactory.Create(
            async (string roleId) => await ExecuteAsync("propose_delete_role", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.DeleteRole,
                    await ResolveRoleIdAsync(guildId, requesterId, roleId),
                    new Dictionary<string, string>()))),
            "propose_delete_role",
            "CRITICAL WRITE-SHAPED TOOL. Create a same-chat approval request to permanently delete a role. roleId accepts an exact ID or exact role name."),

        AIFunctionFactory.Create(
            async (string userId, string roleId) => await ExecuteAsync("propose_assign_role", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.AssignRole,
                    ParseDiscordId(userId, nameof(userId)),
                    new Dictionary<string, string> { ["role_id"] = (await ResolveRoleIdAsync(guildId, requesterId, roleId)).ToString() }))),
            "propose_assign_role",
            "Create a same-chat approval request to assign a role to a member. userId must be an ID; roleId accepts an exact ID or exact role name."),

        AIFunctionFactory.Create(
            async (string userId, string roleId) => await ExecuteAsync("propose_remove_role", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.RemoveRole,
                    ParseDiscordId(userId, nameof(userId)),
                    new Dictionary<string, string> { ["role_id"] = (await ResolveRoleIdAsync(guildId, requesterId, roleId)).ToString() }))),
            "propose_remove_role",
            "Create a same-chat approval request to remove a role from a member. userId must be an ID; roleId accepts an exact ID or exact role name."),

        AIFunctionFactory.Create(
            async (string userId, int minutes, string? reason) => await ExecuteAsync("propose_timeout_member", guildId, requesterId, async () =>
            {
                if (minutes is < 1 or > 40320) throw new ArgumentOutOfRangeException(nameof(minutes), "Timeout must be between 1 minute and 28 days.");
                var arguments = new Dictionary<string, string> { ["minutes"] = minutes.ToString() };
                Add(arguments, "reason", reason);
                return await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(ChangeActionType.TimeoutMember, ParseDiscordId(userId, nameof(userId)), arguments));
            }),
            "propose_timeout_member",
            "Create a same-chat approval request to timeout a member for 1 to 40320 minutes."),

        AIFunctionFactory.Create(
            async (string userId, string? reason) => await ExecuteAsync("propose_kick_member", guildId, requesterId, async () =>
            {
                var arguments = new Dictionary<string, string>();
                Add(arguments, "reason", reason);
                return await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(ChangeActionType.KickMember, ParseDiscordId(userId, nameof(userId)), arguments));
            }),
            "propose_kick_member",
            "HIGH-RISK TOOL. Create a same-chat approval request to remove a member from the server."),

        AIFunctionFactory.Create(
            async (string userId, string? reason, int? deleteMessageDays) => await ExecuteAsync("propose_ban_member", guildId, requesterId, async () =>
            {
                var days = deleteMessageDays ?? 0;
                if (days is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(deleteMessageDays), "Delete-message days must be from 0 to 7.");
                var arguments = new Dictionary<string, string> { ["delete_message_days"] = days.ToString() };
                Add(arguments, "reason", reason);
                return await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(ChangeActionType.BanMember, ParseDiscordId(userId, nameof(userId)), arguments));
            }),
            "propose_ban_member",
            "HIGH-RISK TOOL. Create a same-chat approval request to ban a Discord user."),

        AIFunctionFactory.Create(
            async (string userId) => await ExecuteAsync("propose_unban_member", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.UnbanMember,
                    ParseDiscordId(userId, nameof(userId)),
                    new Dictionary<string, string>()))),
            "propose_unban_member",
            "Create a same-chat approval request to remove a Discord ban."),

        AIFunctionFactory.Create(
            async (string webhookId) => await ExecuteAsync("propose_delete_webhook", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.DeleteWebhook,
                    ParseDiscordId(webhookId, nameof(webhookId)),
                    new Dictionary<string, string>()))),
            "propose_delete_webhook",
            "HIGH-RISK TOOL. Create a same-chat approval request to permanently delete a webhook."),

        AIFunctionFactory.Create(
            async (string inviteCode) => await ExecuteAsync("propose_revoke_invite", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.RevokeInvite,
                    0,
                    new Dictionary<string, string> { ["code"] = inviteCode }))),
            "propose_revoke_invite",
            "Create a same-chat approval request to revoke one Discord invite code. The code is redacted from the approval card."),

        AIFunctionFactory.Create(
            async (string name, string start, string end, string location, string? description) => await ExecuteAsync("propose_create_scheduled_event", guildId, requesterId, async () =>
            {
                var arguments = new Dictionary<string, string>
                {
                    ["name"] = name,
                    ["start"] = start,
                    ["end"] = end,
                    ["location"] = location
                };
                Add(arguments, "description", description);
                return await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(ChangeActionType.CreateScheduledEvent, 0, arguments));
            }),
            "propose_create_scheduled_event",
            "Create a same-chat approval request for an external Discord scheduled event. start and end must be ISO-8601 timestamps and location is required."),

        AIFunctionFactory.Create(
            async (string channelId, string roleId, string allow, string deny) => await ExecuteAsync("propose_set_role_channel_overwrite", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.SetRoleChannelOverwrite,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string>
                    {
                        ["role_id"] = (await ResolveRoleIdAsync(guildId, requesterId, roleId)).ToString(),
                        ["allow"] = allow,
                        ["deny"] = deny
                    }))),
            "propose_set_role_channel_overwrite",
            "HIGH-RISK TOOL. Create a same-chat approval request to replace one role's channel permission overwrite. allow and deny are raw Discord bitset strings."),

        AIFunctionFactory.Create(
            async (string channelId, string roleId) => await ExecuteAsync("propose_remove_role_channel_overwrite", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.RemoveRoleChannelOverwrite,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string> { ["role_id"] = (await ResolveRoleIdAsync(guildId, requesterId, roleId)).ToString() }))),
            "propose_remove_role_channel_overwrite",
            "HIGH-RISK TOOL. Create a same-chat approval request to remove one role-specific channel overwrite."),

        AIFunctionFactory.Create(
            async (string threadId, bool locked) => await ExecuteAsync("propose_set_thread_locked", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.SetThreadLocked,
                    ParseDiscordId(threadId, nameof(threadId)),
                    new Dictionary<string, string> { ["locked"] = locked.ToString() }))),
            "propose_set_thread_locked",
            "Create a same-chat approval request to lock or unlock a Discord thread."),

        AIFunctionFactory.Create(
            async (string threadId, bool archived) => await ExecuteAsync("propose_set_thread_archived", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.SetThreadArchived,
                    ParseDiscordId(threadId, nameof(threadId)),
                    new Dictionary<string, string> { ["archived"] = archived.ToString() }))),
            "propose_set_thread_archived",
            "Create a same-chat approval request to archive or unarchive a Discord thread."),

        AIFunctionFactory.Create(
            async (string name, string[] keywords, string? customMessage, bool? enabled) => await ExecuteAsync("propose_create_automod_keyword_rule", guildId, requesterId, async () =>
            {
                if (keywords.Length == 0) throw new ArgumentException("At least one keyword is required.", nameof(keywords));
                var arguments = new Dictionary<string, string>
                {
                    ["name"] = name,
                    ["keywords"] = string.Join('|', keywords),
                    ["enabled"] = (enabled ?? true).ToString()
                };
                Add(arguments, "custom_message", customMessage);
                return await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(ChangeActionType.CreateAutoModKeywordRule, 0, arguments));
            }),
            "propose_create_automod_keyword_rule",
            "HIGH-RISK TOOL. Create a same-chat approval request for an AutoMod keyword rule that blocks matching messages."),

        AIFunctionFactory.Create(
            async (string ruleId, bool enabled) => await ExecuteAsync("propose_set_automod_rule_enabled", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.SetAutoModRuleEnabled,
                    ParseDiscordId(ruleId, nameof(ruleId)),
                    new Dictionary<string, string> { ["enabled"] = enabled.ToString() }))),
            "propose_set_automod_rule_enabled",
            "HIGH-RISK TOOL. Create a same-chat approval request to enable or disable an AutoMod rule."),

        AIFunctionFactory.Create(
            async (string ruleId) => await ExecuteAsync("propose_delete_automod_rule", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.DeleteAutoModRule,
                    ParseDiscordId(ruleId, nameof(ruleId)),
                    new Dictionary<string, string>()))),
            "propose_delete_automod_rule",
            "CRITICAL TOOL. Create a same-chat approval request to permanently delete an AutoMod rule."),

        AIFunctionFactory.Create(
            async (bool enabled, string description) => await ExecuteAsync("propose_update_welcome_screen", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(
                    ChangeActionType.UpdateWelcomeScreen,
                    0,
                    new Dictionary<string, string> { ["enabled"] = enabled.ToString(), ["description"] = description }))),
            "propose_update_welcome_screen",
            "Create a same-chat approval request to enable/disable the server welcome screen and replace its description while preserving its channel list."),

        AIFunctionFactory.Create(
            async (bool? enabled, string? mode) => await ExecuteAsync("propose_update_onboarding", guildId, requesterId, async () =>
            {
                var arguments = new Dictionary<string, string>();
                if (enabled.HasValue) arguments["enabled"] = enabled.Value.ToString();
                Add(arguments, "mode", mode);
                if (arguments.Count == 0) throw new ArgumentException("Specify enabled, mode, or both.");
                return await ProposeAsync(guildId, requesterId, approvalChannelId, approvalBatchId, new(ChangeActionType.UpdateOnboarding, 0, arguments));
            }),
            "propose_update_onboarding",
            "Create a same-chat approval request to change onboarding enabled state and/or mode (Default or Advanced), preserving existing prompts and default channels."),

        AIFunctionFactory.Create(
            async () => await ExecuteAsync("describe_quorum_capabilities", guildId, requesterId, () => Task.FromResult<object?>(new
            {
                ReadTools = new[] { "fresh scan and drift", "all snapshot sections", "security findings", "effective role permission explanation" },
                WriteTools = new[]
                {
                    "create text channel or category", "rename/update/delete channel", "change channel slowmode",
                    "create/delete role", "change role permissions", "assign/remove member role",
                    "timeout/kick/ban/unban member", "delete webhook", "revoke invite", "create scheduled event",
                    "role channel overwrites", "thread lock/archive", "AutoMod keyword rules", "welcome screen", "onboarding"
                },
                WritePolicy = "Write-shaped tools create durable approval requests in the current chat only. Execution happens only after an administrator clicks Approve.",
                KnownGap = "Advanced AutoMod trigger/action variants, onboarding prompt composition, voice/forum-specific settings, and scheduled-event update/delete are not yet implemented."
            })),
            "describe_quorum_capabilities",
            "Describe Quorum's currently implemented tools and known gaps. Use when asked what Quorum can do.")
    ];

    private static readonly string[] SectionNames =
    [
        "overview", "roles", "channels", "emojis", "stickers", "events", "voice", "bans",
        "invites", "integrations", "webhooks", "automod", "audit", "onboarding", "welcome", "coverage"
    ];

    private static object Coverage(ServerConfigurationSnapshot snapshot) => new
    {
        snapshot.CoveragePercent,
        Collectors = new
        {
            Guild = Status(snapshot.Guild),
            Roles = Status(snapshot.Roles),
            Channels = Status(snapshot.Channels),
            Emojis = Status(snapshot.Emojis),
            Stickers = Status(snapshot.Stickers),
            ScheduledEvents = Status(snapshot.ScheduledEvents),
            VoiceStates = Status(snapshot.VoiceStates),
            Bans = Status(snapshot.Bans),
            Invites = Status(snapshot.Invites),
            Integrations = Status(snapshot.Integrations),
            Webhooks = Status(snapshot.Webhooks),
            AutoModRules = Status(snapshot.AutoModRules),
            AuditLog = Status(snapshot.AuditLog),
            Onboarding = Status(snapshot.Onboarding),
            WelcomeScreen = Status(snapshot.WelcomeScreen)
        }
    };

    private static object Status<T>(CollectorResult<T> result) => new
    {
        result.Status,
        result.Reason,
        result.RequiredPermission,
        result.IsComplete,
        result.CollectedAt
    };

    private static QuorumReadCapability ReadCapabilityForSection(string section) => section.Trim().ToLowerInvariant() switch
    {
        "overview" or "guild" => QuorumReadCapability.Overview,
        "roles" => QuorumReadCapability.Roles,
        "channels" or "permissions" or "overwrites" => QuorumReadCapability.Channels,
        "emojis" or "stickers" => QuorumReadCapability.Expressions,
        "events" or "scheduled_events" => QuorumReadCapability.Events,
        "voice" or "voice_states" => QuorumReadCapability.VoiceStates,
        "bans" => QuorumReadCapability.Bans,
        "invites" => QuorumReadCapability.Invites,
        "integrations" => QuorumReadCapability.Integrations,
        "webhooks" => QuorumReadCapability.Webhooks,
        "automod" or "automod_rules" => QuorumReadCapability.AutoMod,
        "audit" or "audit_log" => QuorumReadCapability.AuditLog,
        "onboarding" => QuorumReadCapability.Onboarding,
        "welcome" or "welcome_screen" => QuorumReadCapability.WelcomeScreen,
        "coverage" => QuorumReadCapability.Coverage,
        _ => throw new ArgumentException($"Unknown section '{section}'. Valid sections: {string.Join(", ", SectionNames)}.")
    };

    private static ulong ParseDiscordId(string value, string name) =>
        ulong.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"{name} must be a Discord snowflake ID encoded as a string.");

    private async Task<ulong> ResolveRoleIdAsync(ulong guildId, ulong requesterId, string selector)
    {
        await authorization.DemandResourceLookupAsync(guildId, requesterId, "role");
        var snapshot = await reads.GetLatestAsync(guildId);
        var roles = snapshot.Roles.Data ?? throw new InvalidOperationException("Role configuration is unavailable.");
        if (ulong.TryParse(selector, out var id))
            return roles.Any(x => x.Id == id)
                ? id
                : throw new InvalidOperationException("The selected role does not exist in this server snapshot.");

        var matches = roles.Where(x => x.Name.Equals(selector.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].Id,
            0 => throw new InvalidOperationException($"No role named '{selector}' exists in this server snapshot. Use find_server_resources to inspect available roles."),
            _ => throw new ArgumentException($"More than one role is named '{selector}'. Use find_server_resources and pass the exact ID.", nameof(selector))
        };
    }

    private async Task<ulong> ResolveChannelIdAsync(ulong guildId, ulong requesterId, string selector)
    {
        await authorization.DemandResourceLookupAsync(guildId, requesterId, "channel");
        var snapshot = await reads.GetLatestAsync(guildId);
        var channels = snapshot.Channels.Data ?? throw new InvalidOperationException("Channel configuration is unavailable.");
        if (ulong.TryParse(selector, out var id))
            return channels.Any(x => x.Id == id)
                ? id
                : throw new InvalidOperationException("The selected channel does not exist in this server snapshot.");

        var matches = channels.Where(x => x.Name.Equals(selector.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].Id,
            0 => throw new InvalidOperationException($"No channel named '{selector}' exists in this server snapshot. Use find_server_resources to inspect available channels."),
            _ => throw new ArgumentException($"More than one channel is named '{selector}'. Use find_server_resources and pass the exact ID.", nameof(selector))
        };
    }

    private static string NormalizeGuildPermissions(string value)
    {
        if (ulong.TryParse(value, out var raw)) return raw.ToString(System.Globalization.CultureInfo.InvariantCulture);

        ulong combined = 0;
        foreach (var token in value.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizePermissionName(token);
            if (normalized == "admin") normalized = "administrator";
            var match = Enum.GetValues<GuildPermission>()
                .Cast<GuildPermission?>()
                .SingleOrDefault(permission => NormalizePermissionName(permission!.Value.ToString()) == normalized);
            if (!match.HasValue)
                throw new ArgumentException($"Unknown Discord permission '{token}'. Use canonical names such as ViewChannel, SendMessages, ManageRoles, or Administrator.", nameof(value));
            combined |= Convert.ToUInt64(match.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return combined.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizePermissionName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task<object> ProposeAsync(
        ulong guildId,
        ulong requesterId,
        ulong approvalChannelId,
        Guid? approvalBatchId,
        ChangeRequest request)
    {
        var proposal = await approvals.ProposeAsync(guildId, requesterId, approvalChannelId, request, approvalBatchId);
        return new
        {
            Success = true,
            proposal.DisplayId,
            proposal.Status,
            proposal.Risk,
            proposal.Change,
            ApprovalPending = true,
            ApprovalChannelId = approvalChannelId.ToString(),
            ApprovalBatchId = approvalBatchId?.ToString("N"),
            Message = "No Discord change has been executed. An administrator must approve the grouped request in this chat."
        };
    }

    private static void Add(IDictionary<string, string> arguments, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) arguments[key] = value;
    }

    private async Task<string> ExecuteAsync(
        string toolName,
        ulong guildId,
        ulong requesterId,
        Func<Task<object?>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Quorum tool {ToolName} started for guild {GuildId} requester {RequesterId}.",
            toolName,
            guildId,
            requesterId);
        try
        {
            var result = await action();
            var json = JsonSerializer.Serialize(result, JsonOptions);
            logger.LogInformation(
                "Quorum tool {ToolName} completed successfully in {ElapsedMs} ms with {ResultLength} result characters.",
                toolName,
                stopwatch.ElapsedMilliseconds,
                json.Length);
            return json;
        }
        catch (Exception exception)
        {
            var category = exception switch
            {
                UnauthorizedAccessException => "permission",
                ArgumentException => "input",
                InvalidOperationException => "configuration_or_state",
                _ => "technical"
            };
            logger.LogWarning(
                "Quorum tool {ToolName} failed in {ElapsedMs} ms for guild {GuildId}; category {ErrorCategory}; {ExceptionType}: {ErrorMessage}.",
                toolName,
                stopwatch.ElapsedMilliseconds,
                guildId,
                category,
                exception.GetType().Name,
                exception.Message);
            logger.LogDebug(exception, "Full exception for failed Quorum tool {ToolName}.", toolName);
            return JsonSerializer.Serialize(new
            {
                Success = false,
                Tool = toolName,
                ErrorCategory = category,
                Error = exception.Message,
                NoDiscordChangeExecuted = toolName.StartsWith("propose_", StringComparison.Ordinal)
            }, JsonOptions);
        }
    }
}
