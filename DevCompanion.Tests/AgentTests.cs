using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;
using DevCompanion.Agent.Configuration;
using DevCompanion.Agent.Services.Analyzer;
using DevCompanion.Agent.Services.Watcher;

namespace DevCompanion.Tests;

public class AgentTests
{
    [Fact]
    public void LlmService_NormalizeUrl_ShouldCorrectlyAppendV1ChatCompletions()
    {
        // We use Reflection to test the private NormalizeUrl method of LlmService
        var mockHttpClient = new HttpClient();
        var mockLogger = new Mock<ILogger<LlmService>>();
        
        var settings = new AgentSettings
        {
            Llm = new LlmSettings { BaseUrl = "https://router.bynara.id" }
        };
        var mockOptions = new Mock<IOptions<AgentSettings>>();
        mockOptions.Setup(o => o.Value).Returns(settings);

        var service = new LlmService(mockHttpClient, mockOptions.Object, null!, null!, mockLogger.Object);
        
        var methodInfo = typeof(LlmService).GetMethod("NormalizeUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(methodInfo);

        // Test 1: Base URL without v1
        var result1 = methodInfo.Invoke(service, new object[] { "https://router.bynara.id" }) as string;
        Assert.Equal("https://router.bynara.id/v1/chat/completions", result1);

        // Test 2: Base URL with trailing slash
        var result2 = methodInfo.Invoke(service, new object[] { "https://router.bynara.id/" }) as string;
        Assert.Equal("https://router.bynara.id/v1/chat/completions", result2);

        // Test 3: Base URL already containing v1
        var result3 = methodInfo.Invoke(service, new object[] { "https://router.bynara.id/v1" }) as string;
        Assert.Equal("https://router.bynara.id/v1/chat/completions", result3);

        // Test 4: Base URL already containing completions endpoint
        var result4 = methodInfo.Invoke(service, new object[] { "https://router.bynara.id/v1/chat/completions" }) as string;
        Assert.Equal("https://router.bynara.id/v1/chat/completions", result4);
    }

    [Fact]
    public async Task GitDiffTracker_NonGitDirectory_ShouldReturnEmptyDiff()
    {
        var mockLogger = new Mock<ILogger<GitDiffTracker>>();
        var tracker = new GitDiffTracker(mockLogger.Object);

        // Run in temp folder that does not contain git repository
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var diff = await tracker.GetDiffAsync(tempDir);
            Assert.Empty(diff);

            var files = await tracker.GetModifiedFilesAsync(tempDir);
            Assert.Empty(files);
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public void AdvancedFeaturesService_CheckArchitectureDrift_ShouldDetectViolations()
    {
        var mockLogger = new Mock<ILogger<AdvancedFeaturesService>>();
        var mockLlm = new Mock<ILlmService>();
        var service = new AdvancedFeaturesService(mockLogger.Object, mockLlm.Object);

        // Test 1: Domain file referencing Infrastructure
        var domainFile = @"D:\project\Domain\Models\Order.cs";
        var domainContent = "using SmartMeetingAssistant.Infrastructure;\nnamespace Domain.Models;\nclass Order {}";
        var violations1 = service.CheckArchitectureDrift(domainFile, domainContent);
        Assert.Single(violations1);
        Assert.Contains("Domain boundary violation", violations1.First());

        // Test 2: Application file referencing Infrastructure
        var appFile = @"D:\project\Application\Services\OrderService.cs";
        var appContent = "using SmartMeetingAssistant.Infrastructure;\nnamespace Application.Services;\nclass OrderService {}";
        var violations2 = service.CheckArchitectureDrift(appFile, appContent);
        Assert.Single(violations2);
        Assert.Contains("Application boundary violation", violations2.First());

        // Test 3: Normal compliant code
        var cleanFile = @"D:\project\Domain\Models\Order.cs";
        var cleanContent = "namespace Domain.Models;\nclass Order {}";
        var violations3 = service.CheckArchitectureDrift(cleanFile, cleanContent);
        Assert.Empty(violations3);
    }

    [Fact]
    public void AdvancedFeaturesService_GetScorecardAndHeatmap_ShouldReturnValidModel()
    {
        var mockLogger = new Mock<ILogger<AdvancedFeaturesService>>();
        var mockLlm = new Mock<ILlmService>();
        var service = new AdvancedFeaturesService(mockLogger.Object, mockLlm.Object);

        // Run on temporary directory
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var data = service.GetScorecardAndHeatmap(tempDir);
            Assert.NotNull(data);
            Assert.Equal("70", data.Score);
            Assert.Equal(0, data.ViolationsCount);
            Assert.Equal("0%", data.InterfacesRatio);
            Assert.Equal("Normal", data.ComplexityIndex);
            Assert.NotEmpty(data.Heatmap);
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }
}
