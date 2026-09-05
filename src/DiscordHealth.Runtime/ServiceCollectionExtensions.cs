using DiscordHealth.Runtime.Commands;
using DiscordHealth.Runtime.DiscordAdapter;
using DiscordHealth.Runtime.ServerConfiguration;
using DiscordHealth.Runtime.Analysis;
using DiscordHealth.Runtime.Drift;
using DiscordHealth.Runtime.Persistence;
using DiscordHealth.Runtime.Tools;
using DiscordHealth.Runtime.Changes;
using DiscordHealth.Runtime.Agents;
using Microsoft.Extensions.Options;
using OpenAI;
using System.ClientModel;

namespace DiscordHealth.Runtime;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDiscordHealth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DiscordOptions>()
            .Bind(configuration.GetSection(DiscordOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Token), "Discord:Token is required")
            .ValidateOnStart();
        services.AddOptions<QuorumOptions>()
            .Bind(configuration.GetSection(QuorumOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<QuorumAgentOptions>()
            .Bind(configuration.GetSection(QuorumAgentOptions.SectionName))
            .Validate(value => !value.Enabled || value.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase), "Only the OpenAI provider is implemented.")
            .Validate(value => !value.Enabled || Uri.TryCreate(value.Endpoint, UriKind.Absolute, out _), "Agent:Endpoint must be an absolute URL.")
            .Validate(value => !value.Enabled || !string.IsNullOrWhiteSpace(value.ApiKey), "Agent:ApiKey is required when the agent is enabled.")
            .Validate(value => !value.Enabled || !string.IsNullOrWhiteSpace(value.Model), "Agent:Model is required when the agent is enabled.")
            .ValidateOnStart();

        services.AddSingleton<DiscordConnection>();
        services.AddSingleton<IDiscordClientAccessor>(provider => provider.GetRequiredService<DiscordConnection>());
        services.AddHostedService(provider => provider.GetRequiredService<DiscordConnection>());
        services.AddSingleton<IServerConfigurationReader, DiscordServerConfigurationReader>();
        services.AddSingleton<ISecurityAnalyzer, SecurityAnalyzer>();
        services.AddSingleton<IEffectivePermissionAnalyzer, EffectivePermissionAnalyzer>();
        services.AddSingleton<ISnapshotStore, FileSnapshotStore>();
        services.AddSingleton<ISnapshotDiffer, SnapshotDiffer>();
        services.AddSingleton<IQuorumReadTools, QuorumReadTools>();
        services.AddSingleton<IPermissionReadTools, PermissionReadTools>();
        services.AddSingleton<IChangeProposalStore, FileChangeProposalStore>();
        services.AddSingleton<IQuorumAuthorizationService, DiscordQuorumAuthorizationService>();
        services.AddSingleton<IApprovedChangeExecutor, DiscordApprovedChangeExecutor>();
        services.AddSingleton<IChangeProposalService, ChangeProposalService>();
        services.AddSingleton<IApprovalPublisher, DiscordApprovalPublisher>();
        services.AddSingleton<IQuorumAgentToolCatalog, QuorumAgentToolCatalog>();
        services.AddSingleton(provider =>
        {
            var agent = provider.GetRequiredService<IOptions<QuorumAgentOptions>>().Value;
            return new OpenAIClient(
                new ApiKeyCredential(string.IsNullOrWhiteSpace(agent.ApiKey) ? "not-configured" : agent.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(agent.Endpoint) });
        });
        services.AddSingleton<IQuorumAgent, QuorumAgent>();
        services.AddHostedService<DiscordInteractionHost>();
        return services;
    }
}
