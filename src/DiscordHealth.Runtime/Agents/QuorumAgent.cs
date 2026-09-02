using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

namespace DiscordHealth.Runtime.Agents;

public sealed record QuorumAgentRequest(
    ulong GuildId,
    ulong ChannelId,
    ulong RequesterId,
    string RequesterName,
    string Message);

public interface IQuorumAgent
{
    Task<string> RunAsync(QuorumAgentRequest request, CancellationToken cancellationToken = default);
}

internal sealed class QuorumAgent(
    OpenAIClient openAi,
    IQuorumAgentToolCatalog toolCatalog,
    IOptions<QuorumAgentOptions> options,
    ILogger<QuorumAgent> logger) : IQuorumAgent
{
    internal const string SystemPrompt = """
        You are Quorum, a conversational Discord administration and security agent.

        Talk naturally and directly. Your operational knowledge comes from tools scoped to the Discord
        server and requester that invoked you. Never ask for or invent a server ID, requester ID, role ID,
        or channel ID when tool results can provide it. Use a fresh scan when the user asks about current
        state; otherwise use the latest snapshot. Clearly distinguish observed facts, deterministic
        findings, inferences, missing collector coverage, permission failures, and unsupported capabilities.
        If a tool returns Success=false, state its exact error category and message. Never replace a tool
        failure with a vague statement or ask the user for focus areas when their request was already clear.
        Never say that a tool was attempted, succeeded, or failed unless you received that tool's result in
        the current run. For role and channel names, use find_server_resources before an ID-based tool.

        Read tools may execute immediately. A write-shaped tool may only create a durable approval request.
        It does not make the Discord change. Never claim a proposed change was applied. Explain that it is
        pending approval and name the proposal ID. Only invoke a write-shaped tool after the user clearly
        specifies the desired change and target. If no matching write tool exists, state the capability gap.
        Never substitute a different mutation just because it has an available tool.
        Do not announce, preview, or promise a proposal before the matching tool returns Success=true.
        A proposed resource does not have a Discord ID until its approval executes. For dependent work,
        use a supported exact-name selector or explain that the prerequisite proposal must be approved first.

        Keep Discord responses compact. Use Discord mentions such as <#channel-id> and <@&role-id> when IDs
        are available. Do not dump raw JSON unless the user asks for it; summarize tool evidence and gaps.
        """;

    private readonly QuorumAgentOptions _options = options.Value;
    private readonly ConcurrentDictionary<ConversationKey, ConversationState> _conversations = new();

    public async Task<string> RunAsync(QuorumAgentRequest request, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Quorum's conversational agent is disabled. Configure Agent with Provider=OpenAI, an API key, and a model, then enable it.");

        var key = new ConversationKey(request.GuildId, request.ChannelId, request.RequesterId);
        var state = _conversations.GetOrAdd(key, _ => new ConversationState());
        await state.Gate.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var tools = toolCatalog.GetTools(request.GuildId, request.RequesterId, request.ChannelId).ToList();
            logger.LogInformation(
                "Quorum agent invocation started for guild {GuildId}, channel {ChannelId}, requester {RequesterId}; provider {Provider}, model {Model}, tools {ToolCount}, request characters {RequestLength}, prior turns {PriorTurns}.",
                request.GuildId,
                request.ChannelId,
                request.RequesterId,
                _options.Provider,
                _options.Model,
                tools.Count,
                request.Message.Length,
                state.Turns.Count);
            var agent = openAi
                .GetResponsesClient()
                .AsAIAgent(
                    model: _options.Model,
                    instructions: SystemPrompt,
                    name: "Quorum",
                    description: "Conversational Discord administration and security agent.",
                    tools: tools);

            var prompt = BuildPrompt(state.Turns, request);
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                MaxOutputTokens = _options.MaxOutputTokens,
                Temperature = 0.2f
            });
            var response = await agent.RunAsync(prompt, session: null, options: runOptions, cancellationToken: cancellationToken);
            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("The model returned an empty response.");

            logger.LogInformation(
                "Quorum agent invocation completed in {ElapsedMs} ms with {ResponseLength} response characters.",
                stopwatch.ElapsedMilliseconds,
                text.Length);

            state.Turns.Add(new ConversationTurn(request.RequesterName, request.Message, text));
            var limit = Math.Max(1, _options.ConversationTurns);
            if (state.Turns.Count > limit) state.Turns.RemoveRange(0, state.Turns.Count - limit);
            return text;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Quorum agent invocation failed after {ElapsedMs} ms for guild {GuildId}, channel {ChannelId}, provider {Provider}, model {Model}.",
                stopwatch.ElapsedMilliseconds,
                request.GuildId,
                request.ChannelId,
                _options.Provider,
                _options.Model);
            throw;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static string BuildPrompt(IReadOnlyList<ConversationTurn> turns, QuorumAgentRequest request)
    {
        var builder = new StringBuilder();
        if (turns.Count > 0)
        {
            builder.AppendLine("Recent conversation with this requester in this channel:");
            foreach (var turn in turns)
            {
                builder.Append(turn.RequesterName).Append(": ").AppendLine(turn.UserMessage);
                builder.Append("Quorum: ").AppendLine(turn.AgentMessage);
            }
            builder.AppendLine();
        }
        builder.Append(request.RequesterName).Append(": ").Append(request.Message);
        return builder.ToString();
    }

    private readonly record struct ConversationKey(ulong GuildId, ulong ChannelId, ulong RequesterId);
    private sealed record ConversationTurn(string RequesterName, string UserMessage, string AgentMessage);
    private sealed class ConversationState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<ConversationTurn> Turns { get; } = [];
    }
}
