using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DevCompanion.Agent.Services.Watcher;

public class GitDiffTracker
{
    private readonly ILogger<GitDiffTracker> _logger;
    private readonly Dictionary<string, bool> _isGitRepoCache = new();

    public GitDiffTracker(ILogger<GitDiffTracker> logger)
    {
        _logger = logger;
    }

    public async Task<string> GetDiffAsync(string projectRoot)
    {
        try
        {
            var modifiedFiles = await GetModifiedFilesAsync(projectRoot);
            foreach (var file in modifiedFiles)
            {
                var relativePath = Path.GetRelativePath(projectRoot, file);
                // Run git add -N for this C# file specifically to make it diffable if untracked
                await RunGitCommandAsync(projectRoot, $"add -N \"{relativePath}\"");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to run git add -N on modified files in {Path}", projectRoot);
        }

        return await RunGitCommandAsync(projectRoot, "diff");
    }

    public async Task<string> GetStagedDiffAsync(string projectRoot)
    {
        return await RunGitCommandAsync(projectRoot, "diff --staged");
    }

    public async Task<List<string>> GetModifiedFilesAsync(string projectRoot)
    {
        var statusOutput = await RunGitCommandAsync(projectRoot, "status --porcelain -u");
        if (string.IsNullOrWhiteSpace(statusOutput))
        {
            return new List<string>();
        }

        var files = new List<string>();
        var lines = statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Length > 3)
            {
                // git status --porcelain formats like:
                //  M path/to/file.cs
                // ?? path/to/newfile.cs
                var status = line.Substring(0, 2).Trim();
                var filePath = line.Substring(3).Trim();
                
                // Only track C# code files for our agent
                if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(Path.Combine(projectRoot, filePath));
                }
            }
        }

        return files;
    }

    private async Task<bool> IsGitRepositoryAsync(string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --is-inside-work-tree",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> RunGitCommandAsync(string workingDir, string arguments)
    {
        try
        {
            if (!Directory.Exists(workingDir))
            {
                _logger.LogWarning("Project root directory does not exist: {Path}", workingDir);
                return string.Empty;
            }

            if (!_isGitRepoCache.TryGetValue(workingDir, out var isGit))
            {
                // Verify worktree using git itself
                isGit = await IsGitRepositoryAsync(workingDir);
                _isGitRepoCache[workingDir] = isGit;
            }

            if (!isGit)
            {
                _logger.LogDebug("Directory is not inside a Git repository: {Path}", workingDir);
                return string.Empty;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await errorTask;
                // If it's a transient git add -N failure for files already added, fail silently
                if (arguments.StartsWith("add -N"))
                {
                    return string.Empty;
                }
                _logger.LogWarning("Git command 'git {Args}' failed: {Error}", arguments, error);
                return string.Empty;
            }

            return await outputTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing Git command 'git {Args}' in {Dir}", arguments, workingDir);
            return string.Empty;
        }
    }
}
