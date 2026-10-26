using System.ComponentModel;
using System.Runtime.CompilerServices;
using SatoriInsight.Models;
using SatoriInsight.Services.Processes;

namespace SatoriInsight.ViewModels;

public sealed class LaunchProcessViewModel : INotifyPropertyChanged
{
    private string _application = "";
    private string _arguments = "";
    private string _workingDirectory = "";
    private string _environmentVariables = "";
    private string _error = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Application
    {
        get => _application;
        set => Update(ref _application, value);
    }

    public string Arguments
    {
        get => _arguments;
        set => Update(ref _arguments, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => Update(ref _workingDirectory, value);
    }

    public string EnvironmentVariables
    {
        get => _environmentVariables;
        set => Update(ref _environmentVariables, value);
    }

    public string Error
    {
        get => _error;
        set
        {
            _error = value;
            Notify();
            Notify(nameof(HasError));
        }
    }

    public bool HasError => Error.Length != 0;

    public bool CanLaunch => !string.IsNullOrWhiteSpace(Application);

    public ProcessLaunchOptions? CreateOptions()
    {
        try
        {
            var environment = ParseEnvironmentVariables();
            var options = new ProcessLaunchOptions(Application, Arguments, WorkingDirectory, environment);
            LocalProcessLauncher.CreateStartInfo(options); // Validate before closing the dialog or changing the current recording.
            Error = "";
            return options;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Error = exception.Message;
            return null;
        }
    }

    private Dictionary<string, string> ParseEnvironmentVariables()
    {
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var lines = EnvironmentVariables.Replace("\r\n", "\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var equals = line.IndexOf('=');

            if (equals < 1 || string.IsNullOrWhiteSpace(line[..equals]))
            {
                throw new ArgumentException($"Environment variable on line {index + 1} must use NAME=value.");
            }

            var name = line[..equals].Trim();

            if (!environment.TryAdd(name, line[(equals + 1)..]))
            {
                throw new ArgumentException($"{name} is listed more than once.");
            }
        }

        return environment;
    }

    private void Update(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Notify(name);
        Error = "";
        Notify(nameof(CanLaunch));
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
