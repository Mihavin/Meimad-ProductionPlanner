using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.Reports;

internal sealed record EmployeeWorkMeasurementValues(
    string? EmployeeResourceId, DateOnly? WorkDate, long PlannedSeconds, long ActualSeconds,
    string? SourceReference, string? Notes);

internal sealed record EmployeeWorkMeasurement(
    string MeasurementId, string EmployeeResourceId, DateOnly WorkDate, long PlannedSeconds,
    long ActualSeconds, string? SourceReference, string? Notes, string RecordedBy,
    DateTimeOffset RecordedAt);

internal sealed record EmployeeEfficiencyAggregate(
    string EmployeeResourceId, string EmployeeNumber, string FirstName, string LastName,
    string Role, long PlannedSeconds, long ActualSeconds);

internal sealed record WeeklyEmployeeEfficiencyItem(
    string EmployeeResourceId, string EmployeeNumber, string FirstName, string LastName,
    string Role, long PlannedSeconds, long ActualSeconds, long DifferenceSeconds,
    decimal? PercentageDifference, long AvailableCapacitySeconds,
    decimal? PlannedCapacityPercent, decimal? ActualCapacityPercent);

internal sealed record WeeklyEmployeeEfficiencyReport(
    DateOnly WeekStart, DateOnly WeekEnd, IReadOnlyList<WeeklyEmployeeEfficiencyItem> Employees);

internal interface IWeeklyEmployeeEfficiencyRepository
{
    Task<EmployeeWorkMeasurement> CreateMeasurementAsync(EmployeeWorkMeasurement value, CancellationToken token);
    Task<IReadOnlyList<EmployeeEfficiencyAggregate>> ReadAsync(DateOnly from, DateOnly toExclusive, CancellationToken token);
    Task<bool> WasAutomaticallySentAsync(string periodKey, CancellationToken token);
    Task MarkAutomaticallySentAsync(string periodKey, int recipientCount, DateTimeOffset sentAt, CancellationToken token);
}

internal interface IEmployeeEfficiencyEmailSender
{
    Task SendAsync(ReportEmailSettings settings, WeeklyEmployeeEfficiencyReport report, CancellationToken token);
}

internal sealed class EmployeeWorkMeasurementValidationException(string field, string message) : Exception(message)
{
    internal string Field { get; } = field;
}
