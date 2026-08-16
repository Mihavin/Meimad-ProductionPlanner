using Xunit;

// WPF controls and the OpenCascade native runtime both own process-global UI/native state.
// Serial execution keeps the STA startup/render checks deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
