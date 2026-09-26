using Xunit;

// Disable parallel test execution across classes because WPF Application.Current is an AppDomain singleton.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
