namespace DiscordHealth.Runtime.Changes;

public interface IApprovedChangeExecutor
{
    Task<ChangeSpecification> CreateSpecificationAsync(ulong guildId, ChangeRequest request, CancellationToken cancellationToken = default);
    Task<string> ObserveAsync(ulong guildId, ChangeSpecification change, CancellationToken cancellationToken = default);
    Task ExecuteAsync(ulong guildId, ChangeSpecification change, CancellationToken cancellationToken = default);
}
