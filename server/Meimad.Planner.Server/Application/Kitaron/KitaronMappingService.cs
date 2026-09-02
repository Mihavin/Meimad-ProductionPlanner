using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed partial class KitaronMappingService(
    IKitaronMappingRepository repository,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] BothModes = ["domain_aligned", "flat_requested"];
    private static readonly string[] DomainMode = ["domain_aligned"];
    private static readonly HashSet<string> Confidences =
        new(["high", "medium", "low", "blocked"], StringComparer.Ordinal);
    private static readonly HashSet<string> Transforms = new(
        [
            "direct", "trim", "trim_or_null", "positive_integer", "positive_int",
            "date_only", "ordered_position", "manual_lookup", "generated_working_folder",
            "seconds", "minutes_to_seconds", "hours_to_seconds", "hours_to_seconds_pending", "unmapped",
            "canonical_order_status", "canonical_order_price"
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlyList<CatalogField> Catalog =
    [
        new("cases", "part_number", "Part Number", "Permanent Case identity.", true,
            "DetailNumber", "high", "trim", ["ITEM_NUMBER", "DetailNumber", "KatalogNumber"], BothModes),
        new("cases", "name", "Case Name", "Human-readable item name.", true,
            "DetailName", "high", "trim", ["ITEM_NAME", "DetailName", "ItemName"], BothModes),
        new("cases", "revision", "Revision", "Item or drawing revision.", false,
            "REV", "high", "trim_or_null", ["ITEM_REVISION", "REV", "DrawingRev"], BothModes),
        new("cases", "customer", "Customer", "Default Case customer; multi-customer items require review.", false,
            "CompanyName", "medium", "trim_or_null", ["CUSTOMER_NAME", "CompanyName"], BothModes),
        new("cases", "working_folder_path", "Working Folder", "Generated below the configured import root; never copied blindly.", true,
            null, "medium", "generated_working_folder", [], BothModes),

        new("orders", "order_reference", "Order Reference", "Sales-order line identity in domain-aligned mode.", true,
            "OrderNumber", "medium", "trim", ["SALES_ORDER_NUMBER", "ORDER_NUMBER", "OrderNumber", "WORKORDER_NUMBER"], BothModes),
        new("orders", "quantity", "Order Quantity", "Positive integral demand quantity.", true,
            "OrdAmount", "medium", "positive_integer", ["ORDER_QUANTITY", "QUANTITY", "OrdAmount", "Number"], BothModes),
        new("orders", "work_finish_date", "Work Finish Date", "Customer commitment date used by planning.", true,
            "SupplyDate", "medium", "date_only", ["WORK_FINISH_DATE", "SUPPLY_DATE", "SupplyDate"], BothModes),
        new("orders", "status", "Kitaron Order Status",
            "Connector-managed projection: cancelled when StopProduction is set, inactive when the delivery row is closed/supplied, otherwise active.",
            true, null, "high", "canonical_order_status", [], BothModes, true),
        new("orders", "price", "Unit Price",
            "Connector-managed unit sales price from TSubOrder.PriceInCurr in the Kitaron order currency; never a row total or manufacturing/BOM cost.",
            false, "PriceInCurr", "high", "canonical_order_price", ["PriceInCurr"], BothModes, true),

        new("case_operations", "operation_number", "Route Operation Number", "Reusable Case-route operation number.", true,
            "ActionNumber", "medium", "positive_int", ["OPER_NUMBER", "OPERATION_NUMBER", "ActionNumber"], BothModes),
        new("case_operations", "route_position", "Route Position", "Stable order within the reusable route.", true,
            "auto", "medium", "ordered_position", ["ROUTE_POSITION", "OPER_SEQUENCE", "auto", "ActNum"], BothModes),
        new("case_operations", "name", "Route Operation Name", "Reusable operation description, not employee name.", true,
            "ActionDescription", "high", "trim", ["OPER_NAME", "OPERATION_NAME", "ActionDescription", "Operation"], BothModes),
        new("case_operations", "required_machine_type", "Required Machine Type", "Manual Kitaron station-to-Meimad Machine Type lookup.", false,
            "Station", "low", "manual_lookup", ["MACHINE_TYPE", "STATION", "Station", "StationType"], BothModes),
        new("case_operations", "setup_seconds", "Setup Seconds", "Leave blocked until source units and precedence are confirmed.", false,
            "DirectionTimeP", "blocked", "hours_to_seconds_pending", ["SETUP_SECONDS", "SETUP_TIME", "DirectionTimeP", "DirectionTime"], BothModes),
        new("case_operations", "cycle_seconds", "Cycle Seconds", "Leave blocked until per-part/cycle units are confirmed.", false,
            "TimeProductionP", "blocked", "hours_to_seconds_pending", ["CYCLE_SECONDS", "CYCLE_TIME", "TimeProductionP", "TimeProduction"], BothModes),

        new("production_batches", "batch_number", "Batch Number", "Kitaron production work-order identity.", true,
            "RootID", "high", "trim", ["WORKORDER_NUMBER", "WORK_ORDER_NUMBER", "RootID", "NUMBER"], DomainMode),
        new("production_batches", "planned_quantity", "Planned Quantity", "Positive integral production quantity.", true,
            "ProductionAmount", "high", "positive_integer", ["PLANNED_QUANTITY", "PRODUCTION_QUANTITY", "ProductionAmount"], DomainMode),

        new("batch_operations", "operation_number", "Work-order Operation Number", "Operation identity within a Production Batch.", true,
            "ActionNumber", "high", "positive_int", ["OPER_NUMBER", "OPERATION_NUMBER", "ActionNumber"], DomainMode),
        new("batch_operations", "route_position", "Work-order Route Position", "Stable operation order within the work order.", true,
            "auto", "high", "ordered_position", ["ROUTE_POSITION", "OPER_SEQUENCE", "auto", "ActNum"], DomainMode),
        new("batch_operations", "name", "Work-order Operation Name", "Work-order-specific operation description.", true,
            "ActionDescription", "high", "trim", ["OPER_NAME", "OPERATION_NAME", "ActionDescription", "Operation"], DomainMode),
        new("batch_operations", "required_machine_type", "Operation Machine Type", "Manual station-to-Machine Type lookup; never a Machine assignment.", false,
            "Station", "low", "manual_lookup", ["MACHINE_TYPE", "STATION", "Station", "StationType"], DomainMode),
        new("batch_operations", "setup_seconds", "Operation Setup Seconds", "Work-order override after unit confirmation.", false,
            "DirectionTimeP", "blocked", "hours_to_seconds_pending", ["SETUP_SECONDS", "SETUP_TIME", "DirectionTimeP", "DirectionTime"], DomainMode),
        new("batch_operations", "cycle_seconds", "Operation Cycle Seconds", "Work-order override after unit confirmation.", false,
            "TimeProductionP", "blocked", "hours_to_seconds_pending", ["CYCLE_SECONDS", "CYCLE_TIME", "TimeProductionP", "TimeProduction"], DomainMode),

        new("material_orders", "source_key", "Material Order Source Key", "Stable Kitaron raw-material purchase-row identity (TBuyRow.BuyRowID).", true,
            "BuyRowID", "high", "trim", ["BuyRowID"], BothModes),
        new("material_orders", "purchase_order_number", "Purchase Order Number", "Kitaron supplier purchase-order number.", true,
            "BuyMainID", "high", "trim", ["BuyMainID"], BothModes),
        new("material_orders", "line_number", "Purchase Order Line", "Line number within the supplier purchase order.", true,
            "NumberOfString", "high", "trim", ["NumberOfString"], BothModes),
        new("material_orders", "material_number", "Material Number", "Raw-material master identity.", true,
            "RowMaterialID", "high", "trim", ["RowMaterialID", "CatalogNumber"], BothModes),
        new("material_orders", "description", "Material Description", "Ordered raw-material description and dimensions.", false,
            "Information", "high", "trim_or_null", ["Information", "MaterialDescription"], BothModes),
        new("material_orders", "supplier", "Supplier", "Supplier named on the Kitaron purchase order.", false,
            "SupplyerName", "high", "trim_or_null", ["SupplyerName", "SupplierName"], BothModes),
        new("material_orders", "ordered_quantity", "Ordered Quantity", "Quantity ordered on the raw-material purchase line.", true,
            "Amount", "high", "direct", ["Amount"], BothModes),
        new("material_orders", "received_quantity", "Received Quantity", "Historical Kitaron receipt total; advisory only, never verified Planner availability.", false,
            "ReceivedAmount", "high", "direct", ["ReceivedAmount"], BothModes),
        new("material_orders", "unit", "Order Unit", "Purchase-line unit of measure.", false,
            "MeasureUnit", "high", "trim_or_null", ["MeasureUnit"], BothModes),
        new("material_orders", "requested_delivery_date", "Requested Delivery Date", "Delivery date requested on the purchase line.", false,
            "DateToRecept", "high", "date_only", ["DateToRecept"], BothModes),
        new("material_orders", "approved_delivery_date", "Supplier Approved Delivery", "Latest supplier-approved delivery date from TAppCostOfferBySupplier.", false,
            "SupplierDate", "high", "date_only", ["SupplierDate", "AppDate"], BothModes),
        new("material_orders", "approved_quantity", "Supplier Approved Quantity", "Latest quantity acknowledged by the supplier.", false,
            "SupplierAmount", "high", "direct", ["SupplierAmount"], BothModes),
        new("material_orders", "approval_note", "Delivery Approval Note", "Latest supplier delivery-approval remark.", false,
            "SupplierRemark", "high", "trim_or_null", ["SupplierRemark", "Remark"], BothModes),
        new("material_orders", "status", "Material Order Status", "Kitaron purchase-line status.", false,
            "Status", "high", "trim_or_null", ["Status"], BothModes),
        new("material_orders", "closed", "Closed", "True when the Kitaron purchase row or its purchase order is closed.", false,
            "Closed", "high", "direct", ["Closed"], BothModes)
    ];

    internal async Task<KitaronMappingSettings> GetAsync(CancellationToken cancellationToken) =>
        Public(await repository.GetAsync(cancellationToken));

    internal async Task<KitaronMappingSettings> UpdateAsync(
        KitaronMappingUpdate update,
        CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(cancellationToken);
        var modelMode = NormalizeToken(update.ModelMode, "modelMode", ["domain_aligned", "flat_requested"]);
        var status = NormalizeToken(update.Status, "status", ["draft", "ready_for_implementation"]);
        var notes = NormalizeNotes(update.Notes, "notes", 4000);
        if (update.Fields is null || update.Fields.Count != Catalog.Count)
        {
            throw new KitaronMappingValidationException(
                "fields", $"All {Catalog.Count} mapping fields must be submitted exactly once.");
        }

        var updates = new Dictionary<string, KitaronMappingFieldUpdate>(StringComparer.Ordinal);
        foreach (var field in update.Fields)
        {
            var entity = Required(field.TargetEntity, "targetEntity", 64);
            var target = Required(field.TargetField, "targetField", 64);
            var key = Key(entity, target);
            if (!updates.TryAdd(key, field))
            {
                throw new KitaronMappingValidationException("fields", $"Duplicate mapping target {entity}.{target}.");
            }
        }

        var detected = DeserializeColumns(current.DetectedColumnsJson);
        var detectedNames = detected.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selections = new List<KitaronMappingSelection>(Catalog.Count);
        foreach (var catalog in Catalog)
        {
            if (!updates.Remove(Key(catalog.TargetEntity, catalog.TargetField), out var field))
            {
                throw new KitaronMappingValidationException(
                    "fields", $"Mapping target {catalog.TargetEntity}.{catalog.TargetField} is missing.");
            }
            var source = NormalizeSourceColumn(field.SourceColumn);
            var confidence = NormalizeToken(field.Confidence, "confidence", Confidences);
            var transform = NormalizeToken(field.Transform, "transform", Transforms);
            var fieldNotes = NormalizeNotes(field.Notes, "fieldNotes", 1000);
            var applies = catalog.ModelModes.Contains(modelMode, StringComparer.Ordinal);
            if (catalog.ConnectorManaged)
            {
                if (!field.Enabled
                    || !StringComparer.OrdinalIgnoreCase.Equals(source, catalog.DefaultSourceColumn)
                    || confidence != catalog.DefaultConfidence
                    || transform != catalog.DefaultTransform)
                {
                    throw new KitaronMappingValidationException(
                        "fields",
                        $"{catalog.TargetEntity}.{catalog.TargetField} is managed by the canonical Kitaron connector and cannot be remapped or disabled.");
                }
                selections.Add(new KitaronMappingSelection(
                    catalog.TargetEntity, catalog.TargetField, true,
                    catalog.DefaultSourceColumn, catalog.DefaultConfidence,
                    catalog.DefaultTransform, fieldNotes));
                continue;
            }
            if (applies && catalog.Required && !field.Enabled)
            {
                throw new KitaronMappingValidationException(
                    "enabled", $"{catalog.TargetEntity}.{catalog.TargetField} is required in {modelMode} mode.");
            }
            if (field.Enabled && source is null
                && transform is not ("generated_working_folder" or "ordered_position"))
            {
                throw new KitaronMappingValidationException(
                    "sourceColumn", $"{catalog.TargetEntity}.{catalog.TargetField} needs a source column or a generated transform.");
            }
            if (status == "ready_for_implementation" && applies && field.Enabled)
            {
                if (confidence == "blocked")
                {
                    throw new KitaronMappingValidationException(
                        "confidence", $"{catalog.TargetEntity}.{catalog.TargetField} is still blocked.");
                }
                if (source is not null && !StringComparer.OrdinalIgnoreCase.Equals(source, "auto")
                    && detectedNames.Count > 0 && !detectedNames.Contains(source))
                {
                    throw new KitaronMappingValidationException(
                        "sourceColumn", $"Source column {source} was not found by the last successful connection test.");
                }
            }
            selections.Add(new KitaronMappingSelection(
                catalog.TargetEntity, catalog.TargetField, field.Enabled, source,
                confidence, transform, fieldNotes));
        }
        if (updates.Count != 0)
        {
            throw new KitaronMappingValidationException(
                "fields", $"Unknown mapping target {updates.Keys.First()}.");
        }

        var stored = current with
        {
            ModelMode = modelMode,
            Status = status,
            MappingsJson = JsonSerializer.Serialize(selections, JsonOptions),
            Notes = notes,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        return Public(await repository.UpdateAsync(
            stored, update.ExpectedVersion, cancellationToken));
    }

    private static KitaronMappingSettings Public(StoredKitaronMappingSettings stored)
    {
        var selections = DeserializeSelections(stored.MappingsJson)
            .ToDictionary(item => Key(item.TargetEntity, item.TargetField), StringComparer.Ordinal);
        var fields = Catalog.Select(catalog =>
        {
            var selection = selections.GetValueOrDefault(Key(catalog.TargetEntity, catalog.TargetField))
                ?? new KitaronMappingSelection(
                    catalog.TargetEntity, catalog.TargetField, true, catalog.DefaultSourceColumn,
                    catalog.DefaultConfidence, catalog.DefaultTransform, null);
            return new KitaronMappingField(
                catalog.TargetEntity, catalog.TargetField, catalog.DisplayName, catalog.Description,
                catalog.Required, selection.Enabled, selection.SourceColumn, selection.Confidence,
                selection.Transform, selection.Notes, catalog.SuggestedSourceColumns, catalog.ModelModes,
                catalog.ConnectorManaged);
        }).ToArray();
        return new KitaronMappingSettings(
            stored.ModelMode, stored.Status, fields, DeserializeColumns(stored.DetectedColumnsJson),
            stored.Notes, stored.Version, stored.UpdatedAt);
    }

    private static IReadOnlyList<KitaronMappingSelection> DeserializeSelections(string json) =>
        JsonSerializer.Deserialize<KitaronMappingSelection[]>(json, JsonOptions) ?? [];

    private static IReadOnlyList<KitaronSourceColumn> DeserializeColumns(string json) =>
        JsonSerializer.Deserialize<KitaronSourceColumn[]>(json, JsonOptions) ?? [];

    private static string? NormalizeSourceColumn(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > 128 || !SqlIdentifier().IsMatch(normalized))
        {
            throw new KitaronMappingValidationException(
                "sourceColumn", "Source columns must be SQL identifiers of at most 128 characters.");
        }
        return normalized;
    }

    private static string NormalizeToken(
        string? value,
        string field,
        IEnumerable<string> allowed)
    {
        var normalized = value?.Trim();
        if (normalized is null || !allowed.Contains(normalized, StringComparer.Ordinal))
        {
            throw new KitaronMappingValidationException(
                field, $"{field} is not a supported value.");
        }
        return normalized;
    }

    private static string Required(string? value, string field, int maximum)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maximum)
        {
            throw new KitaronMappingValidationException(
                field, $"{field} is required and must contain at most {maximum} characters.");
        }
        return normalized;
    }

    private static string? NormalizeNotes(string? value, string field, int maximum)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maximum)
        {
            throw new KitaronMappingValidationException(
                field, $"{field} must contain at most {maximum} characters.");
        }
        return normalized;
    }

    private static string Key(string entity, string field) => $"{entity}.{field}";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_$#@]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlIdentifier();

    private sealed record CatalogField(
        string TargetEntity,
        string TargetField,
        string DisplayName,
        string Description,
        bool Required,
        string? DefaultSourceColumn,
        string DefaultConfidence,
        string DefaultTransform,
        IReadOnlyList<string> SuggestedSourceColumns,
        IReadOnlyList<string> ModelModes,
        bool ConnectorManaged = false);
}
