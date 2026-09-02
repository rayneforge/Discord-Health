namespace DiscordHealth.Runtime.ServerConfiguration;

public interface IServerConfigurationReader
{
    Task<ServerConfigurationSnapshot> CaptureAsync(ulong guildId, CancellationToken cancellationToken = default);
}
