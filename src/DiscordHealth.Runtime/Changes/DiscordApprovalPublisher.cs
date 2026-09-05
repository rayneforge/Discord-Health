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
        Guid? approvalBatchId = null,
        CancellationToken cancellationToken = default);
    Task PublishBatchAsync(ulong guildId, Guid approvalBatchId, CancellationToken cancellationToken = default);
}

internal sealed class DiscordApprovalPublisher(
    IChangeProposalService proposals,
    IDiscordClientAccessor discord,
    ILogger<DiscordApprovalPublisher> logger) : IApprovalPublisher
{
    public async Task<ChangeProposal> ProposeAsync(
        ulong guildId,
        ulong requesterId,
        ulong approvalChannelId,
        ChangeRequest request,
        Guid? approvalBatchId = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await proposals.ProposeAsync(guildId, requesterId, approvalChannelId, request, approvalBatchId, cancellationToken);
        if (approvalBatchId.HasValue) return proposal;
        var approvalChannel = GetApprovalChannel(approvalChannelId);
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
        var attached = await proposals.AttachApprovalMessageAsync(guildId, proposal.Id, message.Id, cancellationToken);
        logger.LogInformation(
            "Quorum approval proposal {ProposalId} published to channel {ChannelId} as message {MessageId}.",
            proposal.DisplayId,
            approvalChannelId,
            message.Id);
        return attached;
    }

    public async Task PublishBatchAsync(ulong guildId, Guid approvalBatchId, CancellationToken cancellationToken = default)
    {
        var batch = await proposals.GetBatchAsync(guildId, approvalBatchId, cancellationToken);
        if (batch.Count == 0 || batch.All(x => x.ApprovalMessageId.HasValue)) return;
        if (batch.Any(x => x.ApprovalChannelId != batch[0].ApprovalChannelId || x.RequestedBy != batch[0].RequestedBy))
            throw new InvalidOperationException("Approval batches cannot span requesters or Discord channels.");

        var approvalChannel = GetApprovalChannel(batch[0].ApprovalChannelId
            ?? throw new InvalidOperationException("The approval batch has no Discord channel."));
        IUserMessage message;
        try
        {
            message = batch.Count == 1
                ? await approvalChannel.SendMessageAsync(
                    embed: ChangeApprovalPresenter.Embed(batch[0]),
                    components: ChangeApprovalPresenter.Components(batch[0]))
                : await approvalChannel.SendMessageAsync(
                    embed: ChangeApprovalPresenter.BatchEmbed(approvalBatchId, batch),
                    components: ChangeApprovalPresenter.BatchComponents(approvalBatchId));
        }
        catch (HttpException exception) when ((int)exception.HttpCode == 403)
        {
            throw new UnauthorizedAccessException(
                "Quorum cannot post the approval card in this channel. Grant View Channel, Send Messages, and Embed Links here, then retry.",
                exception);
        }

        await proposals.AttachApprovalMessageToBatchAsync(guildId, approvalBatchId, message.Id, cancellationToken);
        logger.LogInformation(
            "Quorum approval batch {BatchId} published with {ProposalCount} proposals to channel {ChannelId} as message {MessageId}.",
            ChangeApprovalPresenter.BatchDisplayId(approvalBatchId),
            batch.Count,
            batch[0].ApprovalChannelId,
            message.Id);
    }

    private IMessageChannel GetApprovalChannel(ulong approvalChannelId) =>
        discord.Client.GetChannel(approvalChannelId) as IMessageChannel
        ?? throw new InvalidOperationException("The invoking Discord channel cannot host a Quorum approval message.");
}

internal static class ChangeApprovalPresenter
{
    public static MessageComponent Components(ChangeProposal proposal) => new ComponentBuilder()
        .WithButton("Approve", $"quorum:approve:{proposal.Id:N}", ButtonStyle.Success)
        .WithButton("Reject", $"quorum:reject:{proposal.Id:N}", ButtonStyle.Danger)
        .WithButton("Details", $"quorum:details:{proposal.Id:N}", ButtonStyle.Secondary)
        .Build();

    public static MessageComponent BatchComponents(Guid approvalBatchId) => new ComponentBuilder()
        .WithButton("Approve all", $"quorum:approve-batch:{approvalBatchId:N}", ButtonStyle.Success)
        .WithButton("Reject all", $"quorum:reject-batch:{approvalBatchId:N}", ButtonStyle.Danger)
        .WithButton("Details", $"quorum:details-batch:{approvalBatchId:N}", ButtonStyle.Secondary)
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

    public static Embed BatchEmbed(Guid approvalBatchId, IReadOnlyList<ChangeProposal> proposals)
    {
        if (proposals.Count == 0) throw new ArgumentException("An approval batch must contain at least one proposal.", nameof(proposals));
        var ordered = proposals.OrderBy(x => x.RequestedAt).ThenBy(x => x.DisplayId).ToArray();
        var highestRisk = ordered.Max(x => x.Risk);
        var statusSummary = string.Join(", ", ordered
            .GroupBy(x => x.Status)
            .OrderBy(x => x.Key)
            .Select(x => $"{x.Count()} {x.Key}"));
        var builder = new EmbedBuilder()
            .WithTitle($"Quorum batch approval · {BatchDisplayId(approvalBatchId)}")
            .WithDescription($"Requested by <@{ordered[0].RequestedBy}> · One decision covers {ordered.Length} individually audited changes.")
            .WithColor(highestRisk switch
            {
                ChangeRisk.Low => Color.Blue,
                ChangeRisk.Medium => Color.Orange,
                ChangeRisk.High => Color.DarkOrange,
                ChangeRisk.Critical => Color.Red,
                _ => Color.LightGrey
            });

        for (var index = 0; index < ordered.Length; index++)
        {
            var proposal = ordered[index];
            var target = proposal.Change.DisplayTarget
                ?? (proposal.Change.ResourceId == 0 ? proposal.Change.ResourceType : $"<{proposal.Change.ResourceType}:{proposal.Change.ResourceId}>");
            var arguments = proposal.Change.Arguments is { Count: > 0 }
                ? string.Join(", ", proposal.Change.Arguments.Select(x => $"{x.Key}={(x.Key == "code" ? "[redacted]" : x.Value)}"))
                : "none";
            var value = $"Target: {target}\nChange: `{proposal.Change.Before}` → `{proposal.Change.After}`\nParameters: {arguments}\nPermission: {proposal.Change.RequiredDiscordPermission} · Risk: {proposal.Risk} · Status: {proposal.Status}";
            builder.AddField($"{index + 1}. {Humanize(proposal.Change.Action)} · {proposal.DisplayId}", value[..Math.Min(value.Length, 450)]);
        }

        return builder
            .AddField("Batch status", statusSummary, true)
            .AddField("Expires", $"<t:{ordered.Min(x => x.ExpiresAt).ToUnixTimeSeconds()}:R>", true)
            .WithFooter(ordered.All(x => x.Status == ChangeProposalStatus.PendingApproval)
                ? "No Discord change has been made. Approve all may partially complete if Discord rejects a later action."
                : "Each child status above is authoritative; completed changes are not rolled back automatically.")
            .WithCurrentTimestamp()
            .Build();
    }

    public static string Format(ChangeProposal proposal) =>
        $"**{proposal.DisplayId} · {proposal.Status}**\n{Humanize(proposal.Change.Action)} · {proposal.Change.DisplayTarget ?? proposal.Change.ResourceType}\n" +
        $"\u0060{proposal.Change.Before}\u0060 → \u0060{proposal.Change.After}\u0060\nNo unapproved change is executed.";

    public static string BatchDisplayId(Guid approvalBatchId) => "QBAT-" + approvalBatchId.ToString("N")[..8].ToUpperInvariant();

    private static string Humanize(ChangeActionType action) =>
        string.Concat(action.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
}
