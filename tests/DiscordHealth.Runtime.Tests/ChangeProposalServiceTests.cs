using DiscordHealth.Runtime;
using DiscordHealth.Runtime.Changes;
using Microsoft.Extensions.Options;
using Xunit;

namespace DiscordHealth.Runtime.Tests;

public sealed class ChangeProposalServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "quorum-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Proposal_is_pending_in_invoking_channel_without_execution()
    {
        var executor = new FakeExecutor("0");
        var service = CreateService(executor);

        var proposal = await service.ProposeAsync(1, 100, 333, SlowMode(2, 10));

        Assert.Equal(ChangeProposalStatus.PendingApproval, proposal.Status);
        Assert.Equal((ulong)333, proposal.ApprovalChannelId);
        Assert.Equal(0, executor.ExecuteCount);
    }

    [Fact]
    public async Task Approved_change_is_compared_executed_and_verified()
    {
        var executor = new FakeExecutor("0");
        var service = CreateService(executor);
        var proposal = await service.ProposeAsync(1, 100, 3, SlowMode(2, 10));

        var result = await service.ApproveAsync(1, proposal.Id, 100);

        Assert.Equal(ChangeProposalStatus.Completed, result.Status);
        Assert.Equal("10", result.VerificationValue);
        Assert.Equal(1, executor.ExecuteCount);
        Assert.Single(result.Approvals);
    }

    [Fact]
    public async Task Changed_precondition_marks_proposal_stale_without_execution()
    {
        var executor = new FakeExecutor("0");
        var service = CreateService(executor);
        var proposal = await service.ProposeAsync(1, 100, 3, SlowMode(2, 10));
        executor.Value = "5";

        var result = await service.ApproveAsync(1, proposal.Id, 100);

        Assert.Equal(ChangeProposalStatus.Stale, result.Status);
        Assert.Equal(0, executor.ExecuteCount);
    }

    [Fact]
    public async Task Writes_disabled_rejects_proposal()
    {
        var executor = new FakeExecutor("0");
        var service = CreateService(executor, enabled: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProposeAsync(1, 100, 3, SlowMode(2, 10)));
    }

    private ChangeProposalService CreateService(FakeExecutor executor, bool enabled = true)
    {
        var options = Options.Create(new QuorumOptions
        {
            DataDirectory = _directory,
            Writes = new WriteCapabilityOptions { Enabled = enabled, AllowLowRiskSelfApproval = true }
        });
        return new ChangeProposalService(options, new FileChangeProposalStore(options), executor);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static ChangeRequest SlowMode(ulong channelId, int seconds) =>
        new(ChangeActionType.ChangeChannelSlowMode, channelId, new Dictionary<string, string> { ["seconds"] = seconds.ToString() });

    private sealed class FakeExecutor(string value) : IApprovedChangeExecutor
    {
        public string Value { get; set; } = value;
        public int ExecuteCount { get; private set; }
        public Task<ChangeSpecification> CreateSpecificationAsync(ulong guildId, ChangeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChangeSpecification(ChangeActionType.ChangeChannelSlowMode, "channel", request.ResourceId, "slow_mode_seconds", Value, request.Arguments["seconds"], "MANAGE_CHANNELS"));
        public Task<string> ObserveAsync(ulong guildId, ChangeSpecification change, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task ExecuteAsync(ulong guildId, ChangeSpecification change, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            Value = change.After;
            return Task.CompletedTask;
        }
    }
}
