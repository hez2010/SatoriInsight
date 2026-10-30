namespace SatoriInsight.Tests;

internal static class TestPaths
{
    public static string ManagedWorkload(string framework) =>
        Path.Combine(AppContext.BaseDirectory, "Workloads", framework, "SatoriInsight.Workload.dll");

    public static string SatoriWorkload => Path.Combine(AppContext.BaseDirectory, "Workloads", "Satori",
        OperatingSystem.IsWindows() ? "SatoriInsight.SatoriWorkload.exe" : "SatoriInsight.SatoriWorkload");
}
