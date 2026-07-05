using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DevCompanion.Agent.Configuration;
using DevCompanion.Agent.Services.Watcher;
using DevCompanion.Agent.Services.Notification;

namespace DevCompanion.Agent.Services.Analyzer;

public class CodeReviewer
{
    private readonly ILlmService _llmService;
    private readonly ReportGenerator _reportGenerator;
    private readonly WhatsAppService _whatsAppService;
    private readonly TelegramService _telegramService;
    private readonly GitDiffTracker _gitDiffTracker;
    private readonly IAdvancedFeaturesService _advancedFeaturesService;
    private readonly AgentSettings _settings;
    private readonly ILogger<CodeReviewer> _logger;

    // Matches the C# stack trace file location format: "in D:\path\file.cs:line 42"
    private static readonly Regex StackTraceFileRegex = new(@"in\s+(?<file>[a-zA-Z]:\\[^\s\?]*\.cs):line\s+(?<line>\d+)", RegexOptions.Compiled);

    public CodeReviewer(
        ILlmService llmService,
        ReportGenerator reportGenerator,
        WhatsAppService whatsAppService,
        TelegramService telegramService,
        GitDiffTracker gitDiffTracker,
        IAdvancedFeaturesService advancedFeaturesService,
        IOptions<AgentSettings> settings,
        ILogger<CodeReviewer> logger)
    {
        _llmService = llmService;
        _reportGenerator = reportGenerator;
        _whatsAppService = whatsAppService;
        _telegramService = telegramService;
        _gitDiffTracker = gitDiffTracker;
        _advancedFeaturesService = advancedFeaturesService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task AnalyzeExceptionAsync(LogEventPayload payload)
    {
        if (!_settings.ActiveFeatureIds.Contains(5))
        {
            _logger.LogInformation("AI Root Cause Exception Engine (Feature 5) is disabled. Skipping analysis.");
            return;
        }

        _logger.LogInformation("Analyzing exception: {ExceptionType} - {ExceptionMessage}", payload.ExceptionType, payload.ExceptionMessage);

        var fileContext = string.Empty;
        var offendingFile = string.Empty;
        var offendingLineNumber = 0;

        // Try to locate the source code from stack trace
        var match = StackTraceFileRegex.Match(payload.StackTrace);
        if (match.Success)
        {
            offendingFile = match.Groups["file"].Value;
            if (int.TryParse(match.Groups["line"].Value, out offendingLineNumber))
            {
                fileContext = ExtractFileContext(offendingFile, offendingLineNumber);
            }
        }

        var systemPrompt = @"You are an Elite .NET Debugging Expert and Principal Engineer.
A bug/exception has been detected in a .NET 10, ASP.NET Core application built with Clean Architecture, CQRS, and MediTR. 

Analyze the provided error details (logs, stack trace, or buggy code snippet) and deliver a safe, zero-side-effect resolution strategy:
1. **Root Cause Analysis (RCA):** Explain exactly *why* the error occurs, identifying the underlying architectural or logical failure.
2. **Impact Boundary Mapping:** List all associated files, MediatR handlers, database contexts, or API endpoints that rely on this failing component. Define the ""blast radius"" of the bug.
3. **The Optimal Clean Code Solution:** Provide the exact, optimized refactored code to fix the issue using modern C# 14 / .NET 10 features. The fix MUST NOT modify the method signatures or public APIs used by other layers unless absolutely necessary.
4. **Regression Prevention Strategy:** Explain how to apply this fix without breaking coupled components. Provide explicit instructions on what integration/unit tests should be written or run (e.g., using FluentAssertions or Respawn) to ensure the system's absolute stability post-fix.";

        var userPrompt = $@"Exception: {payload.ExceptionType}
Message: {payload.ExceptionMessage}
Endpoint: {payload.Endpoint}
Method: {payload.Message}

Stack Trace:
{payload.StackTrace}

{(string.IsNullOrEmpty(fileContext) ? "" : $"Offending Source File: {offendingFile}\nOffending Line: {offendingLineNumber}\nCode Context:\n{fileContext}")}

Generate a C# Clean Architecture fix. Write the code in a standard markdown code block.";

        _logger.LogInformation("Requesting fix from LLM...");
        var response = await _llmService.ChatAsync(systemPrompt, userPrompt);

        // Write report
        await _reportGenerator.GenerateBugReportAsync(
            payload.ProjectRoot,
            payload.Message,
            payload.ExceptionType,
            payload.ExceptionMessage,
            payload.StackTrace,
            response);

        // Send WhatsApp & Telegram alerts
        var alertMessage = $"🚨 *DevCompanion Error Alert!*\n" +
                            $"Project: {Path.GetFileName(payload.ProjectRoot)}\n" +
                            $"Error: {payload.ExceptionType}\n" +
                            $"Message: {payload.ExceptionMessage}\n" +
                            $"Endpoint: {payload.Endpoint}\n\n" +
                            $"📝 Check *AI_BUG_REPORT.md* in your project for the proposed C# Clean Architecture fix.";
        await Task.WhenAll(
            _whatsAppService.SendNotificationAsync(alertMessage),
            _telegramService.SendNotificationAsync(alertMessage)
        );
    }

    public async Task ReviewLocalChangesAsync(string projectRoot, string diff, string? targetFile = null)
    {
        if (string.IsNullOrWhiteSpace(diff)) return;

        _logger.LogInformation("Reviewing local git changes in {ProjectRoot}...", projectRoot);

        // 1. Perform instant file-level code review with instant notifications
        try
        {
            var fileDiffs = ParseDiff(diff);
            foreach (var file in fileDiffs)
            {
                if (targetFile == null)
                {
                    break;
                }

                var relativeTarget = Path.GetRelativePath(projectRoot, targetFile).Replace('\\', '/');
                var relativeFile = file.FileName.Replace('\\', '/');
                
                var isMatch = relativeFile.Equals(relativeTarget, StringComparison.OrdinalIgnoreCase) ||
                              relativeFile.EndsWith(relativeTarget, StringComparison.OrdinalIgnoreCase) ||
                              relativeTarget.EndsWith(relativeFile, StringComparison.OrdinalIgnoreCase) ||
                              Path.GetFileName(file.FileName).Equals(Path.GetFileName(targetFile), StringComparison.OrdinalIgnoreCase);

                if (!isMatch) continue;

                if (file.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var groqSystemPrompt = @"You are a senior real-time C# syntax and logic error detector.
Analyze the C# git diff for syntax errors, typos, missing elements (like missing semicolons), and logic issues.
Explain briefly what changed, what the error is (if any), and provide a clean refactored C# solution.

Format your output EXACTLY as follows (do not use other formats):
HAS_ERROR: [Yes/No]
WHAT_HAPPENED: [Brief description of what changed and what the error is, keep it under 2 lines]
PROPOSED_FIX:
[csharp code block showing the corrected code, or leave empty if no error]";

                            var groqUserPrompt = $@"Analyze this Git Diff block for file: {file.FileName} (Lines: {file.LineNumbers}):

```diff
{file.DiffContent}
```";
                            var groqResult = await _llmService.ChatWithCodeReviewLlmAsync(groqSystemPrompt, groqUserPrompt);
                            
                            var hasError = false;
                            var whatHappened = string.Empty;
                            var proposedFix = string.Empty;

                            var lines = groqResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in lines)
                            {
                                if (line.StartsWith("HAS_ERROR:", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasError = line.Substring(10).Trim().StartsWith("Yes", StringComparison.OrdinalIgnoreCase);
                                }
                                else if (line.StartsWith("WHAT_HAPPENED:", StringComparison.OrdinalIgnoreCase))
                                {
                                    whatHappened = line.Substring(14).Trim();
                                }
                            }

                            var fixIndex = groqResult.IndexOf("PROPOSED_FIX:", StringComparison.OrdinalIgnoreCase);
                            if (fixIndex >= 0)
                            {
                                proposedFix = groqResult.Substring(fixIndex + 13).Trim();
                            }

                            var fullPath = Path.Combine(projectRoot, file.FileName);
                            var driftWarnings = new List<string>();
                            var ruleWarnings = string.Empty;

                            if (File.Exists(fullPath))
                            {
                                try
                                {
                                    var fileContent = await File.ReadAllTextAsync(fullPath);
                                    if (_settings.ActiveFeatureIds.Contains(4))
                                    {
                                        driftWarnings = _advancedFeaturesService.CheckArchitectureDrift(fullPath, fileContent);
                                    }
                                    if (_settings.ActiveFeatureIds.Contains(7))
                                    {
                                        ruleWarnings = await _advancedFeaturesService.CheckAiMemoryRulesAsync(projectRoot, fullPath, fileContent);
                                    }
                                }
                                catch {}
                            }

                            if (driftWarnings.Any() || !string.IsNullOrEmpty(ruleWarnings))
                            {
                                hasError = true;
                                var driftText = driftWarnings.Any() ? string.Join("\n", driftWarnings) : "";
                                var ruleText = !string.IsNullOrEmpty(ruleWarnings) ? $"\n🚨 AI Memory Rules Violation:\n{ruleWarnings}" : "";
                                whatHappened = $"{whatHappened}\n\n⚠️ Architecture Drift:\n{driftText}{ruleText}".Trim('\n');
                            }

                            if (string.IsNullOrWhiteSpace(whatHappened))
                            {
                                whatHappened = "Code changes saved.";
                            }

                            // 1. Append modification report to AI_MODIFICATIONS.md
                            await _reportGenerator.AppendModificationReportAsync(
                                projectRoot,
                                file.FileName,
                                file.LineNumbers,
                                file.DiffContent,
                                whatHappened);

                            if (hasError)
                            {
                                // 2. Append error report to AI_CODE_ERRORS.md
                                await _reportGenerator.AppendCodeErrorReportAsync(
                                    projectRoot,
                                    file.FileName,
                                    file.LineNumbers,
                                    whatHappened,
                                    proposedFix);

                                // 3. Send instant short error alert
                                var errorAlert = $"🚨 *DevCompanion Code Error Alert!*\n\n" +
                                                 $"📂 *File:* `{file.FileName}`\n" +
                                                 $"🔢 *Lines:* `{file.LineNumbers}`\n" +
                                                 $"❌ *Error:* {whatHappened}\n\n" +
                                                 $"📝 Check *AI_CODE_ERRORS.md* in your project root for the proposed fix.";

                                await Task.WhenAll(
                                    _whatsAppService.SendNotificationAsync(errorAlert),
                                    _telegramService.SendNotificationAsync(errorAlert)
                                );
                            }
                            else
                            {
                                // 4. Send instant short modification alert
                                var modAlert = $"⚡ *DevCompanion Code Modified!*\n\n" +
                                               $"📂 *File:* `{file.FileName}`\n" +
                                               $"🔢 *Lines:* `{file.LineNumbers}`\n" +
                                               $"ℹ️ *Changes:* {whatHappened}\n\n" +
                                               $"📝 Check *AI_MODIFICATIONS.md* in your project root for details.";

                                await Task.WhenAll(
                                    _whatsAppService.SendNotificationAsync(modAlert),
                                    _telegramService.SendNotificationAsync(modAlert)
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to run Groq live code review for file: {File}", file.FileName);
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse diff or start Groq live code review.");
        }

        // 2. Perform global code review for AI_AUTOLOG.md using OpenRouter
        var systemPrompt = @"You are a Senior .NET Code Reviewer and Git Automation Expert. 

Your task is to analyze the latest modifications made to the codebase. The project is built using .NET 10, ASP.NET Core, and follows Clean Architecture principles.

Analyze the provided diff/changes for each file and provide a structured impact report:
1. **Change Summary:** Briefly explain *what* changed and *why* (the intent behind the modification) for each file.
2. **Side-Effect & Architectural Risk Assessment:** Analyze how these changes affect other parts of the system. Does a change in an Application Service leak into the Presentation layer? Does a modification in a Domain Entity break any existing invariants or aggregates?
3. **Clean Code & Modern .NET 10 Compliance:** Evaluate if the new code utilizes modern .NET 10 features efficiently and strictly adheres to SOLID principles and clean code practices.
4. **Git Commit Suggestion:** Provide a precise, professional conventional commit message (e.g., feat(orders): ..., fix(auth): ...) summarizing these changes.

At the end of your analysis, always provide a semantic Git commit message suggestion wrapped in [COMMIT_MSG] and [/COMMIT_MSG] tags (e.g. [COMMIT_MSG]feat(auth): add database validation[/COMMIT_MSG]).";

        var userPrompt = $@"Analyze this Git Diff for security, performance, architecture, and Clean Code consistency:

```diff
{diff}
```

Format your output in professional Markdown.";

        _logger.LogInformation("Querying LLM for code review and semantic commit message...");
        var reviewOutput = await _llmService.ChatAsync(systemPrompt, userPrompt);

        // Write code review report to project root
        await _reportGenerator.GenerateCodeReviewReportAsync(projectRoot, reviewOutput);

        // Scan the LLM response for indicators of severe security vulnerabilities
        var containsCriticalVulnerability = reviewOutput.Contains("SQL Injection", StringComparison.OrdinalIgnoreCase) || 
                                           reviewOutput.Contains("Vulnerability", StringComparison.OrdinalIgnoreCase) || 
                                           reviewOutput.Contains("Security Risk", StringComparison.OrdinalIgnoreCase);

        if (containsCriticalVulnerability)
        {
            var alertMessage = $"⚠️ *DevCompanion Security Alert!*\n" +
                                $"Project: {Path.GetFileName(projectRoot)}\n" +
                                $"Potential security issues or vulnerabilities were found in your unstaged changes.\n\n" +
                                $"Please inspect *AI_AUTOLOG.md* in your project directory immediately.";
            await Task.WhenAll(
                _whatsAppService.SendNotificationAsync(alertMessage),
                _telegramService.SendNotificationAsync(alertMessage)
            );
        }

        // Generate unit tests for newly created classes asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                await GenerateUnitTestsForNewClassesAsync(projectRoot, diff);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run unit test generation for project: {Path}", projectRoot);
            }
        });
    }

    private List<FileDiffInfo> ParseDiff(string diff)
    {
        var result = new List<FileDiffInfo>();
        if (string.IsNullOrWhiteSpace(diff)) return result;

        var lines = diff.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        FileDiffInfo? currentFile = null;
        var currentFileLines = new List<string>();
        var addedLineNumbers = new List<int>();
        int currentNewLineNum = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git "))
            {
                if (currentFile != null && currentFileLines.Count > 0)
                {
                    currentFile.DiffContent = string.Join("\n", currentFileLines);
                    currentFile.LineNumbers = addedLineNumbers.Count > 0 ? string.Join(", ", addedLineNumbers.Distinct()) : "Unknown";
                    result.Add(currentFile);
                }

                currentFile = new FileDiffInfo();
                currentFileLines = new List<string>();
                addedLineNumbers = new List<int>();

                var match = System.Text.RegularExpressions.Regex.Match(line, @" b/(?<file>\S+)");
                if (match.Success)
                {
                    currentFile.FileName = match.Groups["file"].Value;
                }
                else
                {
                    currentFile.FileName = "Unknown File";
                }
                continue;
            }

            if (currentFile == null) continue;

            currentFileLines.Add(line);

            if (line.StartsWith("@@ "))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"\+(?<start>\d+)(,(?<count>\d+))?");
                if (match.Success)
                {
                    currentNewLineNum = int.Parse(match.Groups["start"].Value);
                }
                continue;
            }

            if (line.StartsWith("+") && !line.StartsWith("+++ "))
            {
                if (currentNewLineNum > 0)
                {
                    addedLineNumbers.Add(currentNewLineNum);
                    currentNewLineNum++;
                }
            }
            else if (!line.StartsWith("-") && !line.StartsWith("--- "))
            {
                if (currentNewLineNum > 0)
                {
                    currentNewLineNum++;
                }
            }
        }

        if (currentFile != null && currentFileLines.Count > 0)
        {
            currentFile.DiffContent = string.Join("\n", currentFileLines);
            currentFile.LineNumbers = addedLineNumbers.Count > 0 ? string.Join(", ", addedLineNumbers.Distinct()) : "Unknown";
            result.Add(currentFile);
        }

        return result;
    }

    private class FileDiffInfo
    {
        public string FileName { get; set; } = string.Empty;
        public string LineNumbers { get; set; } = string.Empty;
        public string DiffContent { get; set; } = string.Empty;
    }

    private string ExtractFileContext(string filePath, int lineNum)
    {
        try
        {
            if (!File.Exists(filePath)) return string.Empty;

            var lines = File.ReadAllLines(filePath);
            var startLine = Math.Max(1, lineNum - 5);
            var endLine = Math.Min(lines.Length, lineNum + 5);

            var contextBuilder = new System.Text.StringBuilder();
            contextBuilder.AppendLine($"// File: {filePath}");
            for (int i = startLine; i <= endLine; i++)
            {
                var prefix = i == lineNum ? "=> " : "   ";
                contextBuilder.AppendLine($"{prefix}{i:D3}: {lines[i - 1]}");
            }
            return contextBuilder.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file context for {Path} at line {Line}", filePath, lineNum);
            return string.Empty;
        }
    }

    private async Task GenerateUnitTestsForNewClassesAsync(string projectRoot, string diff)
    {
        var modifiedFiles = await _gitDiffTracker.GetModifiedFilesAsync(projectRoot);
        if (modifiedFiles == null || modifiedFiles.Count == 0) return;

        foreach (var file in modifiedFiles)
        {
            if (!File.Exists(file)) continue;

            var fileName = Path.GetFileName(file);
            var fileWithoutExtension = Path.GetFileNameWithoutExtension(file);

            // Skip test files, program files, or configs
            if (fileName.Contains("Test", StringComparison.OrdinalIgnoreCase) || 
                fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check if corresponding test file exists in workspace
            var hasTestFile = false;
            var testSearchDir = Directory.GetParent(projectRoot)?.FullName ?? projectRoot;
            if (Directory.Exists(testSearchDir))
            {
                var testFiles = Directory.GetFiles(testSearchDir, $"*{fileWithoutExtension}*.cs", SearchOption.AllDirectories);
                foreach (var tf in testFiles)
                {
                    var tfName = Path.GetFileName(tf);
                    if (tfName.Equals($"{fileWithoutExtension}Tests.cs", StringComparison.OrdinalIgnoreCase) ||
                        tfName.Equals($"{fileWithoutExtension}Test.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        hasTestFile = true;
                        break;
                    }
                }
            }

            if (!hasTestFile)
            {
                _logger.LogInformation("No test file found for C# class {Class}. Generating baseline xUnit tests...", fileWithoutExtension);
                
                var classCode = await File.ReadAllTextAsync(file);

                var systemPrompt = @"You are a C# unit testing expert. 
Generate a complete, high-quality xUnit and Moq unit test class for the provided C# code. 
Follow clean code patterns, setup mocks for dependencies, and write standard test cases with Arrange-Act-Assert structure. 
Return only the complete C# code inside a markdown code block.";

                var userPrompt = $"Here is the C# class code:\n\n```csharp\n{classCode}\n```\n\nGenerate the corresponding [ClassName]Tests class.";
                var generatedTestCode = await _llmService.ChatAsync(systemPrompt, userPrompt);

                // Clean markdown code block
                if (generatedTestCode.Contains("```csharp"))
                {
                    generatedTestCode = ExtractCodeBlock(generatedTestCode, "csharp");
                }
                else if (generatedTestCode.Contains("```"))
                {
                    generatedTestCode = ExtractCodeBlock(generatedTestCode, "");
                }

                if (string.IsNullOrWhiteSpace(generatedTestCode)) continue;

                // Find test project directory or create one
                var testProjectDir = FindTestProjectDirectory(projectRoot);
                if (string.IsNullOrEmpty(testProjectDir))
                {
                    testProjectDir = Path.Combine(projectRoot, "Tests");
                    Directory.CreateDirectory(testProjectDir);
                }

                var testFilePath = Path.Combine(testProjectDir, $"{fileWithoutExtension}Tests.cs");
                await File.WriteAllTextAsync(testFilePath, generatedTestCode);
                _logger.LogInformation("Saved generated unit tests to {Path}", testFilePath);

                // Send alerts
                var alertMsg = $"🧪 *DevCompanion: Unit Tests Generated!*\n" +
                               $"Class: {fileWithoutExtension}\n" +
                               $"Workspace: {Path.GetFileName(projectRoot)}\n\n" +
                               $"💾 Saved to: {Path.GetFileName(testProjectDir)}/{fileWithoutExtension}Tests.cs";
                await Task.WhenAll(
                    _whatsAppService.SendNotificationAsync(alertMsg),
                    _telegramService.SendNotificationAsync(alertMsg)
                );
            }
        }
    }

    private string FindTestProjectDirectory(string projectRoot)
    {
        try
        {
            var parentDir = Directory.GetParent(projectRoot)?.FullName;
            if (!string.IsNullOrEmpty(parentDir))
            {
                var siblingDirs = Directory.GetDirectories(parentDir);
                foreach (var dir in siblingDirs)
                {
                    if (dir.Contains("Test", StringComparison.OrdinalIgnoreCase) && 
                        Directory.GetFiles(dir, "*.csproj").Any())
                    {
                        return dir;
                    }
                }
            }

            var internalTests = Path.Combine(projectRoot, "Tests");
            if (Directory.Exists(internalTests)) return internalTests;
        }
        catch { /* Fallback */ }
        return string.Empty;
    }

    private string ExtractCodeBlock(string input, string lang)
    {
        var tag = $"```{lang}";
        var startIndex = input.IndexOf(tag);
        if (startIndex == -1)
        {
            startIndex = input.IndexOf("```");
            if (startIndex == -1) return input;
            var end = input.LastIndexOf("```");
            if (end > startIndex)
            {
                return input.Substring(startIndex + 3, end - startIndex - 3).Trim();
            }
            return input;
        }
        
        var startOfCode = startIndex + tag.Length;
        var endIndex = input.IndexOf("```", startOfCode);
        if (endIndex != -1)
        {
            return input.Substring(startOfCode, endIndex - startOfCode).Trim();
        }
        return input.Substring(startOfCode).Trim();
    }

    public async Task PerformInitialAuditAsync(string projectRoot)
    {
        try
        {
            _logger.LogInformation("Performing initial project structure audit for {Root}...", projectRoot);

            if (!Directory.Exists(projectRoot)) return;

            // List C# files relative to project root, ignoring bin/obj/git directories
            var files = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                            !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                            !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
                .Select(f => Path.GetRelativePath(projectRoot, f))
                .ToList();

            var projectStructure = files.Count > 0 
                ? string.Join("\n", files) 
                : "No C# source files found in directory.";

            var systemPrompt = @"You are a Principal Software Architect and Enterprise .NET Solution Architect specializing in .NET 10, ASP.NET Core, Clean Architecture, Domain-Driven Design (DDD), CQRS, MediatR, EF Core, SOLID, and Clean Code.

# OBJECTIVE

Perform a comprehensive architectural audit of the provided project. Base every conclusion strictly on the supplied evidence. Never assume or invent missing information. If evidence is insufficient, explicitly state: ""Insufficient evidence.""

# ANALYSIS SCOPE

Analyze the project and evaluate:
1. Overall architecture and project structure.
2. Clean Architecture layer separation and dependency direction.
3. DDD implementation (Entities, Value Objects, Aggregates, Domain Services, Domain Events, Repositories).
4. CQRS implementation (Commands, Queries, Handlers, Validators, Pipeline Behaviors).
5. SOLID principles and Clean Code practices.
6. Project modularity, maintainability, scalability, and extensibility.
7. Dependency violations, circular dependencies, and layer leakage.
8. Missing enterprise components (Logging, Validation, Caching, Auditing, Health Checks, Background Jobs, Rate Limiting, Outbox, etc.).
9. Architectural risks and technical debt.

# ARCHITECTURE RULES

Validate that:
- Domain depends on nothing.
- Application depends only on Domain.
- Infrastructure depends on Application and Domain.
- Presentation depends on Application only.
- Controllers contain no business logic.
- DbContext is never accessed directly from the Presentation layer.
- Business rules are isolated from Infrastructure and Presentation.

# OUTPUT REQUIREMENTS

Provide a structured report containing:
1. Executive Summary
2. Detected Architecture Style
3. Layer Mapping
4. Architecture Strengths
5. Architecture Violations
6. Dependency Analysis
7. DDD Assessment
8. CQRS Assessment
9. SOLID Assessment
10. Maintainability Assessment
11. Technical Debt
12. Risk Assessment (Critical / High / Medium / Low)
13. Prioritized Recommendations
14. Final Architecture Score (0–100)

# RULES

- Never guess.
- Never fabricate findings.
- Every issue must include supporting evidence (File, Class, Method, or Namespace when available).
- Prioritize findings by severity.
- Recommend only practical, production-ready improvements that preserve architectural integrity.";

            var userPrompt = $@"Project Directory Name: {Path.GetFileName(projectRoot)}

Project C# File Structure:
{projectStructure}

Generate a Clean Architecture folder structure review and audit report. Keep it professional and well-formatted in Markdown.";

            _logger.LogInformation("Querying LLM for initial project audit...");
            var auditOutput = await _llmService.ChatAsync(systemPrompt, userPrompt);

            // Write report file
            await _reportGenerator.GenerateProjectAuditReportAsync(projectRoot, auditOutput);

            // Send WhatsApp & Telegram alerts
            var alertMessage = $"🚀 *DevCompanion Project Audit Completed!*\n" +
                               $"Project: {Path.GetFileName(projectRoot)}\n" +
                               $"Initial structure review and C# file layout check is completed successfully.\n\n" +
                               $"📂 See *AI_PROJECT_AUDIT.md* inside the project root for structural analysis and recommendations.";
            await Task.WhenAll(
                _whatsAppService.SendNotificationAsync(alertMessage),
                _telegramService.SendNotificationAsync(alertMessage)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing initial project structure audit.");
        }
    }
}
