using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisplaySwitcher.Config;

public sealed class AppSettings
{
    public int VerifyDelaySeconds { get; set; } = 2;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 5;
    public bool EnableDdcCiEscalation { get; set; } = true;
    public int LogRetentionDays { get; set; } = 7;

    /// <summary>Seconds the monitor is held in DDC/CI standby before being powered back on.</summary>
    public int DdcCiPowerCycleSeconds { get; set; } = 3;

    /// <summary>Seconds to wait after a DDC/CI power-on before re-verifying, to allow the EDID handshake.</summary>
    public int DdcCiSettleSeconds { get; set; } = 5;

    [JsonIgnore]
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppSettings Load(string path, out string? loadWarning)
    {
        loadWarning = null;

        if (!File.Exists(path))
        {
            loadWarning = $"settings.json not found at {path}; using built-in defaults.";
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Clamp();
            return settings;
        }
        catch (Exception ex)
        {
            loadWarning = $"Failed to parse {path} ({ex.Message}); using built-in defaults.";
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        Clamp();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private void Clamp()
    {
        VerifyDelaySeconds = Math.Clamp(VerifyDelaySeconds, 0, 60);
        MaxRetries = Math.Clamp(MaxRetries, 1, 10);
        RetryDelaySeconds = Math.Clamp(RetryDelaySeconds, 0, 60);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 365);
        DdcCiPowerCycleSeconds = Math.Clamp(DdcCiPowerCycleSeconds, 1, 30);
        DdcCiSettleSeconds = Math.Clamp(DdcCiSettleSeconds, 1, 60);
    }

    public string Describe() =>
        $"verifyDelay={VerifyDelaySeconds}s maxRetries={MaxRetries} retryDelay={RetryDelaySeconds}s " +
        $"ddcCi={(EnableDdcCiEscalation ? "on" : "off")} logRetention={LogRetentionDays}d";
}
