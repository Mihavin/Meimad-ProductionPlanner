using System.Diagnostics.Metrics;

namespace Meimad.Planner.Server.Application.Projections;

internal static class PlannerPerformanceMetrics
{
    private static readonly Meter Meter = new("Meimad.Planner.Performance", "1.0");
    internal static readonly Histogram<double> HttpDuration = Meter.CreateHistogram<double>("planner.http.duration", "ms");
    internal static readonly Counter<long> HttpRequests = Meter.CreateCounter<long>("planner.http.requests");
    internal static readonly Histogram<long> HttpBytes = Meter.CreateHistogram<long>("planner.http.response_size", "By");
    internal static readonly Histogram<double> DatabaseOpen = Meter.CreateHistogram<double>("planner.sqlite.open", "ms");
    internal static readonly Counter<long> DatabaseBusy = Meter.CreateCounter<long>("planner.sqlite.open_busy");
    internal static readonly Histogram<double> QueueSnapshot = Meter.CreateHistogram<double>("planner.preparation.snapshot", "ms");
    internal static readonly Histogram<double> RunGraph = Meter.CreateHistogram<double>("planner.runs.graph", "ms");
    internal static readonly Histogram<double> BoardSource = Meter.CreateHistogram<double>("planner.board.source", "ms");
    internal static readonly Histogram<double> WorkflowPackages = Meter.CreateHistogram<double>("planner.readiness.workflow_packages", "ms");
    internal static readonly Histogram<double> ReadinessReferences = Meter.CreateHistogram<double>("planner.readiness.references", "ms");
    internal static readonly Histogram<double> ReadinessEnrichment = Meter.CreateHistogram<double>("planner.readiness.enrichment", "ms");
    internal static readonly Histogram<double> RunReadiness = Meter.CreateHistogram<double>("planner.runs.readiness", "ms");
    internal static readonly Histogram<double> TimelineSource = Meter.CreateHistogram<double>("planner.timeline.source", "ms");
    internal static readonly Histogram<double> TimelineEngine = Meter.CreateHistogram<double>("planner.timeline.engine", "ms");
    internal static readonly Histogram<double> TimelineBaseline = Meter.CreateHistogram<double>("planner.timeline.baseline", "ms");
    internal static readonly Histogram<double> TimelineAuxiliary = Meter.CreateHistogram<double>("planner.timeline.auxiliary", "ms");
    internal static readonly Histogram<double> TimelineProjection = Meter.CreateHistogram<double>("planner.timeline.projection", "ms");
    internal static readonly Histogram<double> TimelineDiagnostics = Meter.CreateHistogram<double>("planner.timeline.diagnostics", "ms");
    internal static readonly Histogram<double> TimelineMapping = Meter.CreateHistogram<double>("planner.timeline.mapping", "ms");
    internal static readonly Histogram<double> TimelinePresentation = Meter.CreateHistogram<double>("planner.timeline.presentation", "ms");
    internal static readonly Histogram<double> TimelineFinalization = Meter.CreateHistogram<double>("planner.timeline.finalization", "ms");
    internal static readonly Histogram<double> TimelineIdentity = Meter.CreateHistogram<double>("planner.timeline.identity", "ms");
    internal static readonly Histogram<double> TimelineReadiness = Meter.CreateHistogram<double>("planner.timeline.readiness", "ms");
    private static readonly Histogram<double> Serialization = Meter.CreateHistogram<double>("planner.projection.serialization", "ms");
    internal static readonly Counter<long> ProjectionCacheHits = Meter.CreateCounter<long>("planner.projection.cache_hits");
    internal static readonly Counter<long> ProjectionShared = Meter.CreateCounter<long>("planner.projection.shared_reads");
    internal static byte[] Serialize<T>(T value, System.Text.Json.JsonSerializerOptions options)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, options); }
        finally { Serialization.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }
}
