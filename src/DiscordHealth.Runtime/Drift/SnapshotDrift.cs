using System.Text.Json;
using DiscordHealth.Runtime.ServerConfiguration;

namespace DiscordHealth.Runtime.Drift;

public enum ConfigurationChangeType { Created, Deleted, Modified, CoverageChanged }
public sealed record ConfigurationChange(ConfigurationChangeType Type, string ResourceType, string ResourceId, string Summary, string? Before, string? After, FindingSeverity Severity);

public interface ISnapshotDiffer
{
    IReadOnlyList<ConfigurationChange> Compare(ServerConfigurationSnapshot before, ServerConfigurationSnapshot after);
}

internal sealed class SnapshotDiffer : ISnapshotDiffer
{
    public IReadOnlyList<ConfigurationChange> Compare(ServerConfigurationSnapshot before, ServerConfigurationSnapshot after)
    {
        if (before.GuildId != after.GuildId) throw new ArgumentException("Snapshots must belong to the same guild.");
        var changes = new List<ConfigurationChange>();
        CompareCoverage(before, after, changes);
        CompareSingleton("guild", before.Guild.Data, after.Guild.Data, changes, FindingSeverity.High);
        CompareResources("role", before.Roles.Data, after.Roles.Data, x => x.Id.ToString(), changes, x => x.SensitivePermissions.Count > 0 ? FindingSeverity.High : FindingSeverity.Notice);
        CompareResources("channel", before.Channels.Data, after.Channels.Data, x => x.Id.ToString(), changes, _ => FindingSeverity.Medium);
        CompareResources("automod", before.AutoModRules.Data, after.AutoModRules.Data, x => x.Id.ToString(), changes, _ => FindingSeverity.High);
        CompareResources("webhook", before.Webhooks.Data, after.Webhooks.Data, x => x.Id.ToString(), changes, _ => FindingSeverity.High);
        return changes;
    }

    private static void CompareCoverage(ServerConfigurationSnapshot before, ServerConfigurationSnapshot after, ICollection<ConfigurationChange> changes)
    {
        var left = Coverage(before);
        var right = Coverage(after);
        foreach (var key in left.Keys.Union(right.Keys))
            if (left.GetValueOrDefault(key) != right.GetValueOrDefault(key))
                changes.Add(new(ConfigurationChangeType.CoverageChanged, "collector", key, $"Collector changed from {left.GetValueOrDefault(key)} to {right.GetValueOrDefault(key)}.", left.GetValueOrDefault(key).ToString(), right.GetValueOrDefault(key).ToString(), FindingSeverity.Notice));
    }

    private static Dictionary<string, CollectorStatus> Coverage(ServerConfigurationSnapshot x) => new()
    {
        ["guild"] = x.Guild.Status, ["roles"] = x.Roles.Status, ["channels"] = x.Channels.Status,
        ["bans"] = x.Bans.Status, ["invites"] = x.Invites.Status, ["integrations"] = x.Integrations.Status,
        ["webhooks"] = x.Webhooks.Status, ["automod"] = x.AutoModRules.Status, ["audit"] = x.AuditLog.Status,
        ["onboarding"] = x.Onboarding.Status, ["welcome"] = x.WelcomeScreen.Status
    };

    private static void CompareSingleton<T>(string type, T? before, T? after, ICollection<ConfigurationChange> changes, FindingSeverity severity)
    {
        var left = Serialize(before); var right = Serialize(after);
        if (left != right) changes.Add(new(ConfigurationChangeType.Modified, type, type, $"{type} configuration changed.", left, right, severity));
    }

    private static void CompareResources<T>(string type, IReadOnlyList<T>? before, IReadOnlyList<T>? after, Func<T, string> id, ICollection<ConfigurationChange> changes, Func<T, FindingSeverity> severity)
    {
        if (before is null || after is null) return;
        var left = before.ToDictionary(id); var right = after.ToDictionary(id);
        foreach (var key in left.Keys.Except(right.Keys)) changes.Add(new(ConfigurationChangeType.Deleted, type, key, $"{type} deleted.", Serialize(left[key]), null, severity(left[key])));
        foreach (var key in right.Keys.Except(left.Keys)) changes.Add(new(ConfigurationChangeType.Created, type, key, $"{type} created.", null, Serialize(right[key]), severity(right[key])));
        foreach (var key in left.Keys.Intersect(right.Keys))
        {
            var oldJson = Serialize(left[key]); var newJson = Serialize(right[key]);
            if (oldJson != newJson) changes.Add(new(ConfigurationChangeType.Modified, type, key, $"{type} changed.", oldJson, newJson, severity(right[key])));
        }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
