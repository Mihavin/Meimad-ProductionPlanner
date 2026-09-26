using Meimad.Planner.Server.Application.Kitaron;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronRoutePlannerTests
{
    private const int Receiving = 43;
    private const int Doosan = 1046;
    private const int Generic = 38;
    private const int SetupInspection = 4;
    private const int Deburr = 8;
    private const int Plating = 41;
    private const int FinalInspection = 27;
    private const int Packing = 3;
    private const int Engineering = 36;

    private static readonly DateTimeOffset Seen = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Route_steps_become_operations_and_chained_requirements_around_the_nearest_machining_step()
    {
        var stations = Stations(
            Station(Receiving, "Receiving", "WORKSTATION", workstationTypeId: "type-inspection", perBatch: 10),
            Station(Doosan, "DOOSAN-1", "MACHINE", machineType: "Mill 3x"),
            Station(SetupInspection, "Inspection", "WORKSTATION", workstationTypeId: "type-inspection", perPart: 2, perBatch: 15),
            Station(Deburr, "Deburr", "WORKSTATION", workstationTypeId: "type-deburr", perPart: 3),
            Station(Plating, "Chrome", "EXTERNAL", externalResourceId: "external-chrome"),
            Station(FinalInspection, "Final inspection", "WORKSTATION", workstationTypeId: "type-inspection", perBatch: 30),
            Station(Packing, "Packing", "IGNORE"),
            Station(Engineering, "Engineering", "IGNORE"));
        var steps = new[]
        {
            Step("16W1120-13", 1, "10", Engineering, "Engineering review"),
            Step("16W1120-13", 2, "20", Receiving, "Raw material receipt"),
            Step("16W1120-13", 3, "30", Doosan, "MACHINE PER PS551170", production: 150, setup: 360),
            Step("16W1120-13", 4, "40", SetupInspection, "Setup inspection", production: 10, setup: 30),
            Step("16W1120-13", 5, "90", Doosan, "MACHINE OP2"),
            Step("16W1120-13", 6, "130", Deburr, "Remove burrs"),
            Step("16W1120-13", 7, "140", Plating, "Chrome plate"),
            Step("16W1120-13", 8, "150", FinalInspection, "Final inspection"),
            Step("16W1120-13", 9, "260", Packing, "Pack")
        };
        var warnings = new List<string>();

        var plan = KitaronRoutePlanner.Plan(steps, stations, Parts("16W1120-13"), Parts(), warnings);

        Assert.Empty(warnings);
        Assert.Equal(0, plan.StepsSkipped);
        Assert.Contains("16W1120-13", plan.PartsWithRoute);

        var operations = plan.Operations;
        Assert.Equal(2, operations.Count);
        Assert.Equal(30, operations[0].OperationNumber);
        Assert.Equal(0, operations[0].RoutePosition);
        Assert.Equal("MACHINE PER PS551170", operations[0].Name);
        Assert.Equal("Mill 3x", operations[0].RequiredMachineType);
        Assert.Equal(360 * 60, operations[0].SetupSeconds);
        Assert.Equal(150 * 60, operations[0].CycleSeconds);
        Assert.Equal(90, operations[1].OperationNumber);
        Assert.Equal(1, operations[1].RoutePosition);
        Assert.Null(operations[1].SetupSeconds);
        // The machining operations form one sequence.
        Assert.Null(operations[0].PredecessorSourceKey);
        Assert.Equal(operations[0].SourceKey, operations[1].PredecessorSourceKey);

        var requirements = plan.Requirements;
        Assert.Equal(5, requirements.Count);
        var receipt = Assert.Single(requirements, item => item.StepNumber == 20);
        Assert.Equal("BACKWARD", receipt.Direction);
        Assert.Equal(KitaronRoutePlanner.OperationKey("16W1120-13", 30), receipt.OperationSourceKey);
        Assert.Null(receipt.PredecessorSourceKey);
        Assert.Equal("WORKSTATION", receipt.ResourceClass);
        Assert.Equal("type-inspection", receipt.WorkstationTypeId);
        Assert.Equal(10 * 60, receipt.DurationSeconds);
        Assert.Equal(0, receipt.DurationPerUnitSeconds);

        var inspection = Assert.Single(requirements, item => item.StepNumber == 40);
        Assert.Equal("FORWARD", inspection.Direction);
        Assert.Equal(KitaronRoutePlanner.OperationKey("16W1120-13", 30), inspection.OperationSourceKey);
        Assert.Null(inspection.PredecessorSourceKey);
        Assert.Equal(30 * 60, inspection.DurationSeconds);
        Assert.Equal(10 * 60, inspection.DurationPerUnitSeconds);

        var deburr = Assert.Single(requirements, item => item.StepNumber == 130);
        var plating = Assert.Single(requirements, item => item.StepNumber == 140);
        var final = Assert.Single(requirements, item => item.StepNumber == 150);
        Assert.All(new[] { deburr, plating, final }, item =>
        {
            Assert.Equal("FORWARD", item.Direction);
            Assert.Equal(KitaronRoutePlanner.OperationKey("16W1120-13", 90), item.OperationSourceKey);
        });
        Assert.Null(deburr.PredecessorSourceKey);
        Assert.Equal(deburr.SourceKey, plating.PredecessorSourceKey);
        Assert.Equal(plating.SourceKey, final.PredecessorSourceKey);
        Assert.Equal(3 * 60, deburr.DurationPerUnitSeconds);
        Assert.Equal("EXTERNAL", plating.ResourceClass);
        Assert.Equal("external-chrome", plating.ExternalResourceId);
        Assert.Equal(0, plating.DurationSeconds);
        Assert.Equal(1, plating.CapacityRequired);
        Assert.Equal(30 * 60, final.DurationSeconds);

        // Requirements come predecessor-first so the apply can resolve ids in one pass.
        Assert.True(Index(requirements, deburr) < Index(requirements, plating));
        Assert.True(Index(requirements, plating) < Index(requirements, final));
    }

    [Fact]
    public void The_route_follows_the_operation_numbers_not_the_kitaron_NumOrder()
    {
        // The shape of the live route of 16W1120-13: NumOrder is not the process order (the
        // purchasing step 10 has NumOrder 27, the setup inspection 80 NumOrder 43).
        var stations = Stations(
            Station(Engineering, "Purchasing", "IGNORE"),
            Station(Receiving, "Receiving inspection", "WORKSTATION", workstationTypeId: "type-inspection"),
            Station(Generic, "Production", "MACHINE", machineType: "Mill 3x"),
            Station(SetupInspection, "Inspection", "WORKSTATION", workstationTypeId: "type-inspection"));
        var steps = new[]
        {
            Step("16W1120-13", 27, "10", Engineering, "Order material", directionId: 82516),
            Step("16W1120-13", 1, "20", Receiving, "Receiving inspection", directionId: 82517),
            Step("16W1120-13", 2, "30", Generic, "Mill stage 1", directionId: 82518),
            Step("16W1120-13", 2, "40", Generic, "Roughness", directionId: 82519),
            Step("16W1120-13", 50, "50", Generic, "Contour report", directionId: 82520),
            Step("16W1120-13", 43, "80", SetupInspection, "Setup inspection", directionId: 82523),
            Step("16W1120-13", 2, "90", Generic, "Mill stage 2", directionId: 82524),
            Step("16W1120-13", 3, "130", SetupInspection, "Setup inspection 2", directionId: 82527)
        };
        var warnings = new List<string>();

        var plan = KitaronRoutePlanner.Plan(steps, stations, Parts("16W1120-13"), Parts(), warnings);

        Assert.Empty(warnings);
        var operations = plan.Operations.OrderBy(item => item.RoutePosition).ToArray();
        Assert.Equal([30, 40, 50, 90], operations.Select(item => item.OperationNumber).ToArray());
        Assert.Equal([0, 1, 2, 3], operations.Select(item => item.RoutePosition).ToArray());
        Assert.Null(operations[0].PredecessorSourceKey);
        Assert.Equal(operations[0].SourceKey, operations[1].PredecessorSourceKey);
        Assert.Equal(operations[1].SourceKey, operations[2].PredecessorSourceKey);
        Assert.Equal(operations[2].SourceKey, operations[3].PredecessorSourceKey);

        var receipt = Assert.Single(plan.Requirements, item => item.StepNumber == 20);
        Assert.Equal(("BACKWARD", operations[0].SourceKey), (receipt.Direction, receipt.OperationSourceKey));
        var setup = Assert.Single(plan.Requirements, item => item.StepNumber == 80);
        Assert.Equal(("FORWARD", operations[2].SourceKey), (setup.Direction, setup.OperationSourceKey));
        var secondSetup = Assert.Single(plan.Requirements, item => item.StepNumber == 130);
        Assert.Equal(("FORWARD", operations[3].SourceKey), (secondSetup.Direction, secondSetup.OperationSourceKey));
        Assert.Null(secondSetup.PredecessorSourceKey);
    }

    [Fact]
    public void Steps_before_the_first_machining_step_chain_backwards_towards_the_machine()
    {
        var stations = Stations(
            Station(Receiving, "Receiving", "WORKSTATION", workstationTypeId: "type-a"),
            Station(SetupInspection, "Incoming inspection", "WORKSTATION", workstationTypeId: "type-a"),
            Station(Doosan, "DOOSAN-1", "MACHINE"));
        var steps = new[]
        {
            Step("P", 1, "10", Receiving, "Receive"),
            Step("P", 2, "20", SetupInspection, "Inspect incoming"),
            Step("P", 3, "30", Doosan, "Mill")
        };

        var plan = KitaronRoutePlanner.Plan(steps, stations, Parts("P"), Parts(), []);

        var receive = Assert.Single(plan.Requirements, item => item.StepNumber == 10);
        var inspect = Assert.Single(plan.Requirements, item => item.StepNumber == 20);
        Assert.Equal("BACKWARD", receive.Direction);
        Assert.Equal("BACKWARD", inspect.Direction);
        // The step next to the machine is anchored to it; the earlier step follows that one backwards.
        Assert.Null(inspect.PredecessorSourceKey);
        Assert.Equal(inspect.SourceKey, receive.PredecessorSourceKey);
        Assert.True(Index(plan.Requirements, inspect) < Index(plan.Requirements, receive));
    }

    [Fact]
    public void Undecided_stations_skip_their_steps_with_one_warning_per_station_and_ignore_is_silent()
    {
        var stations = Stations(
            Station(Doosan, "DOOSAN-1", "MACHINE"),
            Station(SetupInspection, "Inspection", "UNDECIDED"),
            Station(Packing, "Packing", "IGNORE"));
        var steps = new[]
        {
            Step("P", 1, "30", Doosan, "Mill"),
            Step("P", 2, "40", SetupInspection, "Inspect"),
            Step("P", 3, "50", SetupInspection, "Inspect again"),
            Step("P", 4, "60", Packing, "Pack"),
            Step("Q", 1, "30", Doosan, "Turn"),
            Step("Q", 2, "40", SetupInspection, "Inspect")
        };
        var warnings = new List<string>();

        var plan = KitaronRoutePlanner.Plan(steps, stations, Parts("P", "Q"), Parts(), warnings);

        Assert.Equal(2, plan.Operations.Count);
        Assert.Empty(plan.Requirements);
        Assert.Equal(3, plan.StepsSkipped);
        var warning = Assert.Single(warnings);
        Assert.Contains("'Inspection'", warning);
        Assert.Contains("3 route step(s)", warning);
    }

    [Fact]
    public void A_route_without_a_machining_step_imports_nothing_and_is_reported()
    {
        var stations = Stations(
            Station(SetupInspection, "Inspection", "WORKSTATION", workstationTypeId: "type-a"),
            Station(Packing, "Packing", "WORKSTATION", workstationTypeId: "type-a"));
        var steps = new[] { Step("P", 1, "10", SetupInspection, "Inspect"), Step("P", 2, "20", Packing, "Pack") };
        var warnings = new List<string>();

        var plan = KitaronRoutePlanner.Plan(steps, stations, Parts("P"), Parts(), warnings);

        Assert.Empty(plan.Operations);
        Assert.Empty(plan.Requirements);
        Assert.Equal(2, plan.StepsSkipped);
        Assert.Contains("P", plan.PartsWithRoute);
        Assert.Contains(warnings, warning => warning.Contains("no machining step", StringComparison.Ordinal));
    }

    [Fact]
    public void The_current_revision_header_wins_parts_outside_the_scope_are_left_out_and_assemblies_keep_their_route()
    {
        var stations = Stations(Station(Doosan, "DOOSAN-1", "MACHINE"));
        var steps = new[]
        {
            Step("P", 1, "30", Doosan, "Old route", headerId: 34, partRevision: "B", headerRevision: "A"),
            Step("P", 1, "35", Doosan, "Current route", headerId: 5586, partRevision: "B", headerRevision: "B"),
            Step("OUT", 1, "30", Doosan, "Not synchronized"),
            Step("PARENT", 1, "30", Doosan, "Assembly step")
        };
        var warnings = new List<string>();

        var plan = KitaronRoutePlanner.Plan(steps, stations, Parts("P", "PARENT"), Parts("PARENT"), warnings);

        var operation = Assert.Single(plan.Operations, item => item.CaseSourceKey == "P");
        Assert.Equal(35, operation.OperationNumber);
        Assert.Equal("Current route", operation.Name);
        var assembly = Assert.Single(plan.Operations, item => item.CaseSourceKey == "PARENT");
        Assert.Equal("Assembly step", assembly.Name);
        Assert.Contains("PARENT", plan.PartsWithRoute);
        Assert.DoesNotContain("OUT", plan.PartsWithRoute);
        Assert.DoesNotContain(warnings, warning => warning.StartsWith("PARENT is a parent Case", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_and_repeated_action_numbers_are_reported_and_the_first_row_is_kept()
    {
        var stations = Stations(Station(Doosan, "DOOSAN-1", "MACHINE", machineType: "Mill"));
        var steps = new[]
        {
            Step("P", 2, "30", Doosan, "Second by order", directionId: 2),
            Step("P", 1, "30", Doosan, "First by order", directionId: 1),
            Step("P", 3, "ABC", Doosan, "Not numeric"),
            Step("P", 4, "40", Doosan, "Next")
        };
        var warnings = new List<string>();

        var plan = KitaronRoutePlanner.Plan(steps, stations, Parts("P"), Parts(), warnings);

        Assert.Equal([30, 40], plan.Operations.Select(item => item.OperationNumber).ToArray());
        Assert.Equal("First by order", plan.Operations[0].Name);
        Assert.Contains(warnings, warning => warning.Contains("repeat an ActionNumber", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("no positive numeric ActionNumber", StringComparison.Ordinal));
    }

    [Fact]
    public void Station_role_suggestion_follows_the_kitaron_flags()
    {
        Assert.Equal(KitaronStationRoles.Machine, KitaronStationService.SuggestRole(114, 107, 0));
        Assert.Equal(KitaronStationRoles.Workstation, KitaronStationService.SuggestRole(17576, 55, 0));
        Assert.Equal(KitaronStationRoles.External, KitaronStationService.SuggestRole(17, 0, 15));
        Assert.Equal(KitaronStationRoles.Ignore, KitaronStationService.SuggestRole(0, 0, 0));
    }

    private static KitaronSourceRouteStep Step(
        string part, int order, string actionNumber, int stationId, string description,
        double? production = null, double? setup = null, int headerId = 1, string? partRevision = null,
        string? headerRevision = null, int? directionId = null) =>
        new(part, partRevision, headerId, headerRevision, false, false, null,
            directionId ?? order, order, actionNumber, description, null, stationId, false, production, setup, null);

    private static KitaronStationRecord Station(
        int id, string name, string role, string? machineType = null, string? workstationTypeId = null,
        string? externalResourceId = null, double perPart = 0, double perBatch = 0) =>
        new(id, name, null, false, 10, 0, 0, "WORKSTATION", role, machineType, workstationTypeId, externalResourceId,
            perPart, perBatch, 1, null, Seen, Seen, null, null, 1, Seen);

    private static IReadOnlyDictionary<int, KitaronStationRecord> Stations(params KitaronStationRecord[] stations) =>
        stations.ToDictionary(station => station.KitaronStationId);

    private static IReadOnlySet<string> Parts(params string[] parts) => parts.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int Index(IReadOnlyList<KitaronSyncRequirement> values, KitaronSyncRequirement value) =>
        values.ToList().IndexOf(value);
}
