using DiscordHealth.Runtime.ServerConfiguration;

namespace DiscordHealth.Runtime.Analysis;

internal sealed class SecurityAnalyzer : ISecurityAnalyzer
{
    public IReadOnlyList<SecurityFinding> Analyze(ServerConfigurationSnapshot snapshot)
    {
        var findings = new List<SecurityFinding>();
        AnalyzeGuild(snapshot, findings);
        AnalyzeRoles(snapshot, findings);
        AnalyzeChannels(snapshot, findings);
        AddVisibilityFinding("QVIS-001", "AutoMod", snapshot.AutoModRules.Status, snapshot.AutoModRules.Reason, findings);
        AddVisibilityFinding("QVIS-002", "Invites", snapshot.Invites.Status, snapshot.Invites.Reason, findings);
        AddVisibilityFinding("QVIS-003", "Webhooks", snapshot.Webhooks.Status, snapshot.Webhooks.Reason, findings);
        AddVisibilityFinding("QVIS-004", "Integrations", snapshot.Integrations.Status, snapshot.Integrations.Reason, findings);
        AddVisibilityFinding("QVIS-005", "Bans", snapshot.Bans.Status, snapshot.Bans.Reason, findings);
        AddVisibilityFinding("QVIS-006", "Audit log", snapshot.AuditLog.Status, snapshot.AuditLog.Reason, findings);
        return findings.OrderByDescending(x => x.Severity).ThenBy(x => x.Id).ToArray();
    }

    private static void AnalyzeGuild(ServerConfigurationSnapshot snapshot, ICollection<SecurityFinding> findings)
    {
        if (!snapshot.Guild.IsAvailable || snapshot.Guild.Data is not { } guild) return;

        if (guild.MfaLevel.Equals("None", StringComparison.OrdinalIgnoreCase))
            findings.Add(Finding("QSEC-001", "security", FindingSeverity.High, "Moderator MFA is not enforced", "The guild MFA level is None.", "Privileged accounts can perform administrative actions without a server-enforced MFA requirement.", "Review Discord's moderator MFA requirement.", "guild", "mfa_level", guild.MfaLevel));

        if (guild.VerificationLevel is "None" or "Low")
            findings.Add(Finding("QSEC-002", "security", FindingSeverity.Medium, "Verification level is low", $"Verification is {guild.VerificationLevel}.", "Low-friction account entry can increase raid exposure.", "Review whether a higher verification level fits this community.", "guild", "verification_level", guild.VerificationLevel));

        if (guild.ExplicitContentFilter.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            findings.Add(Finding("QSEC-003", "security", FindingSeverity.Medium, "Explicit-content filtering is disabled", "Discord's explicit-content filter is disabled.", "Potentially unsafe media receives less native filtering.", "Review Discord's explicit media content filter.", "guild", "explicit_content_filter", guild.ExplicitContentFilter));

        if (guild.SafetyAlertsChannelId is null)
            findings.Add(Finding("QCFG-006", "configuration", FindingSeverity.Medium, "Safety alert channel is missing", "No safety alerts channel is configured.", "Discord raid or safety signals may lack a clear administrator destination.", "Configure a private, monitored safety alert destination if Community features support it.", "guild", "safety_alerts_channel_id", "null"));

        if (guild.QuorumSensitivePermissions.Contains("Administrator"))
            findings.Add(Finding("QSEC-008", "security", FindingSeverity.High, "Quorum has Administrator", "Quorum's effective guild permissions include Administrator.", "A compromised Quorum credential would have unrestricted guild authority.", "Administrator is accepted by deployment policy, but keep credential isolation, approval gating, and audit controls enabled.", "collector", "quorum_permissions", guild.QuorumPermissions.ToString()));
    }

    private static void AnalyzeRoles(ServerConfigurationSnapshot snapshot, ICollection<SecurityFinding> findings)
    {
        if (!snapshot.Roles.IsAvailable || snapshot.Roles.Data is not { } roles) return;
        foreach (var role in roles)
        {
            if (role.IsEveryone && role.SensitivePermissions.Count > 0)
                findings.Add(Finding("QPERM-001", "permissions", FindingSeverity.Critical, "@everyone has sensitive guild permissions", $"@everyone has: {string.Join(", ", role.SensitivePermissions)}.", "Every server member inherits these permissions.", "Review whether these permissions should be limited to explicit roles.", "role", role.Id.ToString(), string.Join(",", role.SensitivePermissions)));
            else if (!role.IsManaged && role.SensitivePermissions.Contains("Administrator"))
                findings.Add(Finding("QPERM-002", "permissions", FindingSeverity.High, $"Role {role.Name} has Administrator", "Administrator bypasses all channel permission overwrites.", "Any account holding the role has unrestricted guild authority.", "Confirm every Administrator role and assignment is intentional.", "role", role.Id.ToString(), "Administrator"));
        }

        foreach (var group in roles.Where(x => !x.IsEveryone).GroupBy(x => x.Permissions).Where(x => x.Count() > 1))
            findings.Add(Finding("QCFG-004", "configuration", FindingSeverity.Notice, "Roles have duplicate permission sets", string.Join(", ", group.Select(x => x.Name)), "Duplicate authorization structures increase maintenance cost and drift risk.", "Review whether the roles need distinct permission sets.", "roles", "permissions", group.Key.ToString()));
    }

    private static void AnalyzeChannels(ServerConfigurationSnapshot snapshot, ICollection<SecurityFinding> findings)
    {
        if (!snapshot.Channels.IsAvailable || snapshot.Channels.Data is not { } channels) return;
        foreach (var channel in channels.Where(x => x.HasDirectMemberOverwrites))
            findings.Add(Finding("QPERM-003", "permissions", FindingSeverity.Notice, $"Direct member override in {channel.Name}", "The channel contains at least one member-specific permission overwrite.", "Direct exceptions are harder to review than role-based access.", "Confirm the exception remains intentional or replace it with a role.", "channel", channel.Id.ToString(), "member overwrite"));

        var categories = channels.Where(x => x.Type.Contains("Category", StringComparison.OrdinalIgnoreCase)).ToDictionary(x => x.Id);
        foreach (var channel in channels.Where(x => x.CategoryId.HasValue))
        {
            if (!categories.TryGetValue(channel.CategoryId!.Value, out var category)) continue;
            if (!Equivalent(channel.PermissionOverwrites, category.PermissionOverwrites))
                findings.Add(Finding("QCFG-001", "configuration", FindingSeverity.Notice, $"{channel.Name} is not permission-synced", $"The channel's overwrites differ from category {category.Name}.", "An intentional exception can drift or expose a child channel more broadly than expected.", "Compare the effective access difference and classify the exception in Quorum.", "channel", channel.Id.ToString(), "unsynced"));
        }
    }

    private static bool Equivalent(IReadOnlyList<PermissionOverwriteConfiguration> left, IReadOnlyList<PermissionOverwriteConfiguration> right) =>
        left.OrderBy(x => x.TargetType).ThenBy(x => x.TargetId).SequenceEqual(right.OrderBy(x => x.TargetType).ThenBy(x => x.TargetId));

    private static void AddVisibilityFinding(string id, string name, CollectorStatus status, string? reason, ICollection<SecurityFinding> findings)
    {
        if (status is CollectorStatus.Available or CollectorStatus.NotApplicable) return;
        findings.Add(new SecurityFinding(id, "visibility", FindingSeverity.Notice, FindingStatus.Unknown, $"{name} could not be fully inspected", reason ?? status.ToString(), "Unobserved configuration cannot be assessed as safe.", "Grant the required permission if this coverage is desired, or accept the explicit unknown state.", "collector", name, status.ToString(), 0, true));
    }

    private static SecurityFinding Finding(string id, string category, FindingSeverity severity, string title, string observation, string risk, string recommendation, string source, string field, string value) =>
        new(id, category, severity, FindingStatus.Fail, title, observation, risk, recommendation, source, field, value, 1, true);
}
