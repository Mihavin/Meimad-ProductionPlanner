using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Machines;
using Meimad.Planner.Server.Application.WorkingCalendars;
using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.AdministrativeSetup;

internal sealed class AdministrativeSetupService
{
    private readonly IAdministrativeSetupRepository repository;
    private readonly IWorkingCalendarRepository workingCalendars;
    private readonly IMachineRepository machines;
    private readonly IIsraeliHolidaySource holidaySource;
    private readonly TimeProvider timeProvider;

    public AdministrativeSetupService(IAdministrativeSetupRepository repository, IWorkingCalendarRepository workingCalendars, IMachineRepository machines, IIsraeliHolidaySource holidaySource, TimeProvider timeProvider)
    { this.repository = repository; this.workingCalendars = workingCalendars; this.machines = machines; this.holidaySource = holidaySource; this.timeProvider = timeProvider; }

    internal Task<IReadOnlyList<EmployeeResource>> ListResourcesAsync(CancellationToken token = default) => repository.ListResourcesAsync(token);
    internal async Task<IReadOnlyList<EmployeeResource>> ListAvailableResourcesAsync(CancellationToken token = default) =>
        (await repository.ListResourcesAsync(token)).Where(value => value.IsAvailableForFuturePlanning).ToArray();
    internal Task<EmployeeResource?> GetResourceAsync(string id, CancellationToken token = default) => repository.GetResourceAsync(id, token);
    internal async Task<EmployeeResource> CreateResourceAsync(CreateEmployeeResourceCommand command, EditAuthority authority, CancellationToken token = default)
    {
        var values = AdministrativeSetupValidator.Validate(new EmployeeResourceValues(
            command.EmployeeNumber, command.FirstName, command.LastName, command.ResourceType, command.Skills,
            command.AssignedCalendarId, command.PhotoPath, command.Notes, command.Email, command.IsActive, command.ToolLoadSecondsPerTool, command.FixtureAssemblySeconds, command.FirstPartRunningSpeedPercent));
        await EnsureAssignedCalendarAsync(values.AssignedCalendarId!, values.ResourceType!, token);
        await EnsureMachineSkillsAsync(values.Skills!, token);
        var now = timeProvider.GetUtcNow();
        return await repository.CreateResourceAsync(new(Guid.NewGuid().ToString("N"), values.EmployeeNumber!, values.Name, values.ResourceType!, values.Email, values.FirstName!, values.LastName!, values.Skills!, values.AssignedCalendarId!, values.PhotoPath, values.Notes, values.IsActive, 1, now, now, command.RespectMasterCalendar, values.ToolLoadSecondsPerTool, values.FixtureAssemblySeconds, values.FirstPartRunningSpeedPercent), authority, token);
    }
    internal async Task<EmployeeResource> UpdateResourceAsync(string id, int expectedVersion, UpdateEmployeeResourceCommand command, EditAuthority authority, CancellationToken token = default)
    {
        var current = await repository.GetResourceAsync(id, token) ?? throw new AdministrativeResourceNotFoundException("Employee Resource", id);
        var values = AdministrativeSetupValidator.Validate(new EmployeeResourceValues(
            Select(command.EmployeeNumber, current.EmployeeNumber), Select(command.FirstName, current.FirstName),
            Select(command.LastName, current.LastName), Select(command.ResourceType, current.ResourceType),
            Select(command.Skills, current.Skills.Select(value => (string?)value).ToArray()),
            Select(command.AssignedCalendarId, current.AssignedCalendarId), Select(command.PhotoPath, current.PhotoPath),
            Select(command.Notes, current.Notes), Select(command.Email, current.Email), Select(command.IsActive, current.IsActive) ?? false,
            Select(command.ToolLoadSecondsPerTool, current.ToolLoadSecondsPerTool) ?? 60,
            Select(command.FixtureAssemblySeconds, current.FixtureAssemblySeconds),
            Select(command.FirstPartRunningSpeedPercent, current.FirstPartRunningSpeedPercent) ?? 66.6666666667));
        await EnsureAssignedCalendarAsync(values.AssignedCalendarId!, values.ResourceType!, token);
        if (command.Skills.IsSpecified) await EnsureMachineSkillsAsync(values.Skills!, token);
        var candidate = current with { EmployeeNumber = values.EmployeeNumber!, Name = values.Name, ResourceType = values.ResourceType!, FirstName = values.FirstName!, LastName = values.LastName!, Skills = values.Skills!, AssignedCalendarId = values.AssignedCalendarId!, PhotoPath = values.PhotoPath, Notes = values.Notes, Email = values.Email, IsActive = values.IsActive, RespectMasterCalendar = Select(command.RespectMasterCalendar, current.RespectMasterCalendar) ?? true, ToolLoadSecondsPerTool = values.ToolLoadSecondsPerTool, FixtureAssemblySeconds = values.FixtureAssemblySeconds, FirstPartRunningSpeedPercent = values.FirstPartRunningSpeedPercent, Version = expectedVersion + 1, UpdatedAt = timeProvider.GetUtcNow() };
        return await repository.UpdateResourceAsync(candidate, expectedVersion, authority, token) ?? throw new AdministrativeVersionConflictException("Employee Resource", id, expectedVersion);
    }
    internal Task<bool> DeleteResourceAsync(string id, EditAuthority authority, CancellationToken token = default) => repository.DeleteResourceAsync(id, authority, token);

    private async Task EnsureMachineSkillsAsync(IReadOnlyList<string> machineIds, CancellationToken token)
    {
        if (machineIds.Count == 0) return;
        var knownIds = (await machines.ListAsync(token))
            .Select(value => value.MachineId)
            .ToHashSet(StringComparer.Ordinal);
        var issues = machineIds
            .Select((machineId, index) => (machineId, index))
            .Where(value => !knownIds.Contains(value.machineId))
            .Select(value => new ValidationIssue(
                $"skills[{value.index}]", "unknown_machine",
                $"skills[{value.index}] must identify an existing Machine."))
            .ToArray();
        if (issues.Length > 0) throw new AdministrativeSetupValidationException(issues);
    }

    internal async Task<IReadOnlyList<EmployeeCalendarException>> ListEmployeeExceptionsAsync(
        string resourceId, DateOnly? from = null, DateOnly? to = null, CancellationToken token = default)
    {
        _ = await repository.GetResourceAsync(resourceId, token)
            ?? throw new AdministrativeResourceNotFoundException("Employee Resource", resourceId);
        if (from.HasValue && to.HasValue && to < from)
            throw new EmployeeAvailabilityHorizonException("Exception range end must not be earlier than its start.");
        return await repository.ListEmployeeExceptionsAsync(resourceId, from, to, token);
    }

    internal async Task<EmployeeCalendarException> CreateEmployeeExceptionAsync(
        string resourceId, CreateEmployeeCalendarExceptionCommand command, EditAuthority authority,
        CancellationToken token = default)
    {
        _ = await repository.GetResourceAsync(resourceId, token)
            ?? throw new AdministrativeResourceNotFoundException("Employee Resource", resourceId);
        var values = AdministrativeSetupValidator.Validate(new EmployeeCalendarExceptionValues(
            command.Date, command.ExceptionType, command.IsFullDay,
            command.StartsAtLocal, command.EndsAtLocal, command.Note));
        var now = timeProvider.GetUtcNow();
        return await repository.CreateEmployeeExceptionAsync(new(
            Guid.NewGuid().ToString("N"), resourceId, values.Date!.Value, values.ExceptionType!,
            values.IsFullDay, values.StartsAtLocal, values.EndsAtLocal, values.Note, 1, now, now), authority, token);
    }

    internal async Task<EmployeeCalendarException> UpdateEmployeeExceptionAsync(
        string resourceId, string exceptionId, int expectedVersion,
        UpdateEmployeeCalendarExceptionCommand command, EditAuthority authority,
        CancellationToken token = default)
    {
        var current = await repository.GetEmployeeExceptionAsync(resourceId, exceptionId, token)
            ?? throw new AdministrativeResourceNotFoundException("Employee Calendar Exception", exceptionId);
        var values = AdministrativeSetupValidator.Validate(new EmployeeCalendarExceptionValues(
            Select(command.Date, current.Date), Select(command.ExceptionType, current.ExceptionType),
            Select(command.IsFullDay, current.IsFullDay) ?? false,
            Select(command.StartsAtLocal, current.StartsAtLocal), Select(command.EndsAtLocal, current.EndsAtLocal),
            Select(command.Note, current.Note)));
        var candidate = current with
        {
            Date = values.Date!.Value, ExceptionType = values.ExceptionType!, IsFullDay = values.IsFullDay,
            StartsAtLocal = values.StartsAtLocal, EndsAtLocal = values.EndsAtLocal, Note = values.Note,
            Version = expectedVersion + 1, UpdatedAt = timeProvider.GetUtcNow()
        };
        return await repository.UpdateEmployeeExceptionAsync(candidate, expectedVersion, authority, token)
            ?? throw new AdministrativeVersionConflictException("Employee Calendar Exception", exceptionId, expectedVersion);
    }

    internal Task<bool> DeleteEmployeeExceptionAsync(
        string resourceId, string exceptionId, EditAuthority authority, CancellationToken token = default) =>
        repository.DeleteEmployeeExceptionAsync(resourceId, exceptionId, authority, token);

    internal async Task<EmployeeAvailability> GetEmployeeAvailabilityAsync(
        string resourceId, DateTimeOffset from, DateTimeOffset to, CancellationToken token = default)
    {
        if (to <= from || to - from > TimeSpan.FromDays(366))
            throw new EmployeeAvailabilityHorizonException("Availability requires from < to and a horizon of at most 366 days.");
        var resource = await repository.GetResourceAsync(resourceId, token)
            ?? throw new AdministrativeResourceNotFoundException("Employee Resource", resourceId);
        if (string.IsNullOrWhiteSpace(resource.AssignedCalendarId))
            return new(resource.ResourceId, resource.IsActive, null, null, [],
                await repository.ListEmployeeExceptionsAsync(resourceId, DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime(to.UtcDateTime), token));
        var calendar = await workingCalendars.GetByIdAsync(resource.AssignedCalendarId, token)
            ?? throw new EmployeeAssignedCalendarNotFoundException(resource.AssignedCalendarId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
        var localFrom = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).Date);
        var localTo = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).Date);
        var exceptions = await repository.ListEmployeeExceptionsAsync(resourceId, localFrom, localTo, token);
        var holidays = calendar.UseIsraeliHolidays ? await repository.ListHolidaysAsync(token) : [];
        return EmployeeAvailabilityCalculator.Calculate(resource, calendar, exceptions, holidays, from, to);
    }

    internal Task<IReadOnlyList<IsraeliHoliday>> ListHolidaysAsync(CancellationToken token = default) => repository.ListHolidaysAsync(token);
    internal Task<IsraeliHoliday?> GetHolidayAsync(string id, CancellationToken token = default) => repository.GetHolidayAsync(id, token);
    internal async Task<IsraeliHoliday> CreateHolidayAsync(CreateIsraeliHolidayCommand command, EditAuthority authority, CancellationToken token = default)
    {
        var values = AdministrativeSetupValidator.Validate(new IsraeliHolidayValues(
            command.Date, command.Name, command.Status ?? IsraeliHolidayStatus.NonWorking, command.StartsAtLocal, command.EndsAtLocal));
        var now = timeProvider.GetUtcNow();
        return await repository.CreateHolidayAsync(new(Guid.NewGuid().ToString("N"), values.Date!.Value, values.Name!,
            values.Status!, values.StartsAtLocal, values.EndsAtLocal, "manual", null, true, 1, now, now), authority, token);
    }
    internal async Task<IsraeliHoliday> UpdateHolidayAsync(string id, int expectedVersion, UpdateIsraeliHolidayCommand command, EditAuthority authority, CancellationToken token = default)
    {
        var current = await repository.GetHolidayAsync(id, token) ?? throw new AdministrativeResourceNotFoundException("Israeli Holiday", id);
        var values = AdministrativeSetupValidator.Validate(new IsraeliHolidayValues(
            Select(command.Date, current.Date), Select(command.Name, current.Name), Select(command.Status, current.Status),
            Select(command.StartsAtLocal, current.StartsAtLocal), Select(command.EndsAtLocal, current.EndsAtLocal)));
        var candidate = current with { Date = values.Date!.Value, Name = values.Name!, Status = values.Status!,
            StartsAtLocal = values.StartsAtLocal, EndsAtLocal = values.EndsAtLocal, IsManualOverride = true,
            Version = expectedVersion + 1, UpdatedAt = timeProvider.GetUtcNow() };
        return await repository.UpdateHolidayAsync(candidate, expectedVersion, authority, token) ?? throw new AdministrativeVersionConflictException("Israeli Holiday", id, expectedVersion);
    }
    internal Task<bool> DeleteHolidayAsync(string id, EditAuthority authority, CancellationToken token = default) => repository.DeleteHolidayAsync(id, authority, token);

    internal async Task<IsraeliHolidaySyncResult> SynchronizeHolidaysAsync(
        SyncIsraeliHolidaysCommand command, EditAuthority authority, CancellationToken token = default)
    {
        if (command.FromYear < 1900 || command.ToYear > 2200 || command.ToYear < command.FromYear || command.ToYear - command.FromYear > 10)
            throw new IsraeliHolidaySyncRangeException();
        var attemptedAt = timeProvider.GetUtcNow();
        try
        {
            var items = await holidaySource.FetchAsync(command.FromYear, command.ToYear, token);
            return await repository.SynchronizeHolidaysAsync(items, holidaySource.ProviderName,
                command.FromYear, command.ToYear, attemptedAt, null, authority, token);
        }
        catch (IsraeliHolidaySourceException exception)
        {
            return await repository.SynchronizeHolidaysAsync(null, holidaySource.ProviderName,
                command.FromYear, command.ToYear, attemptedAt, exception.Message, authority, token);
        }
    }

    internal Task<ReportEmailSettings> GetReportEmailSettingsAsync(CancellationToken token = default) => repository.GetReportEmailSettingsAsync(token);
    internal async Task<ReportEmailSettings> UpdateReportEmailSettingsAsync(int expectedVersion, UpdateReportEmailSettingsCommand command, EditAuthority authority, CancellationToken token = default)
    {
        var values = AdministrativeSetupValidator.Validate(command.Values);
        var candidate = new ReportEmailSettings(values.SenderAddress, values.Recipients!.Select(value => value!).ToArray(), values.SmtpHost, values.SmtpPort, values.UseSsl, values.DailyReportEnabled, values.DailyReportTimeLocal, values.TimeZoneId, expectedVersion + 1, timeProvider.GetUtcNow(), values.WeeklyMaterialReportEnabled, values.WeeklyMaterialReportSendDay!, values.WeeklyMaterialReportTimeLocal!, values.WeeklyEmployeeEfficiencyEnabled, values.WeeklyEmployeeEfficiencySendDay!, values.WeeklyEmployeeEfficiencyTimeLocal!);
        return await repository.UpdateReportEmailSettingsAsync(candidate, expectedVersion, authority, token) ?? throw new AdministrativeVersionConflictException("Report Email Settings", "1", expectedVersion);
    }

    private static T Select<T>(AdminField<T> field, T current) => field.IsSpecified ? field.Value : current;

    private async Task EnsureAssignedCalendarAsync(string calendarId, string role, CancellationToken token)
    {
        var calendar = await workingCalendars.GetByIdAsync(calendarId, token)
            ?? throw new EmployeeAssignedCalendarNotFoundException(calendarId);
        var requiredUsage = EmployeeResourceRole.CalendarUsage(role);
        if (calendar.Usages is not null && !calendar.Usages.Contains(requiredUsage, StringComparer.OrdinalIgnoreCase))
            throw new EmployeeCalendarUsageException(calendarId, role);
    }
}

internal sealed class EmployeeNumberConflictException(string number) : Exception($"Employee number '{number}' already exists.");
internal sealed class HolidayDateConflictException(DateOnly date) : Exception($"A holiday already exists on {date:yyyy-MM-dd}.");
internal sealed class AdministrativeResourceNotFoundException(string kind, string id) : Exception($"{kind} '{id}' was not found.");
internal sealed class AdministrativeVersionConflictException(string kind, string id, int version) : Exception($"{kind} '{id}' is no longer at version {version}.");
internal sealed class EmployeeAssignedCalendarNotFoundException(string calendarId) : Exception($"Assigned Calendar '{calendarId}' was not found.");
internal sealed class EmployeeCalendarUsageException(string calendarId, string role) : Exception($"Calendar '{calendarId}' cannot be assigned to role '{role}'.");
internal sealed class EmployeeAvailabilityHorizonException(string message) : Exception(message);
internal sealed class IsraeliHolidaySyncRangeException()
    : Exception("Holiday refresh requires a Gregorian range from 1900 through 2200 spanning at most 11 years.");
