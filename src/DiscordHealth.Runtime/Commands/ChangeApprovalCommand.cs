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

    private void EnsureApprovalContext(ChangeProposal proposal)
    {
        if (proposal.ApprovalChannelId != Context.Channel.Id) throw new UnauthorizedAccessException("This proposal can only be approved in the channel where it was requested.");
        if (Context.User is not SocketGuildUser user || !user.GuildPermissions.Administrator) throw new UnauthorizedAccessException("Administrator is required to approve Quorum changes.");
    }

    private async Task UpdateProposalMessageAsync(ChangeProposal proposal)
    {
        if (Context.Interaction is not SocketMessageComponent component) return;
        var terminal = proposal.Status is ChangeProposalStatus.Completed or ChangeProposalStatus.Rejected or ChangeProposalStatus.Expired or ChangeProposalStatus.Stale or ChangeProposalStatus.Failed or ChangeProposalStatus.NeedsReview;
        await component.Message.ModifyAsync(properties =>
        {
            properties.Embeds = new[] { ChangeApprovalPresenter.Embed(proposal) };
            if (terminal) properties.Components = new ComponentBuilder().Build();
        });
    }
}
