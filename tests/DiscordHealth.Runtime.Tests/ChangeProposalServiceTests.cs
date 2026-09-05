using DiscordHealth.Runtime;
using DiscordHealth.Runtime.Changes;
using DiscordHealth.Runtime.DiscordAdapter;
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

    [Fact]
    public async Task Requester_authorization_is_rechecked_before_execution()
    {
        var executor = new FakeExecutor("0");
        var authorization = new FakeAuthorization();
        var service = CreateService(executor, authorization: authorization);
        var proposal = await service.ProposeAsync(1, 100, 3, SlowMode(2, 10));
        authorization.AllowChanges = false;

        var result = await service.ApproveAsync(1, proposal.Id, 999);

        Assert.Equal(ChangeProposalStatus.Failed, result.Status);
        Assert.Contains("could not be revalidated", result.StatusReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, executor.ExecuteCount);
        Assert.Equal(2, authorization.ChangeChecks);
    }

    [Fact]
    public async Task Non_administrator_cannot_approve_a_proposal_through_the_service()
    {
        var executor = new FakeExecutor("0");
        var authorization = new FakeAuthorization { AllowAdministrator = false };
        var service = CreateService(executor, authorization: authorization);
        var proposal = await service.ProposeAsync(1, 100, 3, SlowMode(2, 10));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApproveAsync(1, proposal.Id, 777));

        Assert.Equal(0, executor.ExecuteCount);
        Assert.Equal(ChangeProposalStatus.PendingApproval, (await service.GetAsync(1, proposal.Id))!.Status);
    }

    [Fact]
    public async Task One_batch_approval_executes_each_child_and_preserves_individual_results()
    {
        var executor = new ResourceFakeExecutor();
        var service = CreateService(executor);
        var batchId = Guid.NewGuid();
        var first = await service.ProposeAsync(1, 100, 333, SlowMode(2, 10), batchId);
        var second = await service.ProposeAsync(1, 100, 333, SlowMode(3, 20), batchId);

        var attached = await service.AttachApprovalMessageToBatchAsync(1, batchId, 444);
        var results = await service.ApproveBatchAsync(1, batchId, 999);

        Assert.Equal(2, attached.Count);
        Assert.All(attached, x => Assert.Equal((ulong)444, x.ApprovalMessageId));
        Assert.All(results, x => Assert.Equal(batchId, x.ApprovalBatchId));
        Assert.All(results, x => Assert.Equal(ChangeProposalStatus.Completed, x.Status));
        Assert.Equal(2, executor.ExecuteCount);
        Assert.Equal("10", executor.Values[first.Change.ResourceId]);
        Assert.Equal("20", executor.Values[second.Change.ResourceId]);
    }

    private ChangeProposalService CreateService(IApprovedChangeExecutor executor, bool enabled = true, FakeAuthorization? authorization = null)
    {
        var options = Options.Create(new QuorumOptions
        {
            DataDirectory = _directory,
            Writes = new WriteCapabilityOptions { Enabled = enabled, AllowLowRiskSelfApproval = true }
        });
        return new ChangeProposalService(options, new FileChangeProposalStore(options), executor, authorization ?? new FakeAuthorization());
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

    private sealed class FakeAuthorization : IQuorumAuthorizationService
    {
        public bool AllowChanges { get; set; } = true;
        public bool AllowAdministrator { get; set; } = true;
        public int ChangeChecks { get; private set; }

        public Task DemandReadAsync(ulong guildId, ulong requesterId, QuorumReadCapability capability, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DemandResourceLookupAsync(ulong guildId, ulong requesterId, string resourceType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DemandAdministratorAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default) =>
            AllowAdministrator
                ? Task.CompletedTask
                : Task.FromException(new UnauthorizedAccessException("Administrator is required."));
        public Task DemandChangeAsync(ulong guildId, ulong requesterId, ChangeRequest request, CancellationToken cancellationToken = default)
        {
            ChangeChecks++;
            return AllowChanges
                ? Task.CompletedTask
                : Task.FromException(new UnauthorizedAccessException("Requester permission was revoked."));
        }
    }

    private sealed class ResourceFakeExecutor : IApprovedChangeExecutor
    {
        public Dictionary<ulong, string> Values { get; } = [];
        public int ExecuteCount { get; private set; }

        public Task<ChangeSpecification> CreateSpecificationAsync(ulong guildId, ChangeRequest request, CancellationToken cancellationToken = default)
        {
            var before = Values.GetValueOrDefault(request.ResourceId, "0");
            return Task.FromResult(new ChangeSpecification(
                request.Action,
                "channel",
                request.ResourceId,
                "slow_mode_seconds",
                before,
                request.Arguments["seconds"],
                "MANAGE_CHANNELS",
                Arguments: request.Arguments));
        }

        public Task<string> ObserveAsync(ulong guildId, ChangeSpecification change, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(change.ResourceId, "0"));

        public Task ExecuteAsync(ulong guildId, ChangeSpecification change, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            Values[change.ResourceId] = change.After;
            return Task.CompletedTask;
        }
    }
}
