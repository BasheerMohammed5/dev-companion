using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
namespace DevCompanion.Agent.Services.Analyzer;

public class ScorecardData
{
    public string Score { get; set; } = "--";
    public int ViolationsCount { get; set; } = 0;
    public string InterfacesRatio { get; set; } = "0%";
    public string ComplexityIndex { get; set; } = "Normal";
    public Dictionary<string, int> Heatmap { get; set; } = new();
}

public interface IAdvancedFeaturesService
{
    Task CalculateTechnicalScorecardAsync(string projectRoot);
    void PrintTechnicalDebtHeatmap(string projectRoot);
    List<string> CheckArchitectureDrift(string filePath, string fileContent);
    Task<string> CheckAiMemoryRulesAsync(string projectRoot, string filePath, string fileContent);
    Task RunAutonomousRepairAsync(string projectRoot);
    Task AnswerHistoryQuestionAsync(string projectRoot, string question);
    ScorecardData GetScorecardAndHeatmap(string projectRoot);
    Task<bool> ApplyTargetedFixAsync(string projectRoot, string relativePath, string errorDetails);
}

public class AdvancedFeaturesService : IAdvancedFeaturesService
{
    private readonly ILogger<AdvancedFeaturesService> _logger;
    private readonly ILlmService _llm;

    public AdvancedFeaturesService(ILogger<AdvancedFeaturesService> logger, ILlmService llm)
    {
        _logger = logger;
        _llm = llm;
    }

    public async Task CalculateTechnicalScorecardAsync(string projectRoot)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n=========================================================");
        Console.WriteLine("          📊 DEVCOMPANION ARCHITECTURE SCORECARD 📊       ");
        Console.WriteLine("=========================================================");
        Console.ResetColor();

        var csFiles = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin"))
            .ToList();

        int totalLoc = 0;
        int interfaceCount = 0;
        int classCount = 0;
        int boundaryViolations = 0;

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            totalLoc += lines.Length;

            var content = string.Join("\n", lines);
            if (content.Contains("interface ")) interfaceCount++;
            else if (content.Contains("class ")) classCount++;

            // Count namespace boundary drifts
            var drifts = CheckArchitectureDrift(file, content);
            boundaryViolations += drifts.Count;
        }

        // Calculate metrics
        double avgLocPerFile = csFiles.Count > 0 ? (double)totalLoc / csFiles.Count : 0;
        double couplingRatio = (classCount + interfaceCount) > 0 ? (double)interfaceCount / (classCount + interfaceCount) : 0;
        
        int maintainabilityScore = Math.Max(0, 100 - (int)(avgLocPerFile / 10));
        int couplingScore = Math.Min(100, (int)(couplingRatio * 150)); // Target 66% interfaces/classes for 100
        int cleanArchitectureScore = Math.Max(0, 100 - (boundaryViolations * 15));

        int finalScore = (int)((maintainabilityScore * 0.3) + (couplingScore * 0.3) + (cleanArchitectureScore * 0.4));

        string finalGrade = finalScore switch
        {
            >= 95 => "A+",
            >= 90 => "A",
            >= 80 => "B",
            >= 70 => "C",
            _ => "D"
        };

        Console.WriteLine($"Total C# Files Checked : {csFiles.Count}");
        Console.WriteLine($"Total Lines of Code    : {totalLoc} LOC");
        Console.WriteLine($"Average File Length    : {avgLocPerFile:F1} lines");
        Console.WriteLine($"Boundary Violations    : {boundaryViolations}");
        Console.WriteLine("---------------------------------------------------------");
        
        Console.Write("Maintainability Index  : ");
        PrintScoreBar(maintainabilityScore);
        Console.Write("Coupling & Abstraction : ");
        PrintScoreBar(couplingScore);
        Console.Write("Clean Arch Boundaries  : ");
        PrintScoreBar(cleanArchitectureScore);
        
        Console.WriteLine("---------------------------------------------------------");
        Console.ForegroundColor = finalScore >= 80 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"OVERALL ARCHITECTURE SCORE: {finalScore}/100 ({finalGrade})");
        Console.ResetColor();
        Console.WriteLine("=========================================================\n");
    }

    public void PrintTechnicalDebtHeatmap(string projectRoot)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n=========================================================");
        Console.WriteLine("            🔥 TECHNICAL DEBT HEATMAP 🔥                 ");
        Console.WriteLine("=========================================================");
        Console.ResetColor();

        var layers = new Dictionary<string, (int files, int loc, double complexity)>
        {
            { "Domain", (0, 0, 1.0) },
            { "Application", (0, 0, 1.0) },
            { "Infrastructure", (0, 0, 1.0) },
            { "API / Presentation", (0, 0, 1.0) }
        };

        var csFiles = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin"))
            .ToList();

        foreach (var file in csFiles)
        {
            string layerName = "Domain";
            if (file.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)) layerName = "Infrastructure";
            else if (file.Contains("Application", StringComparison.OrdinalIgnoreCase)) layerName = "Application";
            else if (file.Contains("Controllers", StringComparison.OrdinalIgnoreCase) || file.Contains("API", StringComparison.OrdinalIgnoreCase)) layerName = "API / Presentation";

            var lines = File.ReadAllLines(file);
            var stats = layers[layerName];
            
            // Basic cyclomatic complexity estimate (counting branching statements)
            double fileComplexity = lines.Count(l => l.Contains("if (") || l.Contains("foreach (") || l.Contains("while (") || l.Contains(" && ") || l.Contains(" || ")) * 1.5 + 1;

            layers[layerName] = (stats.files + 1, stats.loc + lines.Length, stats.complexity + fileComplexity);
        }

        foreach (var pair in layers)
        {
            double avgComplexity = pair.Value.files > 0 ? pair.Value.complexity / pair.Value.files : 1;
            int debtPercentage = Math.Min(100, (int)(avgComplexity * 10));

            Console.Write($"{pair.Key.PadRight(20)}: ");
            PrintHeatBar(debtPercentage);
            Console.WriteLine($" {debtPercentage}% Debt Risk ({pair.Value.loc} LOC)");
        }
        Console.WriteLine("=========================================================\n");
    }

    public List<string> CheckArchitectureDrift(string filePath, string fileContent)
    {
        var violations = new List<string>();
        var fileName = Path.GetFileName(filePath);

        // Core Domain should have zero application/infrastructure dependencies
        if (filePath.Contains("Domain", StringComparison.OrdinalIgnoreCase))
        {
            if (fileContent.Contains("using SmartMeetingAssistant.Application") || 
                fileContent.Contains("using SmartMeetingAssistant.Infrastructure"))
            {
                violations.Add($"Domain boundary violation: '{fileName}' references Application or Infrastructure layer directly.");
            }
        }

        // Application layer should have zero direct infrastructure references
        if (filePath.Contains("Application", StringComparison.OrdinalIgnoreCase))
        {
            if (fileContent.Contains("using SmartMeetingAssistant.Infrastructure"))
            {
                violations.Add($"Application boundary violation: '{fileName}' references Infrastructure layer directly. Introduce an abstraction interface instead.");
            }
        }

        // Domain & Application layers should not reference Presentation/Controllers
        if (filePath.Contains("Domain", StringComparison.OrdinalIgnoreCase) || 
            filePath.Contains("Application", StringComparison.OrdinalIgnoreCase))
        {
            if (fileContent.Contains("using Microsoft.AspNetCore.Mvc") || fileContent.Contains("using Controllers"))
            {
                violations.Add($"Business Logic boundary violation: '{fileName}' references Web MVC/Controller types.");
            }
        }

        return violations;
    }

    public async Task<string> CheckAiMemoryRulesAsync(string projectRoot, string filePath, string fileContent)
    {
        var contextPath = Path.Combine(projectRoot, ".ai", "context.json");
        if (!File.Exists(contextPath)) return string.Empty;

        try
        {
            var contextJson = File.ReadAllText(contextPath);
            var systemPrompt = @"You are a strict project-specific architecture guidelines validator.
You will receive the local project design decisions / context rules in JSON, and the content of a saved C# file.
Check if this code violates any naming conventions, decisions, or architectural rules specified in the JSON context.
If there are any violations, state them briefly. If everything complies, output ONLY 'OK'.";

            var userPrompt = $"[PROJECT CONTEXT RULES]\n{contextJson}\n\n[FILE PATH]\n{filePath}\n\n[FILE CONTENT]\n{fileContent}";
            var response = await _llm.ChatAsync(systemPrompt, userPrompt);

            if (response.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to evaluate AI memory rules.");
            return string.Empty;
        }
    }

    public async Task RunAutonomousRepairAsync(string projectRoot)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n=========================================================");
        Console.WriteLine("        🔧 DEVCOMPANION AUTONOMOUS REPAIR MODE 🔧         ");
        Console.WriteLine("=========================================================");
        Console.ResetColor();
        Console.WriteLine("Scanning workspace for compiler diagnostics...");

        // Simple mock implementation of repair pipeline:
        // Reads target file, analyzes error, runs LLM, builds target
        var errors = await RunDotnetBuildAsync(projectRoot);
        if (errors.Count == 0)
        {
            Console.WriteLine("No compiler errors detected! Your project builds successfully.");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Detected {errors.Count} build error(s). Starting repair...");
        Console.ResetColor();

        var firstError = errors.First();
        Console.WriteLine($"Target File: {firstError.FilePath}");
        Console.WriteLine($"Error Description: {firstError.Message}");

        if (File.Exists(firstError.FilePath))
        {
            var originalContent = File.ReadAllText(firstError.FilePath);
            var repairPrompt = $"The following file fails to compile with error: {firstError.Message}. Rewrite the file to fix the compilation error. Return the complete corrected code file contents.";
            
            var repairedCode = await _llm.ChatAsync("You are a C# compiler auto-repair bot. Fix compilation issues and output the complete clean file content. Output ONLY code, no conversational text.", originalContent + "\n\n" + repairPrompt);

            // Clean code fences if LLM wrapped it
            repairedCode = CleanCodeBlock(repairedCode);

            File.WriteAllText(firstError.FilePath, repairedCode);
            Console.WriteLine("Patch applied. Verifying rebuild...");

            var newErrors = await RunDotnetBuildAsync(projectRoot);
            if (newErrors.Count < errors.Count)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✔ Repair successful! Build error resolved.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("⚠ Patch applied but compiler errors remain. Rolling back file changes...");
                Console.ResetColor();
                File.WriteAllText(firstError.FilePath, originalContent);
            }
        }
    }

    public async Task AnswerHistoryQuestionAsync(string projectRoot, string question)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\nQuerying Git and Documentation logs to answer: '{question}'...");
        Console.ResetColor();

        // Query last 10 git logs to collect context
        var gitHistory = "";
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.Arguments = "log -n 10 --oneline";
            process.StartInfo.WorkingDirectory = projectRoot;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            gitHistory = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
        catch {}

        var systemPrompt = @"You are a Git History Q&A agent.
Using the provided recent Git commits, answer the developer's question about the project history accurately.";
        
        var userPrompt = $"[RECENT COMMITS]\n{gitHistory}\n\n[DEVELOPER QUESTION]\n{question}";
        var answer = await _llm.ChatAsync(systemPrompt, userPrompt);
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n[AI HISTORY RESPONSE]");
        Console.ResetColor();
        Console.WriteLine(answer);
        Console.WriteLine();
    }

    private void PrintScoreBar(int score)
    {
        var color = score >= 90 ? ConsoleColor.Green : (score >= 70 ? ConsoleColor.Yellow : ConsoleColor.Red);
        Console.ForegroundColor = color;
        
        int filled = score / 5;
        Console.Write("[");
        Console.Write(new string('█', filled));
        Console.Write(new string('░', 20 - filled));
        Console.Write($"] {score}%\n");
        Console.ResetColor();
    }

    private void PrintHeatBar(int percentage)
    {
        var color = percentage >= 70 ? ConsoleColor.Red : (percentage >= 40 ? ConsoleColor.Yellow : ConsoleColor.Green);
        Console.ForegroundColor = color;

        int filled = percentage / 10;
        Console.Write("[");
        Console.Write(new string('█', filled));
        Console.Write(new string('░', 10 - filled));
        Console.Write("]");
        Console.ResetColor();
    }

    private async Task<List<BuildError>> RunDotnetBuildAsync(string projectRoot)
    {
        var errors = new List<BuildError>();
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.Arguments = "build --no-incremental";
            process.StartInfo.WorkingDirectory = projectRoot;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var matches = Regex.Matches(output, @"(?<file>[^(]+)\((?<line>\d+),\d+\): error (?<code>\w+): (?<msg>.+)");
            foreach (Match match in matches)
            {
                var filePath = match.Groups["file"].Value.Trim();
                if (!Path.IsPathRooted(filePath))
                {
                    filePath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
                }

                errors.Add(new BuildError
                {
                    FilePath = filePath,
                    Message = match.Value
                });
            }
        }
        catch {}
        return errors;
    }

    private string CleanCodeBlock(string input)
    {
        if (input.StartsWith("```"))
        {
            var lines = input.Split('\n').ToList();
            if (lines.Count > 2)
            {
                lines.RemoveAt(0);
                if (lines.Last().StartsWith("```"))
                {
                    lines.RemoveAt(lines.Count - 1);
                }
                return string.Join("\n", lines);
            }
        }
        return input;
    }

    public ScorecardData GetScorecardAndHeatmap(string projectRoot)
    {
        var data = new ScorecardData();

        try
        {
            var csFiles = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("obj") && !f.Contains("bin"))
                .ToList();

            int totalLoc = 0;
            int interfaceCount = 0;
            int classCount = 0;
            int boundaryViolations = 0;

            foreach (var file in csFiles)
            {
                var lines = File.ReadAllLines(file);
                totalLoc += lines.Length;

                var content = string.Join("\n", lines);
                if (content.Contains("interface ")) interfaceCount++;
                else if (content.Contains("class ")) classCount++;

                var drifts = CheckArchitectureDrift(file, content);
                boundaryViolations += drifts.Count;
            }

            double avgLocPerFile = csFiles.Count > 0 ? (double)totalLoc / csFiles.Count : 0;
            double couplingRatio = (classCount + interfaceCount) > 0 ? (double)interfaceCount / (classCount + interfaceCount) : 0;
            
            int maintainabilityScore = Math.Max(0, 100 - (int)(avgLocPerFile / 10));
            int couplingScore = Math.Min(100, (int)(couplingRatio * 150));
            int cleanArchitectureScore = Math.Max(0, 100 - (boundaryViolations * 15));

            int finalScore = (int)((maintainabilityScore * 0.3) + (couplingScore * 0.3) + (cleanArchitectureScore * 0.4));

            data.Score = finalScore.ToString();
            data.ViolationsCount = boundaryViolations;
            data.InterfacesRatio = $"{(int)(couplingRatio * 100)}%";
            data.ComplexityIndex = avgLocPerFile > 300 ? "High" : avgLocPerFile > 150 ? "Medium" : "Normal";

            // Heatmap calculation
            var layers = new Dictionary<string, (int files, int loc, double complexity)>
            {
                { "Domain", (0, 0, 1.0) },
                { "Application", (0, 0, 1.0) },
                { "Infrastructure", (0, 0, 1.0) },
                { "API / Presentation", (0, 0, 1.0) }
            };

            foreach (var file in csFiles)
            {
                string layerName = "Domain";
                if (file.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)) layerName = "Infrastructure";
                else if (file.Contains("Application", StringComparison.OrdinalIgnoreCase)) layerName = "Application";
                else if (file.Contains("Controllers", StringComparison.OrdinalIgnoreCase) || file.Contains("API", StringComparison.OrdinalIgnoreCase)) layerName = "API / Presentation";

                var lines = File.ReadAllLines(file);
                var stats = layers[layerName];
                
                double fileComplexity = lines.Count(l => l.Contains("if (") || l.Contains("foreach (") || l.Contains("while (") || l.Contains(" && ") || l.Contains(" || ")) * 1.5 + 1;

                layers[layerName] = (stats.files + 1, stats.loc + lines.Length, stats.complexity + fileComplexity);
            }

            foreach (var pair in layers)
            {
                double avgComplexity = pair.Value.files > 0 ? pair.Value.complexity / pair.Value.files : 1;
                int debtPercentage = Math.Min(100, (int)(avgComplexity * 10));
                data.Heatmap[pair.Key] = debtPercentage;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate scorecard and heatmap data.");
        }

        return data;
    }

    public async Task<bool> ApplyTargetedFixAsync(string projectRoot, string relativePath, string errorDetails)
    {
        try
        {
            var absolutePath = Path.Combine(projectRoot, relativePath);
            if (!File.Exists(absolutePath))
            {
                _logger.LogWarning("Targeted fix file not found: {Path}", absolutePath);
                return false;
            }

            var fileContent = await File.ReadAllTextAsync(absolutePath);

            var prompt = $@"You are an expert C# compiler repair assistant.
We have a compile or syntax error in this file: `{relativePath}`.
Error details: {errorDetails}

Here is the current content of the file:
```csharp
{fileContent}
```

Please output the fully corrected C# code for this file.
Provide ONLY the raw C# code. Do not include any explanation or markdown formatting wrappers.";

            _logger.LogInformation("Requesting targeted C# code repair for {File}...", relativePath);
            var correctedCode = await _llm.ChatAsync("You are an expert C# compiler repair assistant. Solve the compilation or syntax errors.", prompt);
            correctedCode = CleanCodeBlock(correctedCode);

            if (!string.IsNullOrWhiteSpace(correctedCode) && (correctedCode.Contains("namespace") || correctedCode.Contains("class") || correctedCode.Contains("using ")))
            {
                await File.WriteAllTextAsync(absolutePath, correctedCode);
                _logger.LogInformation("Successfully applied targeted fix to {File}!", relativePath);
                return true;
            }

            _logger.LogWarning("Targeted repair returned empty or invalid content for {File}.", relativePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying targeted C# fix.");
        }
        return false;
    }

    private class BuildError
    {
        public string FilePath { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
