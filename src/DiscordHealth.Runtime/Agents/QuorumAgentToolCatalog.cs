using System.Diagnostics;
using System.Text.Json;
using DiscordHealth.Runtime.Changes;
using DiscordHealth.Runtime.ServerConfiguration;
using DiscordHealth.Runtime.Tools;
using Microsoft.Extensions.AI;

namespace DiscordHealth.Runtime.Agents;

public interface IQuorumAgentToolCatalog
{
    IReadOnlyList<AITool> GetTools(ulong guildId, ulong requesterId, ulong approvalChannelId);
}

internal sealed class QuorumAgentToolCatalog(
    IQuorumReadTools reads,
    IPermissionReadTools permissions,
    IApprovalPublisher approvals,
    ILogger<QuorumAgentToolCatalog> logger) : IQuorumAgentToolCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<AITool> GetTools(ulong guildId, ulong requesterId, ulong approvalChannelId) =>
    [
        AIFunctionFactory.Create(
            async () => await ExecuteAsync("scan_server_configuration", guildId, requesterId, async () =>
            {
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
            async (string? severity, string? status) => await ExecuteAsync("list_security_findings", guildId, requesterId, async () =>
            {
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
                return await permissions.ExplainRolePermissionAsync(
                    guildId,
                    ParseDiscordId(roleId, nameof(roleId)),
                    ParseDiscordId(channelId, nameof(channelId)),
                    permission);
            }),
            "explain_role_permission",
            "Explain a role's effective permission in a channel, including overwrite resolution. Discord IDs must be passed as strings."),

        AIFunctionFactory.Create(
            async (string channelId, int seconds) => await ExecuteAsync("propose_channel_slowmode", guildId, requesterId, async () =>
            {
                return await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.ChangeChannelSlowMode,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string> { ["seconds"] = seconds.ToString() }));
            }),
            "propose_channel_slowmode",
            "WRITE-SHAPED TOOL. Create an approval request to change a text channel's slowmode. It never directly changes Discord. Use only when the user clearly asks for this exact change. channelId must be a string."),

        AIFunctionFactory.Create(
            async (string name, string? categoryId, string? topic, bool? nsfw) => await ExecuteAsync("propose_create_text_channel", guildId, requesterId, async () =>
            {
                var arguments = new Dictionary<string, string> { ["name"] = name };
                Add(arguments, "category_id", categoryId);
                Add(arguments, "topic", topic);
                if (nsfw.HasValue) arguments["nsfw"] = nsfw.Value.ToString();
                return await ProposeAsync(guildId, requesterId, approvalChannelId, new(ChangeActionType.CreateTextChannel, 0, arguments));
            }),
            "propose_create_text_channel",
            "Create a same-chat approval request for a new Discord text channel. No channel is created before an administrator clicks Approve. categoryId is an optional Discord ID string."),

        AIFunctionFactory.Create(
            async (string name) => await ExecuteAsync("propose_create_category", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.CreateCategory, 0, new Dictionary<string, string> { ["name"] = name }))),
            "propose_create_category",
            "Create a same-chat approval request for a new Discord category."),

        AIFunctionFactory.Create(
            async (string channelId, string name) => await ExecuteAsync("propose_rename_channel", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.RenameChannel,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string> { ["name"] = name }))),
            "propose_rename_channel",
            "Create a same-chat approval request to rename an existing channel. channelId must be a Discord ID string."),

        AIFunctionFactory.Create(
            async (string channelId, string topic) => await ExecuteAsync("propose_change_channel_topic", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.ChangeChannelTopic,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string> { ["topic"] = topic }))),
            "propose_change_channel_topic",
            "Create a same-chat approval request to replace a text channel topic."),

        AIFunctionFactory.Create(
            async (string channelId) => await ExecuteAsync("propose_delete_channel", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.DeleteChannel,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string>()))),
            "propose_delete_channel",
            "CRITICAL WRITE-SHAPED TOOL. Create a same-chat approval request to permanently delete a channel. Never use for a rename, lock, or topic change."),

        AIFunctionFactory.Create(
            async (string name, string? permissions) => await ExecuteAsync("propose_create_role", guildId, requesterId, async () =>
            {
                var arguments = new Dictionary<string, string> { ["name"] = name };
                Add(arguments, "permissions", permissions);
                return await ProposeAsync(guildId, requesterId, approvalChannelId, new(ChangeActionType.CreateRole, 0, arguments));
            }),
            "propose_create_role",
            "Create a same-chat approval request for a Discord role. permissions is an optional raw Discord permission bitset string; omit it for no permissions."),

        AIFunctionFactory.Create(
            async (string roleId, string permissions) => await ExecuteAsync("propose_change_role_permissions", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.ChangeRolePermissions,
                    ParseDiscordId(roleId, nameof(roleId)),
                    new Dictionary<string, string> { ["permissions"] = permissions }))),
            "propose_change_role_permissions",
            "HIGH-RISK TOOL. Create a same-chat approval request to replace a role's entire raw permission bitset."),

        AIFunctionFactory.Create(
            async (string roleId) => await ExecuteAsync("propose_delete_role", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.DeleteRole,
                    ParseDiscordId(roleId, nameof(roleId)),
                    new Dictionary<string, string>()))),
            "propose_delete_role",
            "CRITICAL WRITE-SHAPED TOOL. Create a same-chat approval request to permanently delete a role."),

        AIFunctionFactory.Create(
            async (string userId, string roleId) => await ExecuteAsync("propose_assign_role", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.AssignRole,
                    ParseDiscordId(userId, nameof(userId)),
                    new Dictionary<string, string> { ["role_id"] = ParseDiscordId(roleId, nameof(roleId)).ToString() }))),
            "propose_assign_role",
            "Create a same-chat approval request to assign a role to a member. Both IDs must be Discord ID strings."),

        AIFunctionFactory.Create(
            async (string userId, string roleId) => await ExecuteAsync("propose_remove_role", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.RemoveRole,
                    ParseDiscordId(userId, nameof(userId)),
                    new Dictionary<string, string> { ["role_id"] = ParseDiscordId(roleId, nameof(roleId)).ToString() }))),
            "propose_remove_role",
            "Create a same-chat approval request to remove a role from a member. Both IDs must be Discord ID strings."),

        AIFunctionFactory.Create(
            async (string userId, int minutes, string? reason) => await ExecuteAsync("propose_timeout_member", guildId, requesterId, async () =>
            {
                if (minutes is < 1 or > 40320) throw new ArgumentOutOfRangeException(nameof(minutes), "Timeout must be between 1 minute and 28 days.");
                var arguments = new Dictionary<string, string> { ["minutes"] = minutes.ToString() };
                Add(arguments, "reason", reason);
                return await ProposeAsync(guildId, requesterId, approvalChannelId, new(ChangeActionType.TimeoutMember, ParseDiscordId(userId, nameof(userId)), arguments));
            }),
            "propose_timeout_member",
            "Create a same-chat approval request to timeout a member for 1 to 40320 minutes."),

        AIFunctionFactory.Create(
            async (string userId, string? reason) => await ExecuteAsync("propose_kick_member", guildId, requesterId, async () =>
            {
                var arguments = new Dictionary<string, string>();
                Add(arguments, "reason", reason);
                return await ProposeAsync(guildId, requesterId, approvalChannelId, new(ChangeActionType.KickMember, ParseDiscordId(userId, nameof(userId)), arguments));
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
                return await ProposeAsync(guildId, requesterId, approvalChannelId, new(ChangeActionType.BanMember, ParseDiscordId(userId, nameof(userId)), arguments));
            }),
            "propose_ban_member",
            "HIGH-RISK TOOL. Create a same-chat approval request to ban a Discord user."),

        AIFunctionFactory.Create(
            async (string userId) => await ExecuteAsync("propose_unban_member", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.UnbanMember,
                    ParseDiscordId(userId, nameof(userId)),
                    new Dictionary<string, string>()))),
            "propose_unban_member",
            "Create a same-chat approval request to remove a Discord ban."),

        AIFunctionFactory.Create(
            async (string webhookId) => await ExecuteAsync("propose_delete_webhook", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.DeleteWebhook,
                    ParseDiscordId(webhookId, nameof(webhookId)),
                    new Dictionary<string, string>()))),
            "propose_delete_webhook",
            "HIGH-RISK TOOL. Create a same-chat approval request to permanently delete a webhook."),

        AIFunctionFactory.Create(
            async (string inviteCode) => await ExecuteAsync("propose_revoke_invite", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
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
                return await ProposeAsync(guildId, requesterId, approvalChannelId, new(ChangeActionType.CreateScheduledEvent, 0, arguments));
            }),
            "propose_create_scheduled_event",
            "Create a same-chat approval request for an external Discord scheduled event. start and end must be ISO-8601 timestamps and location is required."),

        AIFunctionFactory.Create(
            async (string channelId, string roleId, string allow, string deny) => await ExecuteAsync("propose_set_role_channel_overwrite", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.SetRoleChannelOverwrite,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string>
                    {
                        ["role_id"] = ParseDiscordId(roleId, nameof(roleId)).ToString(),
                        ["allow"] = allow,
                        ["deny"] = deny
                    }))),
            "propose_set_role_channel_overwrite",
            "HIGH-RISK TOOL. Create a same-chat approval request to replace one role's channel permission overwrite. allow and deny are raw Discord bitset strings."),

        AIFunctionFactory.Create(
            async (string channelId, string roleId) => await ExecuteAsync("propose_remove_role_channel_overwrite", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.RemoveRoleChannelOverwrite,
                    ParseDiscordId(channelId, nameof(channelId)),
                    new Dictionary<string, string> { ["role_id"] = ParseDiscordId(roleId, nameof(roleId)).ToString() }))),
            "propose_remove_role_channel_overwrite",
            "HIGH-RISK TOOL. Create a same-chat approval request to remove one role-specific channel overwrite."),

        AIFunctionFactory.Create(
            async (string threadId, bool locked) => await ExecuteAsync("propose_set_thread_locked", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.SetThreadLocked,
                    ParseDiscordId(threadId, nameof(threadId)),
                    new Dictionary<string, string> { ["locked"] = locked.ToString() }))),
            "propose_set_thread_locked",
            "Create a same-chat approval request to lock or unlock a Discord thread."),

        AIFunctionFactory.Create(
            async (string threadId, bool archived) => await ExecuteAsync("propose_set_thread_archived", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
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
                return await ProposeAsync(guildId, requesterId, approvalChannelId, new(ChangeActionType.CreateAutoModKeywordRule, 0, arguments));
            }),
            "propose_create_automod_keyword_rule",
            "HIGH-RISK TOOL. Create a same-chat approval request for an AutoMod keyword rule that blocks matching messages."),

        AIFunctionFactory.Create(
            async (string ruleId, bool enabled) => await ExecuteAsync("propose_set_automod_rule_enabled", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.SetAutoModRuleEnabled,
                    ParseDiscordId(ruleId, nameof(ruleId)),
                    new Dictionary<string, string> { ["enabled"] = enabled.ToString() }))),
            "propose_set_automod_rule_enabled",
            "HIGH-RISK TOOL. Create a same-chat approval request to enable or disable an AutoMod rule."),

        AIFunctionFactory.Create(
            async (string ruleId) => await ExecuteAsync("propose_delete_automod_rule", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
                    ChangeActionType.DeleteAutoModRule,
                    ParseDiscordId(ruleId, nameof(ruleId)),
                    new Dictionary<string, string>()))),
            "propose_delete_automod_rule",
            "CRITICAL TOOL. Create a same-chat approval request to permanently delete an AutoMod rule."),

        AIFunctionFactory.Create(
            async (bool enabled, string description) => await ExecuteAsync("propose_update_welcome_screen", guildId, requesterId, async () =>
                await ProposeAsync(guildId, requesterId, approvalChannelId, new(
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
                return await ProposeAsync(guildId, requesterId, approvalChannelId, new(ChangeActionType.UpdateOnboarding, 0, arguments));
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

    private static ulong ParseDiscordId(string value, string name) =>
        ulong.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"{name} must be a Discord snowflake ID encoded as a string.");

    private async Task<object> ProposeAsync(
        ulong guildId,
        ulong requesterId,
        ulong approvalChannelId,
        ChangeRequest request)
    {
        var proposal = await approvals.ProposeAsync(guildId, requesterId, approvalChannelId, request);
        return new
        {
            Success = true,
            proposal.DisplayId,
            proposal.Status,
            proposal.Risk,
            proposal.Change,
            ApprovalPending = true,
            ApprovalChannelId = approvalChannelId.ToString(),
            Message = "No Discord change has been executed. An administrator must use the approval card in this chat."
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
