using Xunit;

// The desktop suite drives REAL WPF windows and REAL UI Automation: both are
// process-global (one desktop, one UIA tree). Running such tests in parallel makes
// them collide — a window created by one test is visible to another's UIA query,
// and failures land in whichever test loses the race. Serialising the assembly is
// the honest fix: the whole suite runs in about a second anyway.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
