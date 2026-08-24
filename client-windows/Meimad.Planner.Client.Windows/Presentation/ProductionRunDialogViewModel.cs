using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class ProductionRunDialogViewModel : INotifyPropertyChanged
{
    private int sharedSetupSeconds;
    private string validationMessage = string.Empty;
    internal ProductionRunDialogViewModel(IEnumerable<PlanningBoardOperation> operations, bool readOnly = false)
    {
        IsReadOnly = readOnly;
        var index=0;
        foreach(var operation in operations)
        {
            var row=new ProductionRunOutputEditor(operation,++index); row.PropertyChanged += (_,_)=>Validate(); Outputs.Add(row);
        }
        Validate();
    }
    internal ProductionRunDialogViewModel(IEnumerable<PlanningOperationViewModel> operations, bool readOnly = false)
    {
        IsReadOnly=readOnly;var index=0;
        foreach(var operation in operations){var row=new ProductionRunOutputEditor(operation,++index);row.PropertyChanged+=(_,_)=>Validate();Outputs.Add(row);}Validate();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ProductionRunOutputEditor> Outputs { get; }=[];
    public bool IsReadOnly { get; }
    public int SharedSetupSeconds { get=>sharedSetupSeconds; set { sharedSetupSeconds=value; Changed(); Validate(); } }
    public string ValidationMessage { get=>validationMessage; private set { validationMessage=value; Changed(); } }
    public bool CanSave => !IsReadOnly && ValidationMessage.Length==0 && Outputs.Count>0;

    internal ProductionRunCreate CreateRequest()
    {
        Validate(); if(!CanSave)throw new InvalidOperationException(ValidationMessage);
        var programs=Outputs.GroupBy(row=>row.ProgramGroup).OrderBy(group=>group.Key).Select((group,sequence)=>
        {
            var first=group.First();
            return new ProductionRunProgramCreate(first.ManufacturingProgramId.Trim(),first.ProcessRevisionId.Trim(),
                string.IsNullOrWhiteSpace(first.GCodeReleaseId)?null:first.GCodeReleaseId.Trim(),sequence,first.CycleSeconds,
                group.Select(row=>new ProductionRunOutputCreate(row.RevisionOutputId.Trim(),row.BatchOperationId,row.TargetQuantity)).ToArray());
        }).ToArray();
        return new(SharedSetupSeconds,"{\"source\":\"windows-production-run-dialog\"}",programs);
    }

    private void Validate()
    {
        string? error=null;
        if(SharedSetupSeconds<0)error="Shared setup cannot be negative.";
        foreach(var group in Outputs.GroupBy(row=>row.ProgramGroup))
        {
            if(group.Any(row=>string.IsNullOrWhiteSpace(row.ManufacturingProgramId)||string.IsNullOrWhiteSpace(row.ProcessRevisionId)||string.IsNullOrWhiteSpace(row.RevisionOutputId))) { error="Select a Manufacturing Program, revision, and revision output for every row."; break; }
            if(group.Any(row=>row.QuantityPerCycle<=0||row.TargetQuantity<=0||row.TargetQuantity%row.QuantityPerCycle!=0)){error="Every target must be positive and exactly divisible by quantity per cycle.";break;}
            if(group.Select(row=>row.TargetQuantity/row.QuantityPerCycle).Distinct().Count()!=1){error="Coupled outputs in one program must require the same cycle count.";break;}
            if(group.Select(row=>row.ManufacturingProgramId.Trim()).Distinct(StringComparer.Ordinal).Count()!=1||group.Select(row=>row.ProcessRevisionId.Trim()).Distinct(StringComparer.Ordinal).Count()!=1){error="Rows in one program group must select the same program and revision.";break;}
        }
        ValidationMessage=error??string.Empty;Changed(nameof(CanSave));
    }
    private void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}

internal sealed class ProductionRunOutputEditor : INotifyPropertyChanged
{
    private int group;private string program="";private string revision="";private string release="";private string output="";private int quantityPerCycle=1;private long target;private decimal cycleSeconds;
    internal ProductionRunOutputEditor(PlanningBoardOperation operation,int group){Operation=operation;this.group=group;target=operation.RemainingProductionQuantity??operation.PlannedQuantity;cycleSeconds=(decimal)(operation.PlanningCycleTimePerPartSeconds??operation.CycleTimePerPartSeconds??0);}
    internal ProductionRunOutputEditor(PlanningOperationViewModel operation,int group)
    {
        Operation=new(operation.BatchOperationId,operation.BatchId,operation.BatchNumber,operation.CaseId,operation.PartNumber,
            operation.OperationNumber,operation.OperationName,operation.RequiredMachineType,null,null,operation.Status,operation.MachineId,
            operation.BacklogPosition,operation.PlannedQuantity,operation.OrderReferences,operation.EstimatedTimeSeconds,
            RemainingProductionQuantity:operation.RemainingProductionQuantity,PlanningCycleTimePerPartSeconds:operation.PlanningCycleTimePerPartSeconds);
        this.group=group;target=operation.RemainingProductionQuantity??operation.PlannedQuantity;cycleSeconds=(decimal)(operation.PlanningCycleTimePerPartSeconds??0);
    }
    public event PropertyChangedEventHandler? PropertyChanged; public PlanningBoardOperation Operation{get;} public string BatchOperationId=>Operation.BatchOperationId;
    public string Display=>$"{Operation.PartNumber} / {Operation.BatchNumber} / OP{Operation.OperationNumber}";
    public int ProgramGroup{get=>group;set=>Set(ref group,value);} public string ManufacturingProgramId{get=>program;set=>Set(ref program,value);}
    public string ProcessRevisionId{get=>revision;set=>Set(ref revision,value);}public string GCodeReleaseId{get=>release;set=>Set(ref release,value);}
    public string RevisionOutputId{get=>output;set=>Set(ref output,value);}public int QuantityPerCycle{get=>quantityPerCycle;set=>Set(ref quantityPerCycle,value);}
    public long TargetQuantity{get=>target;set=>Set(ref target,value);}public decimal CycleSeconds{get=>cycleSeconds;set=>Set(ref cycleSeconds,value);}
    public long? RequiredCycles=>QuantityPerCycle>0&&TargetQuantity>0&&TargetQuantity%QuantityPerCycle==0?TargetQuantity/QuantityPerCycle:null;
    private void Set<T>(ref T field,T value,[CallerMemberName]string? name=null){if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;PropertyChanged?.Invoke(this,new(name));PropertyChanged?.Invoke(this,new(nameof(RequiredCycles)));}
}
