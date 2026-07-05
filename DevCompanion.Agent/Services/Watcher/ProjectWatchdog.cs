using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DevCompanion.Agent.Configuration;
using DevCompanion.Agent.Services.Analyzer;
using DevCompanion.Agent.Services.Notification;

namespace DevCompanion.Agent.Services.Watcher;

public class ProjectWatchdog : BackgroundService
{
    private readonly AgentSettings _settings;
    private readonly GitDiffTracker _gitDiffTracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProjectWatchdog> _logger;

    private readonly Dictionary<string, string> _lastSwaggerHashes = new();
    private readonly Dictionary<string, string> _lastGitDiffs = new();
    private readonly Dictionary<string, bool> _projectPortWasOnline = new();
    private readonly HashSet<string> _initialAuditedProjects = new();
    private readonly Dictionary<string, DateTime> _lastGitReviewTimes = new();
    private readonly Dictionary<string, FileSystemWatcher> _fileWatchers = new();
    private readonly Dictionary<string, DateTime> _lastFileChangeTimes = new();

    public ProjectWatchdog(
        IOptions<AgentSettings> settings,
        GitDiffTracker gitDiffTracker,
        IServiceScopeFactory scopeFactory,
        HttpClient httpClient,
        ILogger<ProjectWatchdog> logger)
    {
        _settings = settings.Value;
        _gitDiffTracker = gitDiffTracker;
        _scopeFactory = scopeFactory;
        _httpClient = httpClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Project Watchdog Service started.");

        if (_settings.ActiveFeatureIds.Contains(1))
        {
            // Initialize File System Watchers for real-time C# file tracking
            foreach (var project in _settings.Projects)
            {
                if (project.EnableGitTracking && !string.IsNullOrEmpty(project.RootPath) && Directory.Exists(project.RootPath))
                {
                    try
                    {
                        var watcher = new FileSystemWatcher(project.RootPath, "*.cs")
                        {
                            IncludeSubdirectories = true,
                            EnableRaisingEvents = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                        };

                        watcher.Changed += (sender, e) => OnFileChanged(project, e.FullPath);
                        watcher.Created += (sender, e) => OnFileChanged(project, e.FullPath);
                        watcher.Renamed += (sender, e) => OnFileChanged(project, e.FullPath);

                        _fileWatchers[project.RootPath] = watcher;
                        _logger.LogInformation("File System Watcher active for: {Path}", project.RootPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start FileSystemWatcher for {Path}", project.RootPath);
                    }
                }
            }
        }
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var project in _settings.Projects)
                {
                    await WatchProjectAsync(project, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Watchdog loop execution.");
            }

            // Poll every 10 seconds for Swagger/shadow testing
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        // Clean up watchers on stop
        foreach (var watcher in _fileWatchers.Values)
        {
            try { watcher.Dispose(); } catch { }
        }
        _logger.LogInformation("Project Watchdog Service stopping.");
    }

    private void OnFileChanged(ProjectConfig project, string filePath)
    {
        _logger.LogInformation("FileSystemWatcher: Captured change event on file: {File}", Path.GetFileName(filePath));

        // Ignore temporary files, safe-save leftovers, or files not ending with exactly .cs
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || 
            filePath.Contains('~') || 
            filePath.Contains(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (filePath.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
            filePath.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
            filePath.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) ||
            filePath.Contains("AI_PROJECT_AUDIT.md") ||
            filePath.Contains("AI_AUTOLOG.md") ||
            filePath.Contains("AI_MODIFICATIONS.md") ||
            filePath.Contains("AI_CODE_ERRORS.md"))
        {
            return;
        }

        var now = DateTime.UtcNow;
        _lastFileChangeTimes.TryGetValue(project.RootPath, out var lastChangeTime);
        if (now - lastChangeTime < TimeSpan.FromMilliseconds(1500))
        {
            return;
        }
        _lastFileChangeTimes[project.RootPath] = now;

        _logger.LogInformation("FileSystemWatcher: Detected save in C# file: {File}. Triggering code review...", Path.GetFileName(filePath));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                await MonitorGitDiffAsync(project, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running git diff scan after filesystem event.");
            }
        });
    }

    private async Task WatchProjectAsync(ProjectConfig project, CancellationToken stoppingToken)
    {
        var projectRoot = project.RootPath;
        if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot))
        {
            _logger.LogWarning("Configured project directory does not exist: {Path}", projectRoot);
            return;
        }

        // Trigger one-time initial project folder structure & architecture audit on startup
        if (_settings.ActiveFeatureIds.Contains(2) && !_initialAuditedProjects.Contains(projectRoot))
        {
            _initialAuditedProjects.Add(projectRoot);
            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var reviewer = scope.ServiceProvider.GetRequiredService<CodeReviewer>();
                await SafeExecuteTaskAsync("Initial Project Audit", projectRoot, async () =>
                {
                    await reviewer.PerformInitialAuditAsync(projectRoot);
                });
            }, stoppingToken);
        }

        // 1. API Port & Swagger monitoring
        if (_settings.ActiveFeatureIds.Contains(3) && project.EnableShadowTesting)
        {
            await MonitorApiAndSwaggerAsync(project);
        }

        // Run fallback safety check every 2 minutes in case FileSystemWatcher missed anything
        _lastGitReviewTimes.TryGetValue(projectRoot, out var lastReviewTime);
        if (project.EnableGitTracking && DateTime.UtcNow - lastReviewTime > TimeSpan.FromMinutes(2))
        {
            await MonitorGitDiffAsync(project);
        }
    }

    private async Task MonitorApiAndSwaggerAsync(ProjectConfig project)
    {
        var isOnline = await CheckPortOnlineAsync(project.LocalhostUrl);
        var wasOnline = _projectPortWasOnline.TryGetValue(project.RootPath, out var online) && online;
        _projectPortWasOnline[project.RootPath] = isOnline;

        if (!isOnline)
        {
            if (wasOnline)
            {
                _logger.LogInformation("Project API at {Url} went offline.", project.LocalhostUrl);
            }
            return;
        }

        if (!wasOnline)
        {
            _logger.LogInformation("Project API at {Url} detected ONLINE! Starting shadow testing check...", project.LocalhostUrl);
        }

        // Read swagger to check for endpoint changes
        var swaggerUrl = $"{project.LocalhostUrl.TrimEnd('/')}{project.SwaggerPath}";
        try
        {
            var response = await _httpClient.GetAsync(swaggerUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var currentHash = ComputeHash(content);

                _lastSwaggerHashes.TryGetValue(project.RootPath, out var lastHash);

                if (currentHash != lastHash || !wasOnline)
                {
                    _logger.LogInformation("Swagger content changed or API restarted. Re-running Shadow Endpoint Tests...");
                    _lastSwaggerHashes[project.RootPath] = currentHash;

                    // Trigger scanning in scope
                    _ = Task.Run(async () =>
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var apiScanner = scope.ServiceProvider.GetRequiredService<ApiScanner>();
                        await SafeExecuteTaskAsync("API Shadow Fuzzing", project.RootPath, async () =>
                        {
                            await apiScanner.ScanProjectApiAsync(project.RootPath, project.LocalhostUrl, project.SwaggerPath);
                        });
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to fetch Swagger JSON from {Url}: {Message}", swaggerUrl, ex.Message);
        }
    }

    private async Task MonitorGitDiffAsync(ProjectConfig project, string? targetFile = null)
    {
        var diff = await _gitDiffTracker.GetDiffAsync(project.RootPath);
        var stagedDiff = await _gitDiffTracker.GetStagedDiffAsync(project.RootPath);
        
        // Merge diffs
        var combinedDiff = $"{diff}\n{stagedDiff}".Trim();

        if (string.IsNullOrWhiteSpace(combinedDiff))
        {
            _lastGitDiffs.Remove(project.RootPath);
            return;
        }

        _lastGitDiffs.TryGetValue(project.RootPath, out var lastDiff);

        if (combinedDiff != lastDiff)
        {
            _lastGitReviewTimes.TryGetValue(project.RootPath, out var lastReviewTime);
            if (DateTime.UtcNow - lastReviewTime < TimeSpan.FromSeconds(3))
            {
                _logger.LogInformation("Git changes detected for {Project}, but skipping review due to 3-second debounce cooldown.", Path.GetFileName(project.RootPath));
                return;
            }

            _lastGitReviewTimes[project.RootPath] = DateTime.UtcNow;
            _logger.LogInformation("Detected Git code changes in {Project}. Triggering Code Reviewer...", Path.GetFileName(project.RootPath));
            _lastGitDiffs[project.RootPath] = combinedDiff;

            // Run review in background
            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var reviewer = scope.ServiceProvider.GetRequiredService<CodeReviewer>();
                await SafeExecuteTaskAsync("Git Code Review", project.RootPath, async () =>
                {
                    await reviewer.ReviewLocalChangesAsync(project.RootPath, combinedDiff, targetFile);
                });
            });
        }
    }

    private async Task<bool> CheckPortOnlineAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await _httpClient.GetAsync(url, cts.Token);
            return true; // If we get any response, server is up
        }
        catch
        {
            return false;
        }
    }

    private string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private async Task SafeExecuteTaskAsync(string taskName, string projectPath, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during execution of task '{TaskName}' for project at {Path}", taskName, projectPath);
            
            // Send warning alert to Telegram/WhatsApp
            var alertMsg = $"⚠️ *DevCompanion Warning: [{taskName}] failed!*\n" +
                           $"Project: {Path.GetFileName(projectPath)}\n" +
                           $"Error details: {ex.Message}\n\n" +
                           $"The background watchdog service remains active.";
                           
            using var scope = _scopeFactory.CreateScope();
            var telegram = scope.ServiceProvider.GetRequiredService<TelegramService>();
            var whatsapp = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
            
            try
            {
                await Task.WhenAll(
                    telegram.SendNotificationAsync(alertMsg),
                    whatsapp.SendNotificationAsync(alertMsg)
                );
            }
            catch
            {
                // Fail silently to prevent cascade crash loops on network down
            }
        }
    }
}
