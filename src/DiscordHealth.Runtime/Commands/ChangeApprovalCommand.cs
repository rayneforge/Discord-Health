using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordHealth.Runtime.Changes;

namespace DiscordHealth.Runtime.Commands;

[RequireContext(ContextType.Guild)]
public sealed class ChangeApprovalCommand(IChangeProposalService proposals) : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("quorum:approve:*")]
    public async Task ApproveAsync(string proposalId)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            var id = Guid.ParseExact(proposalId, "N");
            var pending = await proposals.GetAsync(Context.Guild.Id, id) ?? throw new KeyNotFoundException("Proposal not found.");
            EnsureApprovalContext(pending);
            var proposal = await proposals.ApproveAsync(Context.Guild.Id, id, Context.User.Id);
            await UpdateProposalMessageAsync(proposal);
            await FollowupAsync($"{proposal.DisplayId}: **{proposal.Status}**", ephemeral: true);
        }
        catch (Exception exception) { await FollowupAsync($"Approval failed: {exception.Message}", ephemeral: true); }
    }

    [ComponentInteraction("quorum:reject:*")]
    public async Task RejectAsync(string proposalId)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            var id = Guid.ParseExact(proposalId, "N");
            var pending = await proposals.GetAsync(Context.Guild.Id, id) ?? throw new KeyNotFoundException("Proposal not found.");
            EnsureApprovalContext(pending);
            var proposal = await proposals.RejectAsync(Context.Guild.Id, id, Context.User.Id, "Rejected through Discord approval controls.");
            await UpdateProposalMessageAsync(proposal);
            await FollowupAsync($"{proposal.DisplayId}: **Rejected**. No change was made.", ephemeral: true);
        }
        catch (Exception exception) { await FollowupAsync($"Rejection failed: {exception.Message}", ephemeral: true); }
    }

    [ComponentInteraction("quorum:details:*")]
    public async Task DetailsAsync(string proposalId)
    {
        try
        {
            var proposal = await proposals.GetAsync(Context.Guild.Id, Guid.ParseExact(proposalId, "N"));
            if (proposal is null) { await RespondAsync("Proposal not found.", ephemeral: true); return; }
            EnsureApprovalContext(proposal);
            await RespondAsync(embed: ChangeApprovalPresenter.Embed(proposal), ephemeral: true);
        }
        catch (Exception exception) { await RespondAsync($"Details unavailable: {exception.Message}", ephemeral: true); }
    }

    [ComponentInteraction("quorum:approve-batch:*")]
    public async Task ApproveBatchAsync(string approvalBatchId)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            var id = Guid.ParseExact(approvalBatchId, "N");
            var pending = await proposals.GetBatchAsync(Context.Guild.Id, id);
            EnsureApprovalBatchContext(pending);
            var batch = await proposals.ApproveBatchAsync(Context.Guild.Id, id, Context.User.Id);
            await UpdateBatchMessageAsync(id, batch);
            await FollowupAsync($"{ChangeApprovalPresenter.BatchDisplayId(id)}: {BatchStatus(batch)}", ephemeral: true);
        }
        catch (Exception exception) { await FollowupAsync($"Batch approval failed: {exception.Message}", ephemeral: true); }
    }

    [ComponentInteraction("quorum:reject-batch:*")]
    public async Task RejectBatchAsync(string approvalBatchId)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            var id = Guid.ParseExact(approvalBatchId, "N");
            var pending = await proposals.GetBatchAsync(Context.Guild.Id, id);
            EnsureApprovalBatchContext(pending);
            var batch = await proposals.RejectBatchAsync(Context.Guild.Id, id, Context.User.Id, "Rejected through Discord batch approval controls.");
            await UpdateBatchMessageAsync(id, batch);
            await FollowupAsync($"{ChangeApprovalPresenter.BatchDisplayId(id)}: all pending changes were rejected. No rejected change was executed.", ephemeral: true);
        }
        catch (Exception exception) { await FollowupAsync($"Batch rejection failed: {exception.Message}", ephemeral: true); }
    }

    [ComponentInteraction("quorum:details-batch:*")]
    public async Task BatchDetailsAsync(string approvalBatchId)
    {
        try
        {
            var id = Guid.ParseExact(approvalBatchId, "N");
            var batch = await proposals.GetBatchAsync(Context.Guild.Id, id);
            EnsureApprovalBatchContext(batch);
            await RespondAsync(embed: ChangeApprovalPresenter.BatchEmbed(id, batch), ephemeral: true);
        }
        catch (Exception exception) { await RespondAsync($"Batch details unavailable: {exception.Message}", ephemeral: true); }
    }

    private void EnsureApprovalContext(ChangeProposal proposal)
    {
        if (proposal.ApprovalChannelId != Context.Channel.Id) throw new UnauthorizedAccessException("This proposal can only be approved in the channel where it was requested.");
        if (Context.User is not SocketGuildUser user || !user.GuildPermissions.Administrator) throw new UnauthorizedAccessException("Administrator is required to approve Quorum changes.");
    }

    private void EnsureApprovalBatchContext(IReadOnlyList<ChangeProposal> batch)
    {
        if (batch.Count == 0) throw new KeyNotFoundException("Approval batch not found.");
        foreach (var proposal in batch) EnsureApprovalContext(proposal);
    }

    private async Task UpdateProposalMessageAsync(ChangeProposal proposal)
    {
        if (Context.Interaction is not SocketMessageComponent component) return;
        var terminal = IsTerminal(proposal.Status);
        await component.Message.ModifyAsync(properties =>
        {
            properties.Embeds = new[] { ChangeApprovalPresenter.Embed(proposal) };
            if (terminal) properties.Components = new ComponentBuilder().Build();
        });
    }

    private async Task UpdateBatchMessageAsync(Guid approvalBatchId, IReadOnlyList<ChangeProposal> batch)
    {
        if (Context.Interaction is not SocketMessageComponent component) return;
        await component.Message.ModifyAsync(properties =>
        {
            properties.Embeds = new[] { ChangeApprovalPresenter.BatchEmbed(approvalBatchId, batch) };
            if (batch.All(x => IsTerminal(x.Status))) properties.Components = new ComponentBuilder().Build();
        });
    }

    private static bool IsTerminal(ChangeProposalStatus status) => status is
        ChangeProposalStatus.Completed or ChangeProposalStatus.Rejected or ChangeProposalStatus.Expired or
        ChangeProposalStatus.Stale or ChangeProposalStatus.Failed or ChangeProposalStatus.Cancelled or
        ChangeProposalStatus.NeedsReview;

    private static string BatchStatus(IReadOnlyList<ChangeProposal> batch) => string.Join(", ", batch
        .GroupBy(x => x.Status)
        .OrderBy(x => x.Key)
        .Select(x => $"{x.Count()} {x.Key}"));
}
