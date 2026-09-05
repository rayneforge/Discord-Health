using System.Text;
using System.Text.Json;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordHealth.Runtime.DiscordAdapter;
using DiscordHealth.Runtime.ServerConfiguration;
using DiscordHealth.Runtime.Tools;
using DiscordHealth.Runtime.Agents;
using Microsoft.Extensions.Options;

namespace DiscordHealth.Runtime.Commands;

internal sealed class DiscordInteractionHost : IHostedService
{
    private readonly IDiscordClientAccessor _accessor;
    private readonly IServiceProvider _services;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordInteractionHost> _logger;
    private readonly InteractionService _interactions;

    public DiscordInteractionHost(IDiscordClientAccessor accessor, IServiceProvider services, IOptions<DiscordOptions> options, ILogger<DiscordInteractionHost> logger)
    {
        _accessor = accessor;
        _services = services;
        _options = options.Value;
        _logger = logger;
        _interactions = new InteractionService(accessor.Client);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _interactions.AddModuleAsync<ServerConfigurationCommand>(_services);
        await _interactions.AddModuleAsync<QuorumAgentCommand>(_services);
        await _interactions.AddModuleAsync<ChangeApprovalCommand>(_services);
        _accessor.Client.InteractionCreated += ExecuteAsync;
        _accessor.Client.Ready += RegisterAsync;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _accessor.Client.InteractionCreated -= ExecuteAsync;
        _accessor.Client.Ready -= RegisterAsync;
        _interactions.Dispose();
        return Task.CompletedTask;
    }

    private async Task RegisterAsync()
    {
        if (_options.GuildId is { } guildId)
            await _interactions.RegisterCommandsToGuildAsync(guildId);
        else
            await _interactions.RegisterCommandsGloballyAsync();
    }

    private async Task ExecuteAsync(SocketInteraction interaction)
    {
        try
        {
            var result = await _interactions.ExecuteCommandAsync(new SocketInteractionContext(_accessor.Client, interaction), _services);
            if (result.IsSuccess) return;
            _logger.LogWarning("Discord interaction failed: {Reason}", result.ErrorReason);
            await CompleteFailureAsync(interaction, result.ErrorReason);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Discord interaction threw after dispatch.");
            await CompleteFailureAsync(interaction, exception.Message);
        }
    }

    private static async Task CompleteFailureAsync(SocketInteraction interaction, string reason)
    {
        var message = $"Quorum could not complete that request: {reason}";
        try
        {
            if (interaction.HasResponded) await interaction.FollowupAsync(message[..Math.Min(message.Length, 1900)], ephemeral: true);
            else await interaction.RespondAsync(message[..Math.Min(message.Length, 1900)], ephemeral: true);
        }
        catch
        {
            // Discord may have expired the interaction; the original exception is already logged.
        }
    }
}

public sealed class ServerConfigurationCommand(IQuorumReadTools tools) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("server-config", "Ask Quorum to export the server configuration (read-only)")]
    [RequireContext(ContextType.Guild)]
    [RequireUserPermission(GuildPermission.ViewAuditLog)]
    [DefaultMemberPermissions(GuildPermission.ViewAuditLog)]
    public async Task ExportAsync()
    {
        await DeferAsync(ephemeral: true);
        try
        {
            var review = await tools.ScanAsync(Context.Guild.Id);
            var snapshot = review.Snapshot;
            var json = JsonSerializer.Serialize(review, new JsonSerializerOptions { WriteIndented = true });
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await FollowupWithFileAsync(stream, "server-review.json", $"Quorum captured **{snapshot.Name}** with {snapshot.CoveragePercent:F1}% collector coverage, {snapshot.Findings.Count} findings, and {review.ChangesFromPrevious.Count} changes from the previous snapshot.", ephemeral: true);
        }
        catch (Exception exception)
        {
            await FollowupAsync($"Quorum could not capture the server configuration: {exception.Message}", ephemeral: true);
        }
    }

    [SlashCommand("quorum-findings", "Show Quorum's current security and visibility findings")]
    [RequireContext(ContextType.Guild)]
    [RequireUserPermission(GuildPermission.ViewAuditLog)]
    [DefaultMemberPermissions(GuildPermission.ViewAuditLog)]
    public async Task FindingsAsync()
    {
        await DeferAsync(ephemeral: true);
        try
        {
            var findings = await tools.ListFindingsAsync(Context.Guild.Id);
            if (findings.Count == 0)
            {
                await FollowupAsync("Quorum has no findings in the latest snapshot.", ephemeral: true);
                return;
            }

            var lines = findings.Take(15).Select(finding => $"**{finding.Severity} · {finding.Id} · {finding.Status}** — {finding.Title}");
            var content = $"**QUORUM FINDINGS**\n\n{string.Join("\n", lines)}";
            if (findings.Count > 15) content += $"\n\n…and {findings.Count - 15} more. Export `/server-config` for complete evidence.";
            await FollowupAsync(content[..Math.Min(content.Length, 1950)], ephemeral: true);
        }
        catch (Exception exception)
        {
            await FollowupAsync($"Quorum could not load findings: {exception.Message}", ephemeral: true);
        }
    }
}

public sealed class QuorumAgentCommand(IQuorumAgent agent) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("quorum", "Talk to Quorum about this server")]
    [RequireContext(ContextType.Guild)]
    [DefaultMemberPermissions(GuildPermission.ViewChannel)]
    public async Task AskAsync([Summary("message", "Question or administration request for Quorum")] string message)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            var response = await agent.RunAsync(new QuorumAgentRequest(
                Context.Guild.Id,
                Context.Channel.Id,
                Context.User.Id,
                Context.User.GlobalName ?? Context.User.Username,
                message));
            foreach (var part in Split(response, 1900))
                await FollowupAsync(part, ephemeral: true);
        }
        catch (Exception exception)
        {
            await FollowupAsync($"Quorum's agent could not answer: {exception.Message}", ephemeral: true);
        }
    }

    private static IEnumerable<string> Split(string value, int length)
    {
        for (var offset = 0; offset < value.Length; offset += length)
            yield return value.Substring(offset, Math.Min(length, value.Length - offset));
    }
}
