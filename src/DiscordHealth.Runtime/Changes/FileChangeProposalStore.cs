using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DiscordHealth.Runtime.Changes;

internal sealed class FileChangeProposalStore(IOptions<QuorumOptions> options) : IChangeProposalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.GetFullPath(options.Value.DataDirectory);

    public async Task SaveAsync(ChangeProposal proposal, CancellationToken cancellationToken = default)
    {
        var directory = DirectoryFor(proposal.GuildId);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, proposal.Id.ToString("N") + ".json");
        var temporaryPath = finalPath + ".tmp";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, proposal, JsonOptions, cancellationToken);
            File.Move(temporaryPath, finalPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    public async Task<ChangeProposal?> GetAsync(ulong guildId, Guid proposalId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(DirectoryFor(guildId), proposalId.ToString("N") + ".json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ChangeProposal>(stream, JsonOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<ChangeProposal>> ListAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var directory = DirectoryFor(guildId);
        if (!Directory.Exists(directory)) return [];
        var proposals = new List<ChangeProposal>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            await using var stream = File.OpenRead(path);
            if (await JsonSerializer.DeserializeAsync<ChangeProposal>(stream, JsonOptions, cancellationToken) is { } proposal) proposals.Add(proposal);
        }
        return proposals.OrderByDescending(x => x.RequestedAt).ToArray();
    }

    private string DirectoryFor(ulong guildId) => Path.Combine(_root, "proposals", guildId.ToString());
}
