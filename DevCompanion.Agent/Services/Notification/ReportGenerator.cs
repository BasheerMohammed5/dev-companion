using Microsoft.Extensions.Logging;

namespace DevCompanion.Agent.Services.Notification;

public class ReportGenerator
{
    private readonly ILogger<ReportGenerator> _logger;

    public ReportGenerator(ILogger<ReportGenerator> logger)
    {
        _logger = logger;
    }

    public async Task GenerateBugReportAsync(string projectRoot, string message, string exceptionType, string exceptionMessage, string stackTrace, string suggestedFix)
    {
        try
        {
            var filePath = Path.Combine(projectRoot, "AI_BUG_REPORT.md");
            var content = $@"# AI Runtime Error Report

A runtime exception was captured by the Live Error-Catch Agent.

## Error Details
- **Message:** {message}
- **Exception Type:** `{exceptionType}`
- **Exception Message:** `{exceptionMessage}`
- **Captured At:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}

## Stack Trace
```csharp
{stackTrace}
```

## Recommended C# Fix (LLM Suggestion)
{suggestedFix}

---
*Report generated automatically by DevCompanion.Agent.*
";
            await File.WriteAllTextAsync(filePath, content);
            _logger.LogInformation("Written AI_BUG_REPORT.md to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write AI_BUG_REPORT.md to project root.");
        }
    }

    public async Task GenerateVulnerabilityReportAsync(string projectRoot, string endpoint, string payload, string responseStatus, string vulnerabilityType, string suggestedFix)
    {
        try
        {
            var filePath = Path.Combine(projectRoot, "AI_Vulnerability_Report.md");
            var content = $@"# AI Security & API Vulnerability Report

A vulnerability was detected during the automated Endpoint Shadow Testing.

## Vulnerability Details
- **Type:** {vulnerabilityType}
- **Target Endpoint:** `{endpoint}`
- **Payload Sent:** `{payload}`
- **Server Response:** `{responseStatus}`
- **Detected At:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}

## Recommended Clean Architecture C# Fix
{suggestedFix}

---
*Report generated automatically by DevCompanion.Agent.*
";
            await File.WriteAllTextAsync(filePath, content);
            _logger.LogInformation("Written AI_Vulnerability_Report.md to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write AI_Vulnerability_Report.md to project root.");
        }
    }

    public async Task GenerateCodeReviewReportAsync(string projectRoot, string diffReview)
    {
        try
        {
            var filePath = Path.Combine(projectRoot, "AI_AUTOLOG.md");
            var content = $@"# AI Code Review Log

The agent reviewed your local unstaged changes.

## Review Notes
{diffReview}

---
*Report generated automatically by DevCompanion.Agent.*
";
            await File.WriteAllTextAsync(filePath, content);
            _logger.LogInformation("Written AI_AUTOLOG.md to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write AI_AUTOLOG.md to project root.");
        }
    }

    public async Task GenerateProjectAuditReportAsync(string projectRoot, string auditNotes)
    {
        try
        {
            var filePath = Path.Combine(projectRoot, "AI_PROJECT_AUDIT.md");
            var content = $@"# AI Initial Project Audit & Structure Analysis

An audit was performed on startup to verify the project's folder structure, files, and Clean Architecture conventions.

## Audit Review
{auditNotes}

---
*Report generated automatically by DevCompanion.Agent.*
";
            await File.WriteAllTextAsync(filePath, content);
            _logger.LogInformation("Written AI_PROJECT_AUDIT.md to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write AI_PROJECT_AUDIT.md to project root.");
        }
    }

    public async Task AppendModificationReportAsync(string projectRoot, string fileName, string lineNumbers, string diffContent, string description)
    {
        try
        {
            var filePath = Path.Combine(projectRoot, "AI_MODIFICATIONS.md");
            var fileExists = File.Exists(filePath);
            var header = fileExists ? "" : "# AI Project Modifications Log\n\nThis file tracks all your saved modifications in C# source files.\n\n";
            
            var content = $"{header}## Modification in {fileName}\n" +
                          $"- **Captured At:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                          $"- **Lines:** {lineNumbers}\n" +
                          $"- **Description:** {description}\n\n" +
                          $"### Diff\n" +
                          $"```diff\n{diffContent}\n```\n\n" +
                          $"---\n\n";

            await File.AppendAllTextAsync(filePath, content);
            _logger.LogInformation("Appended modification report to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write to AI_MODIFICATIONS.md");
        }
    }

    public async Task AppendCodeErrorReportAsync(string projectRoot, string fileName, string lineNumbers, string description, string proposedFix)
    {
        try
        {
            var filePath = Path.Combine(projectRoot, "AI_CODE_ERRORS.md");
            var fileExists = File.Exists(filePath);
            var header = fileExists ? "" : "# AI Code Errors & Syntax Solutions Log\n\nThis file tracks all detected errors and their proposed C# fixes.\n\n";

            var content = $"{header}## Error in {fileName}\n" +
                          $"- **Captured At:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                          $"- **Lines:** {lineNumbers}\n" +
                          $"- **Details:** {description}\n\n" +
                          $"### Proposed C# Fix\n" +
                          $"{proposedFix}\n\n" +
                          $"---\n\n";

            await File.AppendAllTextAsync(filePath, content);
            _logger.LogInformation("Appended error report to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write to AI_CODE_ERRORS.md");
        }
    }
}
