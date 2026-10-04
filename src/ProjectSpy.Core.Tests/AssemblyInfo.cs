using Xunit;

// The suite runs one test at a time on purpose.
//
// The stage-4a sweep asserts a per-site generation budget of 10 ms, and that assertion
// is only meaningful if the machine is not also running five hundred other tests across
// every core. Under xUnit's default parallel collections the sweep competes with the
// rest of the suite, and the slowest sample measures Windows' scheduler rather than the
// generator — which is how a test that exists to catch a real performance regression
// ends up failing on a busy CI agent and being ignored.
//
// The suite is small enough (well under a minute) that serialising it costs almost
// nothing, and a timing assertion that only passes on an idle machine is worse than no
// timing assertion at all. If a future stage needs the parallelism back, the honest fix
// is to move the timing assertion into its own benchmark project rather than to let it
// share a run with correctness tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]