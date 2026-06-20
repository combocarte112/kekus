using GoldSrcProbe.Config;
using GoldSrcProbe.Models;
using GoldSrcProbe.Protocol;
using GoldSrcProbe.Services;

namespace GoldSrcProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var exitCode = await RunAsync(args);
        PauseBeforeExit(args);
        return exitCode;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var baseDir = FindBaseDirectory();
        Directory.SetCurrentDirectory(baseDir);

        var configPath = Path.Combine(baseDir, "config.json");
        var config = AppConfig.Load(configPath);

        string? singleHost = null;
        var joinMode = false;
        var checkConnect = false;
        string? manualChallenge = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--join":
                    joinMode = true;
                    break;
                case "--verify":
                    config.Mode = "verify";
                    break;
                case "--check-connect":
                    checkConnect = true;
                    break;
                case "--challenge" when i + 1 < args.Length:
                    manualChallenge = args[++i];
                    break;
                case "--hold" when i + 1 < args.Length:
                    config.JoinHoldSeconds = int.Parse(args[++i]);
                    break;
                case "--config" when i + 1 < args.Length:
                    config = AppConfig.Load(args[++i]);
                    break;
                case "--servers" when i + 1 < args.Length:
                    config.ServersFile = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    config.VerifyOutputFile = args[++i];
                    config.OutputFile = args[++i];
                    break;
                case "--mode" when i + 1 < args.Length:
                    config.Mode = args[++i];
                    break;
                case "--host" when i + 1 < args.Length:
                    singleHost = args[++i];
                    break;
                case "--a2s-only":
                    config.Mode = "a2s";
                    break;
                case "--connect-only":
                    config.Mode = "connect";
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                case "--name" when i + 1 < args.Length:
                    config.PlayerName = args[++i];
                    break;
                case "--auth" when i + 1 < args.Length:
                    config.AuthEmulator = args[++i];
                    break;
                case "--debug-net":
                    GoldSrcClient.DebugNet = true;
                    break;
                case "--no-pause":
                    break;
                case "--pause":
                    break;
            }
        }

        List<ServerEndpoint> servers;
        if (singleHost is not null)
            servers = [ServerEndpoint.Parse(singleHost)];
        else
            servers = ProbeRunner.LoadServers(Path.Combine(baseDir, config.ServersFile));

        if (servers.Count == 0)
        {
            Console.Error.WriteLine("No servers in list. Edit servers.txt or use --host ip:port");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (checkConnect)
        {
            var server = servers[0];
            Console.WriteLine($"=== CHECK CONNECT === {server.Key}");
            using var client = new GoldSrcClient(config.PlayerName, config.ConnectTimeoutMs, config);
            var reach = await client.ProbeConnectAsync(server, cts.Token);
            Console.WriteLine(reach.Reachability switch
            {
                ConnectReachability.Open => $"OK — challenge {reach.Challenge}",
                ConnectReachability.A2SShield => $"SHIELD — {reach.Summary}",
                _ => "FAIL — no getchallenge response"
            });
            return reach.CanJoin ? 0 : 1;
        }

        if (joinMode)
        {
            var server = servers[0];
            Console.WriteLine($"=== JOIN === {config.PlayerName} | hold {config.JoinHoldSeconds}s");
            using var client = new GoldSrcClient(config.PlayerName, config.ConnectTimeoutMs, config);
            var (ok, err) = await client.JoinAndHoldAsync(server, config.JoinHoldSeconds, cts.Token, manualChallenge);
            Console.WriteLine(ok ? "JOIN OK" : $"JOIN FAILED: {err}");
            return ok ? 0 : 1;
        }

        var mode = config.Mode.ToLowerInvariant();
        if (mode is "verify" or "connect")
        {
            var verify = new VerifyRunner(config);
            try
            {
                var report = await verify.RunAsync(servers, cts.Token);
                await verify.SaveReportAsync(report, cts.Token);
                var joined = report.Servers.Count(s => s.Joined);
                var fake = report.Servers.Count(s => s.Summary?.LikelyFakeServer == true);
                Console.WriteLine($"Done. Joined: {joined}/{report.Servers.Count} | Likely fake: {fake}");
                return joined > 0 ? 0 : 1;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 2;
            }
        }

        var runner = new ProbeRunner(config);
        try
        {
            var report = await runner.RunAsync(servers, cts.Token);
            await runner.SaveReportAsync(report, cts.Token);
            Console.WriteLine();
            Console.WriteLine($"Done. A2S online: {report.Servers.Count(s => s.Online)}/{report.Servers.Count}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 2;
        }
    }

    private static string FindBaseDirectory()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            if (File.Exists(Path.Combine(dir, "config.json")) || File.Exists(Path.Combine(dir, "servers.txt")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        var devRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(devRoot, "config.json")))
            return devRoot;

        return Directory.GetCurrentDirectory();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
GoldSrcProbe — CS 1.6 join bot (verify real players via HLDS status)

Primary use:
  dotnet run -- --verify --host IP:27015
  Bot joins server, runs "status", extracts steamid / ping / time per player.

Options:
  --verify             Join + status + fake detection (default mode)
  --host ip:port       Single server
  --servers file.txt   Server list
  --output out.json    Verify JSON path
  --join --hold 30     Stay connected (TAB test)
  --check-connect      Test getchallenge only
  --mode a2s|both      Legacy A2S query modes (optional)
  --name MsBoostProbe  Bot name
  --debug-net          Verbose netchan log
  --no-pause           Exit without prompt
""");
    }

    private static void PauseBeforeExit(string[] args)
    {
        if (args.Contains("--no-pause"))
            return;
        if (!Environment.UserInteractive || Console.IsInputRedirected)
            return;
        Console.WriteLine();
        Console.WriteLine("Apasa Enter...");
        _ = Console.ReadLine();
    }
}
