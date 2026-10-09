namespace Meimad.Planner.Server.Domain.Timeline;
internal static class SetupWorkerQualification
{
    internal static bool Matches(IReadOnlyList<string> employeeSkills, IReadOnlyList<string> machineSkills) =>
        employeeSkills.Any(value => value.Trim() == "*") || employeeSkills.Any(employee =>
            machineSkills.Any(machine => string.Equals(employee.Trim(), machine.Trim(), StringComparison.OrdinalIgnoreCase)));
}
