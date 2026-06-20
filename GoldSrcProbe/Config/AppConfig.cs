using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoldSrcProbe.Config;

public sealed class AppConfig
{
    public string PlayerName { get; set; } = "MsBoostProbe";
    public int ConnectTimeoutMs { get; set; } = 60000;
    /// <summary>Extra connect attempts after first failure (0 = no retry).</summary>
    public int ConnectRetryCount { get; set; } = 1;
    /// <summary>Pause before each connect retry (ms). Keep ≥15s to avoid query-flood bans.</summary>
    public int ConnectRetryDelayMs { get; set; } = 20000;
    public int A2STimeoutMs { get; set; } = 4000;
    public int DelayBetweenServersMs { get; set; } = 500;
    public string OutputFile { get; set; } = "output/status_probe.json";
    /// <summary>JSON output for --verify (join + status players).</summary>
    public string VerifyOutputFile { get; set; } = "output/verify_players.json";
    public string ServersFile { get; set; } = "servers.txt";
    /// <summary>a2s | connect | both | verify (verify = join+status only)</summary>
    public string Mode { get; set; } = "verify";
    public string AuthEmulator { get; set; } = "auto";
    public int JoinHoldSeconds { get; set; } = 30;
    /// <summary>Path to CS 1.6 install (cstrike/valve) for Rechecker MD5 hashes.</summary>
    public string? CsGamePath { get; set; }
    /// <summary>UDP bind port like CS client (default 27005). 0 = random.</summary>
    public int BindPort { get; set; } = 27005;

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return new AppConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions()) ?? new AppConfig();
    }

    public static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
