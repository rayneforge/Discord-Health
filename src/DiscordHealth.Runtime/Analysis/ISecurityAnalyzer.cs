using DiscordHealth.Runtime.ServerConfiguration;

namespace DiscordHealth.Runtime.Analysis;

public interface ISecurityAnalyzer
{
    IReadOnlyList<SecurityFinding> Analyze(ServerConfigurationSnapshot snapshot);
}
