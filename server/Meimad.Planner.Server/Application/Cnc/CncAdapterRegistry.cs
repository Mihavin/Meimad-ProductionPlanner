using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Application.Cnc;

internal sealed class CncAdapterRegistry
{
    private static readonly CncAdapterCapabilities Unsupported = new(
        false, false, false, false, false, false, false, false,
        false, false, false, false, false);

    private static readonly IReadOnlyList<CncAdapterDefinition> Definitions =
    [
        new(CncAdapterTypes.HaasNgc, "Haas NGC", true, new(
            true, true, true, true, true, true, false, false,
            false, false, false, false, false)),
        new(CncAdapterTypes.MtConnect, "MTConnect — Coming later", false, Unsupported),
        new(CncAdapterTypes.OpcUa, "OPC UA — Coming later", false, Unsupported),
        new(CncAdapterTypes.Custom, "Custom — Coming later", false, Unsupported)
    ];

    internal IReadOnlyList<CncAdapterDefinition> List() => Definitions;

    internal CncAdapterDefinition Get(CncAdapterType type) =>
        Definitions.Single(value => value.Id == CncAdapterTypes.Serialize(type));
}
