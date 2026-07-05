using System.Text.Json;
using DevCompanion.Agent.Configuration;
using DevCompanion.Agent.Services.Analyzer;
using DevCompanion.Agent.Services.Notification;
using DevCompanion.Agent.Services.Watcher;

if (args.Contains("commit"))
{
    var currentDir = Directory.GetCurrentDirectory();
    var success = await GitCommitExecutor.CommitLastDiffAsync(currentDir);
    return success ? 0 : 1;
}

var builder = Host.CreateApplicationBuilder(args);

// Explicitly load the tool's own appsettings.json from the installation folder
var assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
if (!string.IsNullOrEmpty(assemblyDir))
{
    var globalSettingsPath = Path.Combine(assemblyDir, "appsettings.json");
    if (File.Exists(globalSettingsPath))
    {
        builder.Configuration.AddJsonFile(globalSettingsPath, optional: true, reloadOnChange: true);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
    }
}

var webSocketLogQueue = new DevCompanion.Agent.Integration.WebSocketLogQueue();
builder.Services.AddSingleton(webSocketLogQueue);
builder.Logging.AddProvider(new DevCompanion.Agent.Integration.WebSocketLoggerProvider(webSocketLogQueue));

// Configure Settings
builder.Services.Configure<AgentSettings>(
    builder.Configuration.GetSection("AgentSettings"));

// Post-configure to dynamically discover C# projects in the current working directory
builder.Services.PostConfigure<AgentSettings>(settings =>
{
    var currentDir = Directory.GetCurrentDirectory();
    var hasCsharpProject = Directory.GetFiles(currentDir, "*.csproj").Any() || 
                           Directory.GetFiles(currentDir, "*.sln").Any();

    if (hasCsharpProject)
    {
        if (!settings.Projects.Any(p => p.RootPath.Equals(currentDir, StringComparison.OrdinalIgnoreCase)))
        {
            var dynamicProject = new ProjectConfig
            {
                RootPath = currentDir,
                EnableGitTracking = true,
                EnableShadowTesting = true
            };

            // Attempt to parse local launchSettings.json to find correct HTTP port
            var launchSettingsPath = Path.Combine(currentDir, "Properties", "launchSettings.json");
            if (File.Exists(launchSettingsPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
                    if (doc.RootElement.TryGetProperty("profiles", out var profiles))
                    {
                        foreach (var profile in profiles.EnumerateObject())
                        {
                            if (profile.Value.TryGetProperty("applicationUrl", out var urlProp))
                            {
                                var urls = urlProp.GetString()?.Split(';');
                                var httpUrl = urls?.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
                                if (!string.IsNullOrEmpty(httpUrl))
                                {
                                    dynamicProject.LocalhostUrl = httpUrl;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Fail silently, use default localhost
                }
            }

            settings.Projects.Add(dynamicProject);
        }
    }
});

// Register Services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GitDiffTracker>();
builder.Services.AddTransient<ReportGenerator>();
builder.Services.AddTransient<WhatsAppService>();
builder.Services.AddTransient<TelegramService>();
builder.Services.AddTransient<ILlmService, LlmService>();
builder.Services.AddTransient<ApiScanner>();
builder.Services.AddTransient<CodeReviewer>();
builder.Services.AddTransient<IAdvancedFeaturesService, AdvancedFeaturesService>();

// Register Hosted Services
builder.Services.AddHostedService<LogListenerService>();
builder.Services.AddHostedService<ProjectWatchdog>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var advancedService = scope.ServiceProvider.GetRequiredService<IAdvancedFeaturesService>();
    var currentDir = Directory.GetCurrentDirectory();

    if (args.Contains("score"))
    {
        await advancedService.CalculateTechnicalScorecardAsync(currentDir);
        advancedService.PrintTechnicalDebtHeatmap(currentDir);
        return 0;
    }

    if (args.Contains("improve") || args.Contains("fix"))
    {
        await advancedService.RunAutonomousRepairAsync(currentDir);
        return 0;
    }

    if (args.Contains("ask"))
    {
        var questionIdx = Array.IndexOf(args, "ask") + 1;
        var question = questionIdx < args.Length ? string.Join(" ", args.Skip(questionIdx)) : "Explain project state";
        await advancedService.AnswerHistoryQuestionAsync(currentDir, question);
        return 0;
    }

    // Parse features from CLI arguments if present
    var featuresIdx = Array.IndexOf(args, "--features");
    if (featuresIdx < 0) featuresIdx = Array.IndexOf(args, "-f");

    if (featuresIdx >= 0 && featuresIdx + 1 < args.Length)
    {
        var featuresArg = args[featuresIdx + 1];
        var parts = featuresArg.Split(',');
        var argsActiveIds = new HashSet<int>();
        foreach (var part in parts)
        {
            if (int.TryParse(part.Trim(), out var id) && id >= 1 && id <= 11)
            {
                argsActiveIds.Add(id);
            }
        }
        var argsOptions = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSettings>>();
        argsOptions.Value.ActiveFeatureIds = argsActiveIds;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nActivated {argsActiveIds.Count} feature(s) from arguments. Starting DevCompanion daemon...");
        Console.ResetColor();
        
        host.Run();
        return 0;
    }

    // Show Interactive Launcher Menu
    try { Console.Clear(); } catch {}

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("=========================================================");
    Console.WriteLine("       🚀 DEVCOMPANION INTERACTIVE LAUNCHER 🚀          ");
    Console.WriteLine("=========================================================");
    Console.ResetColor();
    Console.WriteLine("Available features:\n");
    Console.WriteLine("[1] Live Code Review & Syntax Check (FileSystemWatcher + LLM)");
    Console.WriteLine("[2] Startup Project Structure Audit (AI_PROJECT_AUDIT)");
    Console.WriteLine("[3] API Shadow Testing & Fuzzing (Swagger monitoring)");
    Console.WriteLine("[4] Architecture Drift Detection (Detect Layer Boundary Violations)");
    Console.WriteLine("[5] AI Root Cause Exception Engine (DI & Runtime Analyzer)");
    Console.WriteLine("[6] Technical Debt Heatmap Generator");
    Console.WriteLine("[7] AI Memory & Context Preservation (.ai/context.json)");
    Console.WriteLine("[8] Technical Scorecard (dev-companion score)");
    Console.WriteLine("[9] Autonomous Repair Mode (dev-companion fix / improve)");
    Console.WriteLine("[10] Project Knowledge Graph Builder");
    Console.WriteLine("[11] Conversation with Code History (dev-companion ask)");

    var activeIds = new HashSet<int>();
    string? choice = null;
    var isRedirected = Console.IsInputRedirected || Console.IsOutputRedirected;

    if (isRedirected)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n⚠️ Redirected terminal detected (Visual Studio PMC). Keyboard input is disabled.");
        Console.WriteLine("👉 To customize active features, launch with arguments, for example:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   dev-companion -f 1,4\n");
        Console.ResetColor();
        Console.WriteLine("Auto-activating all features...");
        activeIds = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    }
    else
    {
        Console.Write("\nEnter choice (comma-separated, e.g. '1,4' or 'all') [default: all]: ");
        try
        {
            choice = Console.ReadLine();
        }
        catch
        {
            choice = "all";
        }

        if (string.IsNullOrWhiteSpace(choice) || choice.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            activeIds = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        }
        else
        {
            var parts = choice.Split(',');
            foreach (var part in parts)
            {
                if (int.TryParse(part.Trim(), out var id) && id >= 1 && id <= 11)
                {
                    activeIds.Add(id);
                }
            }
        }
    }

    var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSettings>>();
    options.Value.ActiveFeatureIds = activeIds;

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\nActivated {activeIds.Count} feature(s). Starting DevCompanion daemon...");
    Console.ResetColor();
}

host.Run();
return 0;
