using Discord;
using Discord.Net;
using DiscordHealth.Runtime.DiscordAdapter;

namespace DiscordHealth.Runtime.Changes;

public interface IApprovalPublisher
{
    Task<ChangeProposal> ProposeAsync(
        ulong guildId,
        ulong requesterId,
        ulong approvalChannelId,
        ChangeRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class DiscordApprovalPublisher(
    IChangeProposalService proposals,
    IDiscordClientAccessor discord) : IApprovalPublisher
{
    public async Task<ChangeProposal> ProposeAsync(
        ulong guildId,
        ulong requesterId,
        ulong approvalChannelId,
        ChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        var approvalChannel = discord.Client.GetChannel(approvalChannelId) as IMessageChannel
            ?? throw new InvalidOperationException("The invoking Discord channel cannot host a Quorum approval message.");

        var proposal = await proposals.ProposeAsync(guildId, requesterId, approvalChannelId, request, cancellationToken);
        IUserMessage message;
        try
        {
            message = await approvalChannel.SendMessageAsync(
                embed: ChangeApprovalPresenter.Embed(proposal),
                components: ChangeApprovalPresenter.Components(proposal));
        }
        catch (HttpException exception) when ((int)exception.HttpCode == 403)
        {
            throw new UnauthorizedAccessException(
                "Quorum cannot post the approval card in this channel. Grant View Channel, Send Messages, and Embed Links here, then retry.",
                exception);
        }
        return await proposals.AttachApprovalMessageAsync(guildId, proposal.Id, message.Id, cancellationToken);
    }
}

internal static class ChangeApprovalPresenter
{
    public static MessageComponent Components(ChangeProposal proposal) => new ComponentBuilder()
        .WithButton("Approve", $"quorum:approve:{proposal.Id:N}", ButtonStyle.Success)
        .WithButton("Reject", $"quorum:reject:{proposal.Id:N}", ButtonStyle.Danger)
        .WithButton("Details", $"quorum:details:{proposal.Id:N}", ButtonStyle.Secondary)
        .Build();

    public static Embed Embed(ChangeProposal proposal)
    {
        var target = proposal.Change.DisplayTarget
            ?? (proposal.Change.ResourceId == 0 ? proposal.Change.ResourceType : $"<{proposal.Change.ResourceType}:{proposal.Change.ResourceId}>");
        var arguments = proposal.Change.Arguments is { Count: > 0 }
            ? string.Join("\n", proposal.Change.Arguments.Select(x => $"\u0060{x.Key}\u0060: {(x.Key == "code" ? "[redacted]" : x.Value)}"))
            : "None";

        return new EmbedBuilder()
            .WithTitle($"Quorum approval required · {proposal.DisplayId}")
            .WithDescription($"Requested by <@{proposal.RequestedBy}>")
            .WithColor(proposal.Risk switch
            {
                ChangeRisk.Low => Color.Blue,
                ChangeRisk.Medium => Color.Orange,
                ChangeRisk.High => Color.DarkOrange,
                ChangeRisk.Critical => Color.Red,
                _ => Color.LightGrey
            })
            .AddField("Action", Humanize(proposal.Change.Action), true)
            .AddField("Risk", proposal.Risk, true)
            .AddField("Status", proposal.Status, true)
            .AddField("Target", target)
            .AddField("Before → after", $"\u0060{proposal.Change.Before}\u0060 → \u0060{proposal.Change.After}\u0060")
            .AddField("Parameters", arguments[..Math.Min(arguments.Length, 1000)])
            .AddField("Required bot permission", proposal.Change.RequiredDiscordPermission, true)
            .AddField("Approvals", $"{proposal.Approvals.Count}/{proposal.RequiredApprovals}", true)
            .AddField("Expires", $"<t:{proposal.ExpiresAt.ToUnixTimeSeconds()}:R>", true)
            .WithFooter(proposal.Status == ChangeProposalStatus.PendingApproval
                ? "No Discord change has been made."
                : proposal.StatusReason ?? $"Proposal is {proposal.Status}.")
            .WithCurrentTimestamp()
            .Build();
    }

    public static string Format(ChangeProposal proposal) =>
        $"**{proposal.DisplayId} · {proposal.Status}**\n{Humanize(proposal.Change.Action)} · {proposal.Change.DisplayTarget ?? proposal.Change.ResourceType}\n" +
        $"\u0060{proposal.Change.Before}\u0060 → \u0060{proposal.Change.After}\u0060\nNo unapproved change is executed.";

    private static string Humanize(ChangeActionType action) =>
        string.Concat(action.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
}
