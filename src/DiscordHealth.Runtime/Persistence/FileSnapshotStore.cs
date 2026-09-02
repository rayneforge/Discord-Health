using System.Text.Json;
using Microsoft.Extensions.Options;
using DiscordHealth.Runtime.ServerConfiguration;

namespace DiscordHealth.Runtime.Persistence;

internal sealed class FileSnapshotStore(IOptions<QuorumOptions> options) : ISnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.GetFullPath(options.Value.DataDirectory);

    public async Task SaveAsync(ServerConfigurationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var directory = GuildDirectory(snapshot.GuildId);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, $"{snapshot.CapturedAt:yyyyMMddTHHmmssfffffffZ}-{snapshot.SnapshotId:N}.json");
        var temporaryPath = finalPath + ".tmp";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            File.Move(temporaryPath, finalPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    public async Task<ServerConfigurationSnapshot?> GetLatestAsync(ulong guildId, Guid? excludingSnapshotId = null, CancellationToken cancellationToken = default)
    {
        var directory = GuildDirectory(guildId);
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(x => x, StringComparer.Ordinal))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var snapshot = await JsonSerializer.DeserializeAsync<ServerConfigurationSnapshot>(stream, JsonOptions, cancellationToken);
                if (snapshot is not null && snapshot.SnapshotId != excludingSnapshotId) return snapshot;
            }
            catch (JsonException)
            {
                // Snapshots are immutable. Skip records written by an incompatible older schema.
            }
            catch (NotSupportedException)
            {
                // A newer/unsupported record must not wedge every read command.
            }
        }
        return null;
    }

    private string GuildDirectory(ulong guildId) => Path.Combine(_root, "snapshots", guildId.ToString());
}
