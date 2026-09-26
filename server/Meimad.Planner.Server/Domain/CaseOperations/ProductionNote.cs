namespace Meimad.Planner.Server.Domain.CaseOperations;

/// <summary>
/// "Production Note" is the Machine Type value of an operation that is only a note in the
/// production chain (for example a Kitaron "הערה לייצור" step such as "FOR CONTOUR SEE REPORT").
/// It stays in the operation list and the Work Order route for everyone to read, but it needs no
/// Machine and no time: it is never offered for assignment, never scheduled, and never blocks the
/// operations around it in the dependency sequence.
/// </summary>
internal static class ProductionNote
{
    internal const string MachineType = "Production Note";

    internal static bool Is(string? requiredMachineType) =>
        string.Equals(requiredMachineType?.Trim(), MachineType, StringComparison.OrdinalIgnoreCase);
}
