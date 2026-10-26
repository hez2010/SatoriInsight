namespace SatoriInsight.Tests;

internal static class TestPaths
{
    public static string ManagedWorkload(string framework) =>
        Path.Combine(AppContext.BaseDirectory, "Workloads", framework, "SatoriInsight.Workload.dll");

    public static string SatoriWorkload => Path.Combine(AppContext.BaseDirectory, "Workloads", "Satori", "SatoriInsight.SatoriWorkload.exe");

    public static string Artifacts => Path.Combine(AppContext.BaseDirectory, "TestResults");
}
