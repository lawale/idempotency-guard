using BenchmarkDotNet.Running;

var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

// BenchmarkDotNet reports build and run failures in its output but still exits 0,
// which would leave a scheduled job green with nothing measured.
return summaries.Any() && summaries.All(s => !s.HasCriticalValidationErrors && s.Reports.All(r => r.Success) && s.Reports.Any()) ? 0 : 1;

/// <summary>Entry point marker for <see cref="BenchmarkSwitcher"/>.</summary>
public partial class Program;
