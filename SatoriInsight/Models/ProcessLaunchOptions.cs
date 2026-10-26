namespace SatoriInsight.Models;

// Launch settings stay in memory and are never included in session recordings.
public sealed record ProcessLaunchOptions(
    string Application, string Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> EnvironmentVariables);
