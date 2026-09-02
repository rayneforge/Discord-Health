using DiscordHealth.Runtime.Agents;
using DiscordHealth.Runtime.Changes;
using DiscordHealth.Runtime.ServerConfiguration;
using DiscordHealth.Runtime.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DiscordHealth.Runtime.Tests;

public sealed class QuorumAgentToolCatalogTests
{
    [Fact]
    public void Tool_schemas_do_not_allow_the_model_to_select_guild_or_requester()
    {
        var catalog = CreateCatalog();

        var tools = catalog.GetTools(123, 456, 789).OfType<AIFunction>().ToArray();

        Assert.Contains(tools, x => x.Name == "scan_server_configuration");
        Assert.Contains(tools, x => x.Name == "inspect_server_configuration");
        Assert.Contains(tools, x => x.Name == "propose_channel_slowmode");
        Assert.Contains(tools, x => x.Name == "propose_create_text_channel");
        Assert.Contains(tools, x => x.Name == "propose_ban_member");
        Assert.Contains(tools, x => x.Name == "propose_create_automod_keyword_rule");
        Assert.All(tools, tool =>
        {
            var schema = tool.JsonSchema.ToString();
            Assert.DoesNotContain("guildId", schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("requesterId", schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("approvalChannelId", schema, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Write_tool_explicitly_declares_approval_only_behavior()
    {
        var catalog = CreateCatalog();

        var tool = Assert.Single(catalog.GetTools(123, 456, 789).OfType<AIFunction>(), x => x.Name == "propose_channel_slowmode");

        Assert.Contains("never directly changes Discord", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static QuorumAgentToolCatalog CreateCatalog() =>
        new(new StubReads(), new StubPermissions(), new StubApprovals(), NullLogger<QuorumAgentToolCatalog>.Instance);

    private sealed class StubReads : IQuorumReadTools
    {
        public Task<ServerReview> ScanAsync(ulong guildId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServerConfigurationSnapshot> GetLatestAsync(ulong guildId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SecurityFinding>> ListFindingsAsync(ulong guildId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubPermissions : IPermissionReadTools
    {
        public Task<DiscordHealth.Runtime.Analysis.PermissionExplanation> ExplainRolePermissionAsync(ulong guildId, ulong roleId, ulong channelId, string permission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubApprovals : IApprovalPublisher
    {
        public Task<ChangeProposal> ProposeAsync(ulong guildId, ulong requesterId, ulong approvalChannelId, ChangeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
