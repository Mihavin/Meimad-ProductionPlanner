using Meimad.Planner.Server.Domain.ResourcePlanning;

namespace Meimad.Planner.Server.Tests.ResourcePlanning;

public sealed class AutomaticResourceSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026,9,1,6,0,0,TimeSpan.Zero);
    private static readonly ResourceAvailabilityWindow Shift = new(Start,Start.AddHours(12));
    private static ResourceCandidate Station(string id, int capacity = 1, string type = "manual") =>
        new(id,ResourceBaseClass.Workstation,[Shift],capacity,type);
    private static ResourceCandidate Employee(string id, params string[] skills) =>
        new(id,ResourceBaseClass.Employee,[Shift],1,SkillIds:skills);
    private static ResourceWorkItem Work(string id, DateTimeOffset anchor, string? dependency = null,
        ResourceScheduleDirection direction = ResourceScheduleDirection.Forward) =>
        new(id,TimeSpan.FromHours(1),direction,anchor,new("manual",EmployeeSkillId:"operate"),dependency);

    [Fact]
    public void Capacity_one_workstation_blocks_second_job_even_with_two_employees()
    {
        var result = Calculate([Work("a",Start),Work("b",Start)],[Station("station")],[Employee("e1","operate"),Employee("e2","operate")]);
        Assert.Equal(Start,result.Assignments[0].StartsAt);
        Assert.Equal(Start.AddHours(1),result.Assignments[1].StartsAt);
    }

    [Fact]
    public void One_employee_blocks_two_workstations()
    {
        var result = Calculate([Work("a",Start),Work("b",Start)],[Station("s1"),Station("s2")],[Employee("e1","operate")]);
        Assert.Equal(Start.AddHours(1),result.Assignments[1].StartsAt);
    }

    [Fact]
    public void Two_workstations_and_employees_run_in_parallel_and_ties_are_stable()
    {
        var result = Calculate([Work("a",Start),Work("b",Start)],[Station("s2"),Station("s1")],[Employee("e2","operate"),Employee("e1","operate")]);
        Assert.All(result.Assignments,value => Assert.Equal(Start,value.StartsAt));
        Assert.Equal("s1",result.Assignments.Single(value=>value.WorkId=="a").WorkstationId);
        Assert.Equal("e1",result.Assignments.Single(value=>value.WorkId=="a").EmployeeId);
    }

    [Fact]
    public void Another_eligible_employee_is_selected_when_first_is_reserved()
    {
        var fixedWork = new ExistingResourceReservation("confirmed","e1",ResourceBaseClass.Employee,Start,Start.AddHours(2));
        var input = Input([Work("a",Start)],[Station("s1")],[Employee("e1","operate"),Employee("e2","operate")]) with { FixedReservations=[fixedWork] };
        Assert.Equal("e2",new AutomaticResourceScheduler().Calculate(input).Assignments.Single().EmployeeId);
    }

    [Fact]
    public void Preparation_is_latest_fit_backward_and_post_work_is_earliest_forward()
    {
        var machineStart=Start.AddHours(6);
        var preparation=Work("prepare",machineStart,direction:ResourceScheduleDirection.Backward);
        var post=Work("post",machineStart,"prepare",ResourceScheduleDirection.Forward);
        var result=Calculate([preparation,post],[Station("s1")],[Employee("e1","operate")]);
        Assert.Equal(machineStart, result.Assignments.Single(x=>x.WorkId=="prepare").EndsAt);
        Assert.Equal(machineStart, result.Assignments.Single(x=>x.WorkId=="post").StartsAt);
    }

    [Fact]
    public void Missing_skill_is_configuration_error_not_contention_conflict()
    {
        var result=Calculate([Work("a",Start)],[Station("s1")],[Employee("e1","different")]);
        Assert.Empty(result.Assignments);
        Assert.Equal("qualified_employee_missing",result.BlockingConfigurationErrors.Single().Code);
    }

    [Fact]
    public void Preparation_contention_predicts_machine_shift_instead_of_resource_conflict()
    {
        var anchor=Start.AddHours(2);
        var fixedStation=new ExistingResourceReservation("fixed","s1",ResourceBaseClass.Workstation,Start,anchor);
        var fixedEmployee=new ExistingResourceReservation("fixed","e1",ResourceBaseClass.Employee,Start,anchor);
        var result=new AutomaticResourceScheduler().Calculate(Input(
            [Work("prepare",anchor,direction:ResourceScheduleDirection.Backward)],
            [Station("s1")],[Employee("e1","operate")]) with { FixedReservations=[fixedStation,fixedEmployee] });
        Assert.Empty(result.BlockingConfigurationErrors);
        Assert.Equal(anchor,result.Assignments.Single().StartsAt);
        Assert.Equal(TimeSpan.FromHours(1),result.PredictedShift);
    }

    [Fact]
    public void External_lead_and_buffer_move_timeline_without_internal_load()
    {
        var external=new ExternalResourceCandidate("supplier",TimeSpan.FromDays(2),TimeSpan.FromDays(1),false);
        var work=new ResourceWorkItem("outside",TimeSpan.Zero,ResourceScheduleDirection.Forward,Start,new(ExternalResourceId:"supplier"));
        var result=new AutomaticResourceScheduler().Calculate(Input([work],[],[]) with { ExternalResources=[external] });
        Assert.Equal(Start.AddDays(3),result.Assignments.Single().EndsAt);
        Assert.Empty(result.Load);
    }

    [Fact]
    public void Delivery_risk_is_evaluated_after_feasible_completion()
    {
        var work=Work("a",Start) with { RequiredDeliveryAt=Start.AddMinutes(30) };
        var result=Calculate([work],[Station("s1")],[Employee("e1","operate")]);
        Assert.True(result.DeliveryAtRisk);
        Assert.Equal(Start.AddHours(1),result.PredictedCompletion);
    }

    [Fact]
    public void Pinned_assignment_is_a_constraint_and_confirmed_reservation_is_not_moved()
    {
        var fixedWork=new ExistingResourceReservation("actual","e1",ResourceBaseClass.Employee,Start,Start.AddHours(1));
        var work=Work("a",Start) with { PinnedEmployeeId="e1",PinnedStartsAt=Start.AddHours(1) };
        var result=new AutomaticResourceScheduler().Calculate(Input([work],[Station("s1")],[Employee("e1","operate")]) with { FixedReservations=[fixedWork] });
        Assert.True(result.Assignments.Single().IsPinned);
        Assert.Equal(Start.AddHours(1),result.Assignments.Single().StartsAt);
        Assert.Contains(result.Load,value=>value.WorkId=="actual" && value.StartsAt==Start);
    }

    private static ResourcePlanningResult Calculate(IReadOnlyList<ResourceWorkItem> work,
        IReadOnlyList<ResourceCandidate> stations,IReadOnlyList<ResourceCandidate> employees) =>
        new AutomaticResourceScheduler().Calculate(Input(work,stations,employees));
    private static ResourcePlanningInput Input(IReadOnlyList<ResourceWorkItem> work,
        IReadOnlyList<ResourceCandidate> stations,IReadOnlyList<ResourceCandidate> employees) =>
        new(Start,Start.AddHours(12),work,stations,employees,[]);
}
