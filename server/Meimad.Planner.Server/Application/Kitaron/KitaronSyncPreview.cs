namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed partial class KitaronSyncService
{
    private sealed record PendingPreview(string Actor, KitaronSyncPlan Plan, KitaronImportPreview View);
    private PendingPreview? pendingPreview; // One bounded preview; restart or replacement requires a fresh read.

    internal async Task<KitaronImportPreview> PreviewAsync(string actor, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new KitaronSyncBlockedException("A Kitaron import operation is already running.");
        try
        {
            var mapping = await mappingService.GetAsync(token);
            var plan = await PrepareAsync(mapping, withLocalRevision: true, token);
            plan = plan with { Evidence = plan.Evidence! with { RequestedBy = actor } };
            if (plan.ExpectedLocalRevision is null) throw new KitaronSyncBlockedException("Versioned import preview is unavailable.");
            var local = await syncRepository.PreviewAsync(plan, timeProvider.GetUtcNow(), token);
            var view = new KitaronImportPreview(plan.Evidence!.RunId, timeProvider.GetUtcNow().AddMinutes(15),
                plan.ExpectedLocalRevision, plan.Evidence, local, plan.Warnings);
            pendingPreview = new(actor, plan, view);
            return view;
        }
        finally { gate.Release(); }
    }

    internal async Task<KitaronSyncStatus> ApplyPreviewAsync(string actor, string previewId, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new KitaronSyncBlockedException("A Kitaron import operation is already running.");
        try
        {
            var saved = pendingPreview;
            if (saved is null || saved.Actor != actor || saved.View.PreviewId != previewId || saved.View.ExpiresAt <= timeProvider.GetUtcNow())
                throw new KitaronSyncBlockedException("The preview expired, was replaced or belongs to another user. Create a fresh preview.");
            var receipt = await syncRepository.FindReceiptAsync(previewId, token);
            if (receipt is not null) return receipt;
            var current = await PrepareAsync(await mappingService.GetAsync(token), withLocalRevision: false, token);
            if (current.Evidence!.SourceEndpoint != saved.Plan.Evidence!.SourceEndpoint
                || current.Evidence.ConnectionVersion != saved.Plan.Evidence.ConnectionVersion
                || current.MappingVersion != saved.Plan.MappingVersion
                || current.Evidence.SourceFingerprint != saved.Plan.Evidence.SourceFingerprint)
                throw new KitaronSyncBlockedException("The ERP source, mapping or connection changed since preview. Create a fresh preview; nothing was applied.");
            // Apply the reviewed plan, guarded against its original local context inside SQLite's write transaction.
            return await syncRepository.ApplyAsync(saved.Plan with
            { Evidence = saved.Plan.Evidence! with { ReviewedPreview = true } }, timeProvider.GetUtcNow(), token);
        }
        finally { gate.Release(); }
    }

    private static void ValidateSourceIdentity(KitaronSourceSnapshot source, bool strict)
    {
        static void Unique<T>(IEnumerable<T> rows, Func<T, string> key, string entity)
        {
            foreach (var group in rows.GroupBy(key, StringComparer.OrdinalIgnoreCase))
                if (string.IsNullOrWhiteSpace(group.Key) || group.Select(KitaronImportFingerprint.Of).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    throw new KitaronSyncBlockedException($"Ambiguous Kitaron {entity} source key '{group.Key}': conflicting source rows. No import was applied.");
        }
        Unique(source.Orders, x => x.SourceKey, "order");
        Unique(source.Components, x => x.SourceKey, "component");
        Unique(source.WorkOrders ?? [], x => x.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), "work order");
        if (!strict) return;
        foreach (var order in source.Orders)
            if (order.Quantity is { } quantity && (!double.IsFinite(quantity) || quantity < 0 || quantity != Math.Truncate(quantity)))
                throw new KitaronSyncBlockedException($"Kitaron order '{order.SourceKey}' quantity {order.Quantity} must be a nonnegative whole count of parts. Unit conversion/rounding is not permitted.");
    }
}
