using System;
using System.IO;
using System.Text.Json;

namespace AEVRIX.Desktop;

internal static class StartupFailureReporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static void TryWrite(string stage, Exception exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AEVRIX",
                "Diagnostics");
            Directory.CreateDirectory(root);

            var payload = new
            {
                schemaVersion = 1,
                recordedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                stage = SanitizeStage(stage),
                exceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                hresult = $"0x{exception.HResult:X8}"
            };

            var path = Path.Combine(root, "startup-failure.json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, SerializerOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Diagnostics must never weaken the fail-closed startup path.
        }
    }

    private static string SanitizeStage(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return "unknown";
        }

        return stage switch
        {
            "app-initialize" => stage,
            "app-launch" => stage,
            "unhandled-ui" => stage,
            "main-window-initialize" => stage,
            "main-window-activate" => stage,
            "project-credentials-initialize" => stage,
            "research-browser-initialize" => stage,
            _ => "unknown"
        };
    }
}
