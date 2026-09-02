using Microsoft.Extensions.Options;

namespace DiscordHealth.Runtime.Changes;

public interface IChangeProposalService
{
    Task<ChangeProposal> ProposeAsync(ulong guildId, ulong requesterId, ulong approvalChannelId, ChangeRequest request, CancellationToken cancellationToken = default);
    Task<ChangeProposal> AttachApprovalMessageAsync(ulong guildId, Guid proposalId, ulong messageId, CancellationToken cancellationToken = default);
    Task<ChangeProposal> ApproveAsync(ulong guildId, Guid proposalId, ulong approverId, CancellationToken cancellationToken = default);
    Task<ChangeProposal> RejectAsync(ulong guildId, Guid proposalId, ulong rejectedBy, string reason, CancellationToken cancellationToken = default);
    Task<ChangeProposal?> GetAsync(ulong guildId, Guid proposalId, CancellationToken cancellationToken = default);
}

internal sealed class ChangeProposalService(IOptions<QuorumOptions> options, IChangeProposalStore store, IApprovedChangeExecutor executor) : IChangeProposalService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ChangeProposal> ProposeAsync(ulong guildId, ulong requesterId, ulong approvalChannelId, ChangeRequest request, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var change = await executor.CreateSpecificationAsync(guildId, request, cancellationToken);
        if (change.Before == change.After) throw new InvalidOperationException("The requested change already matches the current value.");
        var id = Guid.NewGuid();
        var risk = RiskFor(request.Action);
        var proposal = new ChangeProposal(id, "QCHG-" + id.ToString("N")[..8].ToUpperInvariant(), guildId, requesterId, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(options.Value.Writes.ApprovalTtlMinutes), risk, ChangeProposalStatus.PendingApproval,
            change, 1, options.Value.Writes.AllowLowRiskSelfApproval, [], null, null, null, null, approvalChannelId);
        await store.SaveAsync(proposal, cancellationToken);
        return proposal;
    }

    public async Task<ChangeProposal> AttachApprovalMessageAsync(ulong guildId, Guid proposalId, ulong messageId, CancellationToken cancellationToken = default)
    {
        var proposal = await RequiredAsync(guildId, proposalId, cancellationToken);
        proposal = proposal with { ApprovalMessageId = messageId };
        await store.SaveAsync(proposal, cancellationToken);
        return proposal;
    }

    public async Task<ChangeProposal> ApproveAsync(ulong guildId, Guid proposalId, ulong approverId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var proposal = await RequiredAsync(guildId, proposalId, cancellationToken);
            if (proposal.Status != ChangeProposalStatus.PendingApproval) throw new InvalidOperationException($"Proposal is {proposal.Status}, not pending approval.");
            if (proposal.ExpiresAt <= DateTimeOffset.UtcNow) return await SaveAsync(proposal with { Status = ChangeProposalStatus.Expired, StatusReason = "Approval TTL elapsed." }, cancellationToken);
            if (!proposal.AllowSelfApproval && proposal.RequestedBy == approverId) throw new InvalidOperationException("Self-approval is not permitted for this proposal.");
            if (proposal.Approvals.Any(x => x.UserId == approverId)) throw new InvalidOperationException("This administrator already approved the proposal.");

            proposal = proposal with { Approvals = proposal.Approvals.Append(new ChangeApproval(approverId, DateTimeOffset.UtcNow)).ToArray() };
            if (proposal.Approvals.Count < proposal.RequiredApprovals) return await SaveAsync(proposal, cancellationToken);
            proposal = await SaveAsync(proposal with { Status = ChangeProposalStatus.Approved }, cancellationToken);
            proposal = await SaveAsync(proposal with { Status = ChangeProposalStatus.Validating }, cancellationToken);

            var current = await executor.ObserveAsync(guildId, proposal.Change, cancellationToken);
            if (current != proposal.Change.Before)
                return await SaveAsync(proposal with { Status = ChangeProposalStatus.Stale, StatusReason = $"Expected {proposal.Change.Before}; observed {current}." }, cancellationToken);

            proposal = await SaveAsync(proposal with { Status = ChangeProposalStatus.Executing }, cancellationToken);
            try { await executor.ExecuteAsync(guildId, proposal.Change, cancellationToken); }
            catch (Exception exception) { return await SaveAsync(proposal with { Status = ChangeProposalStatus.Failed, StatusReason = exception.Message }, cancellationToken); }

            proposal = await SaveAsync(proposal with { Status = ChangeProposalStatus.Verifying, ExecutedAt = DateTimeOffset.UtcNow }, cancellationToken);
            var observed = await executor.ObserveAsync(guildId, proposal.Change, cancellationToken);
            return await SaveAsync(proposal with
            {
                Status = observed == proposal.Change.After ? ChangeProposalStatus.Completed : ChangeProposalStatus.NeedsReview,
                StatusReason = observed == proposal.Change.After ? null : "Discord accepted the request but verification did not match.",
                VerificationValue = observed
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<ChangeProposal> RejectAsync(ulong guildId, Guid proposalId, ulong rejectedBy, string reason, CancellationToken cancellationToken = default)
    {
        var proposal = await RequiredAsync(guildId, proposalId, cancellationToken);
        if (proposal.Status != ChangeProposalStatus.PendingApproval) throw new InvalidOperationException($"Proposal is {proposal.Status}, not pending approval.");
        return await SaveAsync(proposal with { Status = ChangeProposalStatus.Rejected, StatusReason = $"Rejected by {rejectedBy}: {reason}" }, cancellationToken);
    }

    public Task<ChangeProposal?> GetAsync(ulong guildId, Guid proposalId, CancellationToken cancellationToken = default) => store.GetAsync(guildId, proposalId, cancellationToken);
    private static ChangeRisk RiskFor(ChangeActionType action) => action switch
    {
        ChangeActionType.ChangeChannelSlowMode or ChangeActionType.ChangeChannelTopic or ChangeActionType.RenameChannel => ChangeRisk.Low,
        ChangeActionType.CreateTextChannel or ChangeActionType.CreateCategory or ChangeActionType.CreateRole or ChangeActionType.AssignRole or ChangeActionType.RemoveRole => ChangeRisk.Medium,
        ChangeActionType.ChangeRolePermissions => ChangeRisk.High,
        ChangeActionType.TimeoutMember or ChangeActionType.UnbanMember or ChangeActionType.RevokeInvite or ChangeActionType.CreateScheduledEvent => ChangeRisk.Medium,
        ChangeActionType.SetThreadLocked or ChangeActionType.SetThreadArchived or ChangeActionType.UpdateWelcomeScreen or ChangeActionType.UpdateOnboarding => ChangeRisk.Medium,
        ChangeActionType.KickMember or ChangeActionType.BanMember or ChangeActionType.DeleteWebhook or
        ChangeActionType.SetRoleChannelOverwrite or ChangeActionType.RemoveRoleChannelOverwrite or
        ChangeActionType.CreateAutoModKeywordRule or ChangeActionType.SetAutoModRuleEnabled => ChangeRisk.High,
        ChangeActionType.DeleteAutoModRule => ChangeRisk.Critical,
        ChangeActionType.DeleteChannel or ChangeActionType.DeleteRole => ChangeRisk.Critical,
        _ => ChangeRisk.High
    };

    private void EnsureEnabled() { if (!options.Value.Writes.Enabled) throw new InvalidOperationException("Quorum write capabilities are disabled."); }
    private async Task<ChangeProposal> RequiredAsync(ulong guildId, Guid id, CancellationToken ct) => await store.GetAsync(guildId, id, ct) ?? throw new KeyNotFoundException("Change proposal was not found.");
    private async Task<ChangeProposal> SaveAsync(ChangeProposal proposal, CancellationToken ct) { await store.SaveAsync(proposal, ct); return proposal; }
}
