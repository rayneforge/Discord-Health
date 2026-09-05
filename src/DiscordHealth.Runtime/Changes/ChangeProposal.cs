namespace DiscordHealth.Runtime.Changes;

public enum ChangeRisk { Low, Medium, High, Critical }
public enum ChangeProposalStatus { Draft, PendingApproval, Approved, Validating, Executing, Verifying, Completed, Rejected, Expired, Stale, Failed, Cancelled, NeedsReview }
public enum ChangeActionType
{
    CreateTextChannel,
    CreateCategory,
    RenameChannel,
    ChangeChannelTopic,
    ChangeChannelSlowMode,
    DeleteChannel,
    CreateRole,
    ChangeRolePermissions,
    DeleteRole,
    AssignRole,
    RemoveRole,
    TimeoutMember,
    KickMember,
    BanMember,
    UnbanMember,
    DeleteWebhook,
    RevokeInvite,
    CreateScheduledEvent,
    SetRoleChannelOverwrite,
    RemoveRoleChannelOverwrite,
    SetThreadLocked,
    SetThreadArchived,
    CreateAutoModKeywordRule,
    SetAutoModRuleEnabled,
    DeleteAutoModRule,
    UpdateWelcomeScreen,
    UpdateOnboarding
}

public sealed record ChangeRequest(
    ChangeActionType Action,
    ulong ResourceId,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record ChangeSpecification(
    ChangeActionType Action,
    string ResourceType,
    ulong ResourceId,
    string Property,
    string Before,
    string After,
    string RequiredDiscordPermission,
    string? DisplayTarget = null,
    IReadOnlyDictionary<string, string>? Arguments = null);

public sealed record ChangeApproval(ulong UserId, DateTimeOffset ApprovedAt);

public sealed record ChangeProposal(
    Guid Id,
    string DisplayId,
    ulong GuildId,
    ulong RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    ChangeRisk Risk,
    ChangeProposalStatus Status,
    ChangeSpecification Change,
    int RequiredApprovals,
    bool AllowSelfApproval,
    IReadOnlyList<ChangeApproval> Approvals,
    ulong? ApprovalMessageId,
    string? StatusReason,
    DateTimeOffset? ExecutedAt,
    string? VerificationValue,
    ulong? ApprovalChannelId = null,
    Guid? ApprovalBatchId = null);
