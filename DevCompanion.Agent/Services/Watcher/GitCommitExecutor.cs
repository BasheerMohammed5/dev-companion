using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DevCompanion.Agent.Services.Watcher;

public static class GitCommitExecutor
{
    private static readonly Regex CommitMsgRegex = new(@"\[COMMIT_MSG\](?<msg>.*?)\[/COMMIT_MSG\]", RegexOptions.Compiled | RegexOptions.Singleline);

    public static async Task<bool> CommitLastDiffAsync(string projectRoot)
    {
        var autologPath = Path.Combine(projectRoot, "AI_AUTOLOG.md");
        if (!File.Exists(autologPath))
        {
            Console.WriteLine("❌ Error: AI_AUTOLOG.md not found in the current directory.");
            Console.WriteLine("Please run 'dev-companion' and make some changes first to generate the audit log.");
            return false;
        }

        var content = await File.ReadAllTextAsync(autologPath);
        var match = CommitMsgRegex.Match(content);
        var commitMessage = string.Empty;

        if (match.Success)
        {
            commitMessage = match.Groups["msg"].Value.Trim();
        }
        else
        {
            // Fallback parsing (look for a line that starts with 'feat', 'fix', 'refactor', etc.)
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var cleanLine = line.Trim('*', ' ', '`');
                if (cleanLine.StartsWith("feat", StringComparison.OrdinalIgnoreCase) ||
                    cleanLine.StartsWith("fix", StringComparison.OrdinalIgnoreCase) ||
                    cleanLine.StartsWith("refactor", StringComparison.OrdinalIgnoreCase) ||
                    cleanLine.StartsWith("chore", StringComparison.OrdinalIgnoreCase) ||
                    cleanLine.StartsWith("docs", StringComparison.OrdinalIgnoreCase))
                {
                    commitMessage = cleanLine;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            Console.WriteLine("❌ Error: No AI-suggested commit message found in AI_AUTOLOG.md.");
            return false;
        }

        Console.WriteLine($"💡 AI Suggested Commit Message: \"{commitMessage}\"");
        Console.WriteLine("Committing changes to local repository...");

        // 1. Run 'git add .'
        var addResult = await RunCommandAsync(projectRoot, "git", "add .");
        if (!addResult)
        {
            Console.WriteLine("❌ Error: 'git add .' failed.");
            return false;
        }

        // Escape double quotes in commit message
        var escapedMessage = commitMessage.Replace("\"", "\\\"");

        // 2. Run 'git commit -m "[msg]"'
        var commitResult = await RunCommandAsync(projectRoot, "git", $"commit -m \"{escapedMessage}\"");
        if (commitResult)
        {
            Console.WriteLine("🚀 Success! Changes committed successfully with AI message.");
            return true;
        }

        Console.WriteLine("❌ Error: 'git commit' failed. Are there any unstaged changes to commit?");
        return false;
    }

    private static async Task<bool> RunCommandAsync(string workingDir, string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
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
}
