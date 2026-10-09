using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Domain.ResourcePlanning;

namespace Meimad.Planner.Server.Tests.ResourcePlanning;

public sealed class ResourceSchedulerParityTests
{
    // Captured from the pre-index allocator. Full result signatures include assignment
    // order, exact instants, pins, explanations, load, issues and delivery predictions.
    [Fact]
    public void Reservation_index_preserves_recorded_allocator_results_and_inputs()
    {
        using var stream = typeof(ResourceSchedulerParityTests).Assembly.GetManifestResourceStream(
            "Meimad.Planner.Server.Tests.ResourcePlanning.resource-scheduler-baseline.txt")!;
        using var reader = new StreamReader(stream);
        var expected = reader.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(64, expected.Length);
        for (var seed = 0; seed < 64; seed++)
        {
            var input = Fixture(seed);
            var before = JsonSerializer.Serialize(input);
            var result = new AutomaticResourceScheduler().Calculate(input);
            Assert.Equal(before, JsonSerializer.Serialize(input));
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result))));
            Assert.True(expected[seed] == signature, $"Allocator changed for seed {seed}: {JsonSerializer.Serialize(result)}");
        }
    }

    [Fact]
    public void Many_candidate_pairs_preserve_ties_and_new_calculations_observe_changed_calendars()
    {
        var start = new DateTimeOffset(2026, 10, 8, 6, 0, 0, TimeSpan.Zero);
        ResourceAvailabilityWindow[] calendar = [new(start, start.AddHours(8))];
        var stations = Enumerable.Range(0, 35).Select(i => new ResourceCandidate($"s{i:D2}",
            ResourceBaseClass.Workstation, calendar, TypeId: "bench")).Reverse().ToArray();
        var employees = Enumerable.Range(0, 35).Select(i => new ResourceCandidate($"e{i:D2}",
            ResourceBaseClass.Employee, calendar, SkillIds: ["operate"])).Reverse().ToArray();
        var work = Enumerable.Range(0, 2).Select(i => new ResourceWorkItem($"work-{i}", TimeSpan.FromHours(1),
            ResourceScheduleDirection.Forward, start, new("bench", EmployeeSkillId: "operate"))).ToArray();
        var input = new ResourcePlanningInput(start, start.AddHours(8), work, stations, employees, []);
        var scheduler = new AutomaticResourceScheduler();
        var first = scheduler.Calculate(input);
        Assert.Equal(new[] { "s00", "s01" }, first.Assignments.Select(value => value.WorkstationId));
        Assert.Equal(new[] { "e00", "e01" }, first.Assignments.Select(value => value.EmployeeId));
        Assert.All(first.Assignments, value => Assert.Equal(start, value.StartsAt));
        calendar[0] = new(start.AddHours(3), start.AddHours(8));
        var second = scheduler.Calculate(input);
        Assert.All(second.Assignments, value => Assert.Equal(start.AddHours(3), value.StartsAt));
        Assert.Equal(first.Assignments.Select(value => value.WorkstationId), second.Assignments.Select(value => value.WorkstationId));
        Assert.Empty(second.BlockingConfigurationErrors);
    }

    private static ResourcePlanningInput Fixture(int seed)
    {
        var random = new Random(seed);
        var start = new DateTimeOffset(2026, 10, 8, 6, 0, 0, TimeSpan.Zero);
        ResourceAvailabilityWindow[] Calendar(int offset) =>
        [new(start.AddMinutes(offset), start.AddHours(4)),
            new(start.AddHours(5), start.AddHours(9)),
            new(start.AddDays(1), start.AddDays(1).AddHours(8))];
        var stations = Enumerable.Range(0, 3).Select(i => new ResourceCandidate($"s{i}",
            ResourceBaseClass.Workstation, Calendar(i * 10), i + 1, "bench", ["measure"])).ToArray();
        var employees = Enumerable.Range(0, 4).Select(i => new ResourceCandidate($"e{i}",
            ResourceBaseClass.Employee, Calendar(i * 5), SkillIds: i == 3 ? ["inspect"] : ["operate", "inspect"])).ToArray();
        var reservations = new List<ExistingResourceReservation>();
        for (var i = 0; i < 20; i++)
        {
            var employee = i % 2 == 0;
            var at = start.AddMinutes(random.Next(-30, 1000));
            reservations.Add(new($"fixed-{i}", employee ? $"e{i % 4}" : $"s{i % 3}",
                employee ? ResourceBaseClass.Employee : ResourceBaseClass.Workstation,
                at, at.AddMinutes(random.Next(0, 90)), 1, true));
        }
        // Unrelated resource boundaries and duplicate reservations must not alter semantics.
        reservations.Add(new("other", "unrelated", ResourceBaseClass.Machine, start.AddMinutes(17), start.AddHours(3)));
        reservations.Add(reservations[0]);
        var work = new List<ResourceWorkItem>();
        for (var i = 0; i < 48; i++)
        {
            var requirement = (i % 8) switch
            {
                0 => new ResourceWorkRequirement(ExternalResourceId: "outside"),
                1 => new ResourceWorkRequirement(EmployeeSkillId: "inspect"),
                2 => new ResourceWorkRequirement("bench", WorkstationCapacity: 2),
                3 => new ResourceWorkRequirement(EmployeeSkillId: "missing"),
                4 => new ResourceWorkRequirement(),
                _ => new ResourceWorkRequirement("bench", "measure", "operate", 1)
            };
            var anchor = start.AddMinutes(random.Next(0, 1500));
            work.Add(new($"work-{i:D3}", TimeSpan.FromMinutes(i % 11 == 0 ? 0 : random.Next(5, 100)),
                i % 3 == 0 ? ResourceScheduleDirection.Backward : ResourceScheduleDirection.Forward,
                anchor, requirement, i % 5 == 0 && i > 0 ? $"work-{i - 1:D3}" : null,
                start.AddDays(1), PinnedWorkstationId: i % 13 == 0 && requirement.WorkstationTypeId is not null ? "s1" : null,
                PinnedEmployeeId: i % 7 == 0 && requirement.EmployeeSkillId is not null ? "e1" : null,
                PinnedStartsAt: i % 9 == 0 ? anchor : null));
        }
        if (seed % 2 == 0) { work.Reverse(); Array.Reverse(stations); Array.Reverse(employees); }
        return new(start, start.AddDays(2), work, stations, employees,
            [new("outside", TimeSpan.FromHours(3), TimeSpan.FromMinutes(30), false)], reservations);
    }
}
