namespace DiscordHealth.Runtime.Changes;

public interface IChangeProposalStore
{
    Task SaveAsync(ChangeProposal proposal, CancellationToken cancellationToken = default);
    Task<ChangeProposal?> GetAsync(ulong guildId, Guid proposalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChangeProposal>> ListAsync(ulong guildId, CancellationToken cancellationToken = default);
}
