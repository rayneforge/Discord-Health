namespace DiscordHealth.Runtime;

public sealed class QuorumOptions
{
    public const string SectionName = "Quorum";
    public string DataDirectory { get; init; } = "data";
    public WriteCapabilityOptions Writes { get; init; } = new();
}

public sealed class QuorumAgentOptions
{
    public const string SectionName = "Agent";
    public bool Enabled { get; init; }
    public string Provider { get; init; } = "OpenAI";
    public string Endpoint { get; init; } = "https://api.openai.com/v1";
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-4o-mini";
    public int MaxOutputTokens { get; init; } = 2000;
    public int ConversationTurns { get; init; } = 8;
}

public sealed class WriteCapabilityOptions
{
    public bool Enabled { get; init; }
    public int ApprovalTtlMinutes { get; init; } = 30;
    public bool AllowLowRiskSelfApproval { get; init; } = true;
}
