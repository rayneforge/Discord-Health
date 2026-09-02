using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;

namespace DiscordHealth.Runtime.DiscordAdapter;

internal interface IDiscordClientAccessor
{
    DiscordSocketClient Client { get; }
}

internal sealed class DiscordConnection : IDiscordClientAccessor, IHostedService
{
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordConnection> _logger;

    public DiscordConnection(IOptions<DiscordOptions> options, ILogger<DiscordConnection> logger)
    {
        _options = options.Value;
        _logger = logger;
        Client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
            AlwaysDownloadUsers = false
        });
        Client.Log += LogAsync;
    }

    public DiscordSocketClient Client { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Client.LoginAsync(TokenType.Bot, _options.Token);
        await Client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Client.StopAsync();
        await Client.LogoutAsync();
        Client.Dispose();
    }

    private Task LogAsync(LogMessage message)
    {
        _logger.Log(message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug
        }, message.Exception, "Discord: {Message}", message.Message);
        return Task.CompletedTask;
    }
}
