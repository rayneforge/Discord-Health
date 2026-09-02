using DiscordHealth.Runtime.ServerConfiguration;
using DiscordHealth.Runtime.DiscordAdapter;
using Discord.WebSocket;
using System.Reflection;
using Xunit;

namespace DiscordHealth.Runtime.Tests;

public sealed class ReadOnlyBoundaryTests
{
    [Fact]
    public void Configuration_reader_exposes_only_capture()
    {
        var methods = typeof(IServerConfigurationReader).GetMethods();
        var method = Assert.Single(methods);
        Assert.Equal("CaptureAsync", method.Name);
    }

    [Fact]
    public void Unavailable_collector_keeps_reason_and_required_permission()
    {
        var section = CollectorResult<IReadOnlyList<string>>.Unavailable(
            CollectorStatus.PermissionDenied,
            "missing permission",
            "MANAGE_GUILD",
            DateTimeOffset.UtcNow);
        Assert.False(section.IsAvailable);
        Assert.Null(section.Data);
        Assert.Equal("missing permission", section.Reason);
        Assert.Equal("MANAGE_GUILD", section.RequiredPermission);
        Assert.Equal(CollectorStatus.PermissionDenied, section.Status);
    }

    [Fact]
    public void Approved_thread_changes_require_a_guild_scoped_lookup()
    {
        var resolver = typeof(DiscordApprovedChangeExecutor).GetMethod(
            "GetThread",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(resolver);
        Assert.Equal(
            [typeof(SocketGuild), typeof(ulong)],
            resolver.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }
}
