using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests;

public sealed class ProductionRunDialogViewModelTests
{
    [Fact]
    public void Coupled_outputs_require_exact_equal_cycle_counts()
    {
        var model=new ProductionRunDialogViewModel([Operation("a",20),Operation("b",10)]);
        Configure(model.Outputs[0],1,"program","revision","output-a",2,20);
        Configure(model.Outputs[1],1,"program","revision","output-b",1,9);
        Assert.False(model.CanSave);
        Assert.Contains("same cycle count",model.ValidationMessage,StringComparison.OrdinalIgnoreCase);
        model.Outputs[1].TargetQuantity=10;
        Assert.True(model.CanSave);
        var request=model.CreateRequest();Assert.Single(request.Programs);Assert.Equal(2,request.Programs[0].Outputs.Count);
    }

    [Fact]
    public void Independent_programs_may_have_different_cycle_counts_and_read_only_never_saves()
    {
        var model=new ProductionRunDialogViewModel([Operation("a",10),Operation("b",4)]);
        Configure(model.Outputs[0],1,"program-a","revision-a","output-a",1,10);
        Configure(model.Outputs[1],2,"program-b","revision-b","output-b",1,4);
        Assert.True(model.CanSave);Assert.Equal(2,model.CreateRequest().Programs.Count);
        var readOnly=new ProductionRunDialogViewModel([Operation("a",10)],true);
        Configure(readOnly.Outputs[0],1,"program","revision","output",1,10);
        Assert.False(readOnly.CanSave);
    }

    private static void Configure(ProductionRunOutputEditor row,int group,string program,string revision,string output,int perCycle,long target)
    {row.ProgramGroup=group;row.ManufacturingProgramId=program;row.ProcessRevisionId=revision;row.RevisionOutputId=output;row.QuantityPerCycle=perCycle;row.TargetQuantity=target;row.CycleSeconds=5;}
    private static PlanningBoardOperation Operation(string id,int quantity)=>new(id,$"batch-{id}",$"B-{id}",$"case-{id}",$"PART-{id}",10,"Mill","mill",0,5,"not_started",null,null,quantity);
}
