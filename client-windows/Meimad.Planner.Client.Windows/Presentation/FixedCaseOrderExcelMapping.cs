using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// Temporary fixed Excel contract used until Kitaron integration replaces this tool.
/// It intentionally contains only Case and Order fields supplied by the operator.
/// </summary>
internal static class FixedCaseOrderExcelMapping
{
    internal static IReadOnlyList<LegacyImportColumnMapping> Columns { get; } =
    [
        new("open_orders", "partNumber", "A"),
        new("open_orders", "orderNumber", "B"),
        new("open_orders", "customer", "D"),
        new("open_orders", "deliveryDate", "E"),
        new("open_orders", "revision", "F"),
        new("open_orders", "orderedQuantity", "L"),
        new("open_orders", "productionInstruction", "N"),
        new("open_orders", "itemName", "O")
    ];

    internal const string Summary =
        "Cases: Part Number A, Name O, Revision F, Customer D. "
        + "Orders: Order Number B, Quantity L, Work Finish Date E, Active/Production Instruction N. "
        + "All other fields are left empty. No Batches, Operations, Machines, assignments, or planning data are imported.";
}
