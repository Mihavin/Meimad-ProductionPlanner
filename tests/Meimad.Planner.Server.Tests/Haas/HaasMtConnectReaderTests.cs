using Meimad.Planner.Server.Domain.Haas;
using Meimad.Planner.Server.Infrastructure.Haas;
using Meimad.Planner.Server.Infrastructure.MtConnect;
using Meimad.Planner.Server.Tests.MtConnect;

namespace Meimad.Planner.Server.Tests.Haas;

public sealed class HaasMtConnectReaderTests
{
    [Fact]
    [Trait("Category", "LiveCommissioning")]
    public async Task Optional_live_agent_commissioning_check_uses_the_same_reader_as_Server_polling()
    {
        var configured = Environment.GetEnvironmentVariable("MEIMAD_MTCONNECT_LIVE_URL");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var address = new Uri(configured, UriKind.Absolute);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var reader = new HaasMtConnectReader(new MtConnectHttpClient(http), TimeProvider.System);

        var result = await reader.ReadAsync(address.Host, address.Port, 5000,
            10605, HaasPartCounterSources.M30Counter1);

        Assert.Equal("AVAILABLE", result.Availability);
        Assert.False(string.IsNullOrWhiteSpace(result.DeviceName));
        Assert.False(string.IsNullOrWhiteSpace(result.MachineStatus));
        Assert.NotNull(result.Parts);
    }

    [Fact]
    public async Task Reader_normalizes_live_Haas_items_and_keeps_diagnostics_compact()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var current = MtConnectDocumentParser.ParseCurrent(MtConnectTestDocuments.Current, probe);
        var reader = new HaasMtConnectReader(new StubClient(probe, current), TimeProvider.System);

        var result = await reader.ReadAsync(
            "192.168.0.56", 8082, 3000, 10605, HaasPartCounterSources.M30Counter1);

        Assert.Equal("VF-3SS", result.DeviceName);
        Assert.Equal("AVAILABLE", result.Availability);
        Assert.Equal("ACTIVE", result.MachineStatus);
        Assert.Equal("AUTOMATIC", result.ControllerMode);
        Assert.Equal("1500.CNC", result.ProgramNumber);
        Assert.Equal(9300, result.Parts);
        Assert.Equal(1, result.ProductionVariableValue);
        Assert.Null(result.ProductionVariableError);
        Assert.Equal(12000m, result.SpindleRpm);
        Assert.Equal(800.5m, result.FeedRate);
        Assert.Equal(1, result.ActiveAlarmCount);
        Assert.True(result.DiagnosticPayload.Length < 4096);
        Assert.DoesNotContain("MacroRange", result.DiagnosticPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Q500_compatibility_source_prefers_standard_PartCount_then_M30_counter()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var current = MtConnectDocumentParser.ParseCurrent(MtConnectTestDocuments.Current, probe);
        var reader = new HaasMtConnectReader(new StubClient(probe, current), TimeProvider.System);

        var result = await reader.ReadAsync(
            "192.168.0.56", 8082, 3000, 10605, HaasPartCounterSources.Q500);

        Assert.Equal(42, result.Parts);
    }

    [Fact]
    public async Task Q500_compatibility_falls_back_when_standard_PartCount_is_unavailable()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var xml = MtConnectTestDocuments.Current.Replace(
            ">42</m:PartCount>", ">UNAVAILABLE</m:PartCount>", StringComparison.Ordinal);
        var current = MtConnectDocumentParser.ParseCurrent(xml, probe);
        var reader = new HaasMtConnectReader(new StubClient(probe, current), TimeProvider.System);

        var result = await reader.ReadAsync(
            "192.168.0.56", 8082, 3000, 10605, HaasPartCounterSources.Q500);

        Assert.Equal(9300, result.Parts);
    }

    [Fact]
    public async Task Reader_counts_simultaneous_conditions_that_share_one_data_item()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var xml = MtConnectTestDocuments.Current.Replace(
            "</m:Condition>",
            """
                  <m:Fault dataItemId="mcond" name="MachineCondition" sequence="8335"
                           timestamp="2026-08-23T15:19:35.000Z"
                           nativeCode="120" nativeSeverity="HIGH">Low air pressure</m:Fault>
                </m:Condition>
            """,
            StringComparison.Ordinal);
        var current = MtConnectDocumentParser.ParseCurrent(xml, probe);
        var reader = new HaasMtConnectReader(new StubClient(probe, current), TimeProvider.System);

        var result = await reader.ReadAsync(
            "192.168.0.56", 8082, 3000, 10605, HaasPartCounterSources.M30Counter1);

        Assert.Equal(2, result.ActiveAlarmCount);
    }

    [Fact]
    public async Task Reader_uses_server_receipt_time_but_retains_agent_time_in_diagnostics()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var current = MtConnectDocumentParser.ParseCurrent(MtConnectTestDocuments.Current, probe);
        var receivedAt = new DateTimeOffset(2026, 8, 23, 18, 30, 0, TimeSpan.Zero);
        var reader = new HaasMtConnectReader(
            new StubClient(probe, current), new FixedTimeProvider(receivedAt));

        var result = await reader.ReadAsync(
            "192.168.0.56", 8082, 3000, 10605, HaasPartCounterSources.M30Counter1);

        Assert.Equal(receivedAt, result.ReadAt);
        Assert.Contains("2026-08-23T17:23:01.481", result.DiagnosticPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_rejects_ambiguous_multi_machine_agent_instead_of_guessing()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var parsed = MtConnectDocumentParser.ParseCurrent(MtConnectTestDocuments.Current, probe);
        var current = parsed with { Devices = [parsed.Devices[0], parsed.Devices[0]] };
        var reader = new HaasMtConnectReader(new StubClient(probe, current), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<MtConnectProtocolException>(() => reader.ReadAsync(
            "192.168.0.56", 8082, 3000, 10605, HaasPartCounterSources.Q500));

        Assert.Contains("more than one machine", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Slow_probe_for_one_agent_does_not_block_another_agent()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var current = MtConnectDocumentParser.ParseCurrent(MtConnectTestDocuments.Current, probe);
        var client = new IsolatedProbeClient(probe, current);
        var reader = new HaasMtConnectReader(client, TimeProvider.System);
        var slow = reader.ReadAsync(
            "slow.example", 8082, 5000, 10605, HaasPartCounterSources.M30Counter1);
        await client.SlowProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var healthy = await reader.ReadAsync(
                    "healthy.example", 8082, 1000, 10605, HaasPartCounterSources.M30Counter1)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("AVAILABLE", healthy.Availability);
        }
        finally
        {
            client.ReleaseSlowProbe.TrySetResult();
            await slow;
        }
    }

    [Fact]
    public async Task Internal_deadline_is_reported_as_MTConnect_timeout()
    {
        var reader = new HaasMtConnectReader(new BlockingProbeClient(), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync(
            "timeout.example", 8082, 50, 10605, HaasPartCounterSources.M30Counter1));

        Assert.Contains("timeout.example:8082", exception.Message, StringComparison.Ordinal);
        Assert.Contains("50 ms", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_remains_operation_cancellation()
    {
        var reader = new HaasMtConnectReader(new BlockingProbeClient(), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(
            "cancelled.example", 8082, 5000, 10605,
            HaasPartCounterSources.M30Counter1, cancellation.Token));

        Assert.IsNotType<TimeoutException>(exception);
    }

    [Fact]
    public async Task Same_authority_reuses_probe_but_reads_fresh_current_document()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var current = MtConnectDocumentParser.ParseCurrent(MtConnectTestDocuments.Current, probe);
        var client = new SequencedClient([probe], [current, current]);
        var reader = new HaasMtConnectReader(client, TimeProvider.System);

        await reader.ReadAsync(
            "192.168.0.56", 8082, 3000, 10605, HaasPartCounterSources.M30Counter1);
        await reader.ReadAsync(
            "192.168.0.56", 8082, 3000, 10605, HaasPartCounterSources.M30Counter1);

        Assert.Equal(1, client.ProbeCallCount);
        Assert.Equal(2, client.CurrentCallCount);
        Assert.Equal([probe, probe], client.CurrentProbeArguments);
    }

    [Fact]
    public async Task Instance_change_refreshes_probe_and_retries_current_with_new_definition_set()
    {
        var originalProbe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var refreshedProbe = originalProbe with
        {
            Header = originalProbe.Header with { InstanceId = "replacement-instance" }
        };
        var parsedWithOriginalProbe = MtConnectDocumentParser.ParseCurrent(
            MtConnectTestDocuments.Current, originalProbe);
        var mismatchedCurrent = parsedWithOriginalProbe with
        {
            Header = parsedWithOriginalProbe.Header with { InstanceId = "replacement-instance" }
        };
        var parsedWithRefreshedProbe = MtConnectDocumentParser.ParseCurrent(
            MtConnectTestDocuments.Current, refreshedProbe);
        var replacementCurrent = parsedWithRefreshedProbe with
        {
            Header = parsedWithRefreshedProbe.Header with { InstanceId = "replacement-instance" }
        };
        var client = new SequencedClient(
            [originalProbe, refreshedProbe],
            [mismatchedCurrent, replacementCurrent]);
        var reader = new HaasMtConnectReader(client, TimeProvider.System);

        var result = await reader.ReadAsync(
            "192.168.0.56", 8082, 3000, 10605, HaasPartCounterSources.M30Counter1);

        Assert.Equal("VF-3SS", result.DeviceName);
        Assert.Equal(2, client.ProbeCallCount);
        Assert.Equal(2, client.CurrentCallCount);
        Assert.Equal([originalProbe, refreshedProbe], client.CurrentProbeArguments);
    }

    private sealed class StubClient(
        MtConnectProbeDocument probe,
        MtConnectCurrentDocument current) : IMtConnectClient
    {
        public Task<MtConnectProbeDocument> ProbeAsync(
            Uri agentBaseAddress,
            CancellationToken cancellationToken = default) => Task.FromResult(probe);

        public Task<MtConnectCurrentDocument> ReadCurrentAsync(
            Uri agentBaseAddress,
            CancellationToken cancellationToken = default) => Task.FromResult(current);

        public Task<MtConnectCurrentDocument> ReadCurrentAsync(
            Uri agentBaseAddress,
            MtConnectProbeDocument probeDocument,
            CancellationToken cancellationToken = default) => Task.FromResult(current);
    }

    private sealed class IsolatedProbeClient(
        MtConnectProbeDocument probe,
        MtConnectCurrentDocument current) : IMtConnectClient
    {
        internal TaskCompletionSource SlowProbeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSlowProbe { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MtConnectProbeDocument> ProbeAsync(
            Uri agentBaseAddress,
            CancellationToken cancellationToken = default)
        {
            if (agentBaseAddress.Host.Equals("slow.example", StringComparison.OrdinalIgnoreCase))
            {
                SlowProbeStarted.TrySetResult();
                await ReleaseSlowProbe.Task.WaitAsync(cancellationToken);
            }
            return probe;
        }

        public Task<MtConnectCurrentDocument> ReadCurrentAsync(
            Uri agentBaseAddress,
            CancellationToken cancellationToken = default) => Task.FromResult(current);

        public Task<MtConnectCurrentDocument> ReadCurrentAsync(
            Uri agentBaseAddress,
            MtConnectProbeDocument probeDocument,
            CancellationToken cancellationToken = default) => Task.FromResult(current);
    }

    private sealed class BlockingProbeClient : IMtConnectClient
    {
        public async Task<MtConnectProbeDocument> ProbeAsync(
            Uri agentBaseAddress,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-aware probe unexpectedly completed.");
        }

        public Task<MtConnectCurrentDocument> ReadCurrentAsync(
            Uri agentBaseAddress,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Current must not be requested before probe completes.");

        public Task<MtConnectCurrentDocument> ReadCurrentAsync(
            Uri agentBaseAddress,
            MtConnectProbeDocument probeDocument,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Current must not be requested before probe completes.");
    }

    private sealed class SequencedClient(
        IReadOnlyList<MtConnectProbeDocument> probeResults,
        IReadOnlyList<MtConnectCurrentDocument> currentResults) : IMtConnectClient
    {
        private int probeIndex;
        private int currentIndex;

        internal int ProbeCallCount => probeIndex;
        internal int CurrentCallCount => currentIndex;
        internal List<MtConnectProbeDocument> CurrentProbeArguments { get; } = [];

        public Task<MtConnectProbeDocument> ProbeAsync(
            Uri agentBaseAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Next(probeResults, ref probeIndex, "probe"));

        public Task<MtConnectCurrentDocument> ReadCurrentAsync(
            Uri agentBaseAddress,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The enriched current overload is required.");

        public Task<MtConnectCurrentDocument> ReadCurrentAsync(
            Uri agentBaseAddress,
            MtConnectProbeDocument probeDocument,
            CancellationToken cancellationToken = default)
        {
            CurrentProbeArguments.Add(probeDocument);
            return Task.FromResult(Next(currentResults, ref currentIndex, "current"));
        }

        private static T Next<T>(IReadOnlyList<T> values, ref int index, string operation)
        {
            if (index >= values.Count)
                throw new InvalidOperationException($"Unexpected extra MTConnect {operation} request.");
            return values[index++];
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
