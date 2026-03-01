using System.Diagnostics;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Creates / queries / removes a Windows Task Scheduler entry named
/// "AvoTelemetryAgent" using schtasks.exe (no external packages).
/// </summary>
public sealed class WindowsAutostartService
{
    private const string TaskName = "AvoTelemetryAgent";

    private readonly AgentConfigService            _configService;
    private readonly ILogger<WindowsAutostartService> _log;

    public WindowsAutostartService(
        AgentConfigService               configService,
        ILogger<WindowsAutostartService> log)
    {
        _configService = configService;
        _log           = log;
    }

    public bool IsEnabled()
    {
        if (!_configService.Current.Agent.AllowAutostartOperations) return false;
        var (exit, _) = Run($"/query /tn \"{TaskName}\"");
        return exit == 0;
    }

    public async Task EnableAsync()
    {
        if (!_configService.Current.Agent.AllowAutostartOperations)
        {
            _log.LogWarning("Autostart operations are disabled by config");
            return;
        }

        var exePath = Environment.ProcessPath
                   ?? Process.GetCurrentProcess().MainModule?.FileName
                   ?? string.Empty;

        if (string.IsNullOrWhiteSpace(exePath))
        {
            _log.LogError("Cannot resolve executable path for autostart task");
            return;
        }

        // /f overwrites if already exists.
        var (exit, output) = await RunAsync(
            $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\"\" /sc ONLOGON /rl HIGHEST /f");

        if (exit == 0)
            _log.LogInformation("Autostart task created");
        else
            _log.LogWarning("schtasks /create exit {Exit}: {Output}", exit, output);
    }

    public async Task DisableAsync()
    {
        if (!_configService.Current.Agent.AllowAutostartOperations)
        {
            _log.LogWarning("Autostart operations are disabled by config");
            return;
        }

        var (exit, output) = await RunAsync($"/delete /tn \"{TaskName}\" /f");

        if (exit == 0)
            _log.LogInformation("Autostart task removed");
        else
            _log.LogWarning("schtasks /delete exit {Exit}: {Output}", exit, output);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static (int Exit, string Output) Run(string args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("schtasks.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            },
        };
        proc.Start();
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, output.Trim());
    }

    private static async Task<(int Exit, string Output)> RunAsync(string args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("schtasks.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            },
        };
        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync()
                   + await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, output.Trim());
    }
}
