namespace Meimad.Planner.Client.Windows.Api;
internal sealed record HandoffWorker(string EmployeeId, string Name, string QualificationStamp);
internal sealed record PackageHandoff(string HandoffId, string PackageId, string EmployeeId, string EmployeeName,
    string Actor, DateTimeOffset RecordedAt, int Version, string QualificationStamp, ProductionPackageContext Context);
internal sealed record HandoffView(string PackageId, int Version, bool PackageIsCurrent, bool HandoffIsCurrent,
    IReadOnlyList<HandoffWorker> QualifiedWorkers, IReadOnlyList<PackageHandoff> History);
internal sealed record HandoffRequest(string RequestId, string EmployeeId, string QualificationStamp,
    int ExpectedVersion, ProductionPackageContext Context);
