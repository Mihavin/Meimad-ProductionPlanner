using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Application.ShiftRoster;

internal interface IShiftRosterRepository
{
    /// <summary>The roster entries dated from <paramref name="from"/> through <paramref name="to"/>, of every employee or one.</summary>
    Task<IReadOnlyList<ShiftRosterEntry>> ListAsync(
        DateOnly from, DateOnly to, string? resourceId, CancellationToken token);

    /// <summary>
    /// Applies every change in one transaction. A change whose expected version no longer matches
    /// the stored entry (null: no entry) refuses the whole request with <see cref="ShiftRosterStaleException"/>.
    /// </summary>
    Task<IReadOnlyList<ShiftRosterEntry>> ApplyAsync(
        IReadOnlyList<ShiftRosterChange> changes, DateTimeOffset now, EditAuthority authority, CancellationToken token);
}

/// <summary>Sets the shift of one employee and date; a null <see cref="ShiftCode"/> removes the entry so the pattern applies again.</summary>
internal sealed record ShiftRosterChange(
    string ResourceId, DateOnly Date, string? ShiftCode, string? Note, int? ExpectedVersion);

internal sealed class ShiftRosterStaleException(ShiftRosterChange change, ShiftRosterEntry? current)
    : Exception(current is null
        ? $"The roster entry of {change.Date:yyyy-MM-dd} was removed after you opened the roster."
        : $"The roster entry of {change.Date:yyyy-MM-dd} was changed to '{current.ShiftCode}' after you opened the roster.")
{
    internal ShiftRosterChange Change { get; } = change;
    internal ShiftRosterEntry? Current { get; } = current;
}
