namespace Meimad.Planner.Server.Domain.ToolPreparations;

/// <summary>
/// A spindle adaptor of the Setup library (schema v91): the tool holder's taper above the gauge line
/// (<see cref="TaperLength"/> from the gauge line up to the small end, <see cref="GaugeDiameter"/>
/// at the gauge line, <see cref="SmallEndDiameter"/> at the top) and the tool-changer flange just
/// below it, drawn as a cylinder of <see cref="ToolChangerDiameter"/> × <see cref="ToolChangerLength"/>
/// (TCD × TCL). Millimetres.
/// </summary>
internal sealed record SpindleAdaptor(
    string SpindleAdaptorId,
    string Name,
    double TaperLength,
    double GaugeDiameter,
    double SmallEndDiameter,
    double ToolChangerDiameter,
    double ToolChangerLength,
    string? Notes,
    bool IsActive,
    int Version,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

/// <summary>
/// A pull stud (retention knob) of the Setup library: <see cref="ExposedLength"/> is the part above
/// the taper's small end, drawn as the pilot collar, the neck and the knob. Millimetres and degrees.
/// </summary>
internal sealed record PullStud(
    string PullStudId,
    string Name,
    string? Thread,
    double? Angle,
    double? OverallLength,
    double ExposedLength,
    double KnobDiameter,
    double? NeckDiameter,
    double? PilotDiameter,
    string? Notes,
    bool IsActive,
    int Version,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

/// <summary>The default adaptor and pull stud of a Machine's spindle; every tool on it uses them unless it overrides them.</summary>
internal sealed record MachineSpindleInterface(
    string MachineId,
    string? SpindleAdaptorId,
    string? PullStudId,
    int Version);

internal sealed record SpindleLibrary(
    IReadOnlyList<SpindleAdaptor> Adaptors,
    IReadOnlyList<PullStud> PullStuds,
    IReadOnlyList<MachineSpindleInterface> Machines);

internal sealed record SpindleAdaptorValues(
    string? Name, double TaperLength, double GaugeDiameter, double? SmallEndDiameter,
    double ToolChangerDiameter, double ToolChangerLength, string? Notes, bool IsActive);

internal sealed record PullStudValues(
    string? Name, string? Thread, double? Angle, double? OverallLength, double ExposedLength,
    double KnobDiameter, double? NeckDiameter, double? PilotDiameter, string? Notes, bool IsActive);

internal sealed class SpindleInterfaceValidationException(string code, string message, string? field = null) : Exception(message)
{
    internal string Code { get; } = code;
    internal string? Field { get; } = field;
}

internal sealed class SpindleInterfaceConflictException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class SpindleInterfaceNotFoundException(string message) : Exception(message);

internal static class SpindleInterfaceValidator
{
    /// <summary>A 7:24 steep taper narrows by 7 mm of diameter per 24 mm of length.</summary>
    internal static double SteepTaperSmallEnd(double gaugeDiameter, double taperLength) =>
        Math.Round(gaugeDiameter - taperLength * 7 / 24, 3);

    internal static SpindleAdaptorValues Validate(SpindleAdaptorValues values)
    {
        var name = Name(values.Name);
        var taper = Size(values.TaperLength, "taperLength", positive: true);
        var gauge = Size(values.GaugeDiameter, "gaugeDiameter", positive: true);
        var small = values.SmallEndDiameter is { } given ? Size(given, "smallEndDiameter", positive: true) : SteepTaperSmallEnd(gauge, taper);
        if (small <= 0 || small > gauge)
            throw new SpindleInterfaceValidationException("spindle_adaptor_small_end_invalid",
                "smallEndDiameter must be positive and not larger than gaugeDiameter.", "smallEndDiameter");
        return values with
        {
            Name = name,
            TaperLength = taper,
            GaugeDiameter = gauge,
            SmallEndDiameter = small,
            ToolChangerDiameter = Size(values.ToolChangerDiameter, "toolChangerDiameter", positive: true),
            ToolChangerLength = Size(values.ToolChangerLength, "toolChangerLength", positive: false),
            Notes = Notes(values.Notes)
        };
    }

    internal static PullStudValues Validate(PullStudValues values)
    {
        var thread = values.Thread?.Trim();
        if (thread is { Length: > 40 })
            throw new SpindleInterfaceValidationException("pull_stud_thread_too_long", "thread must be at most 40 characters.", "thread");
        if (values.Angle is { } angle && (!double.IsFinite(angle) || angle < 0 || angle > 180))
            throw new SpindleInterfaceValidationException("pull_stud_angle_invalid", "angle must be between 0 and 180 degrees.", "angle");
        return values with
        {
            Name = Name(values.Name),
            Thread = string.IsNullOrEmpty(thread) ? null : thread,
            OverallLength = values.OverallLength is { } overall ? Size(overall, "overallLength", positive: true) : null,
            ExposedLength = Size(values.ExposedLength, "exposedLength", positive: true),
            KnobDiameter = Size(values.KnobDiameter, "knobDiameter", positive: true),
            NeckDiameter = values.NeckDiameter is { } neck ? Size(neck, "neckDiameter", positive: true) : null,
            PilotDiameter = values.PilotDiameter is { } pilot ? Size(pilot, "pilotDiameter", positive: true) : null,
            Notes = Notes(values.Notes)
        };
    }

    private static string Name(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 80)
            throw new SpindleInterfaceValidationException("spindle_interface_name_invalid", "name is required (at most 80 characters).", "name");
        return name;
    }

    private static double Size(double value, string field, bool positive)
    {
        if (!double.IsFinite(value) || value < 0 || (positive && value == 0) || value > 1000)
            throw new SpindleInterfaceValidationException("spindle_interface_dimension_invalid",
                $"{field} must be a millimetre value {(positive ? "above 0" : "from 0")} up to 1000.", field);
        return Math.Round(value, 4);
    }

    private static string? Notes(string? value)
    {
        var notes = value?.Trim();
        if (notes is { Length: > 500 })
            throw new SpindleInterfaceValidationException("spindle_interface_notes_too_long", "notes must be at most 500 characters.", "notes");
        return string.IsNullOrEmpty(notes) ? null : notes;
    }
}
