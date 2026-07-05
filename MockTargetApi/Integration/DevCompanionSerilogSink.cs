using System.Text;
using System.Text.Json;
using Serilog.Core;
using Serilog.Events;

namespace MockTargetApi.Integration;

public class DevCompanionSerilogSink : ILogEventSink
{
    private readonly string _agentUrl;
    private readonly string _projectRoot;
    private static readonly HttpClient HttpClient = new();

    public DevCompanionSerilogSink(string agentUrl, string projectRoot)
    {
        _agentUrl = agentUrl.TrimEnd('/') + "/";
        _projectRoot = projectRoot;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level >= LogEventLevel.Warning && logEvent.Exception != null)
        {
            SendLogToAgent(logEvent);
        }
    }

    private void SendLogToAgent(LogEvent logEvent)
    {
        try
        {
            var exception = logEvent.Exception!;
            var payload = new
            {
                Message = logEvent.RenderMessage(),
                Level = logEvent.Level.ToString(),
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                ExceptionMessage = exception.Message,
                StackTrace = exception.StackTrace ?? string.Empty,
                SourceContext = logEvent.Properties.TryGetValue("SourceContext", out var contextVal) 
                    ? contextVal.ToString().Trim('"') 
                    : "Unknown",
                ProjectRoot = _projectRoot,
                Endpoint = logEvent.Properties.TryGetValue("RequestPath", out var pathVal)
                    ? pathVal.ToString().Trim('"')
                    : string.Empty
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _ = Task.Run(async () =>
            {
                try
                {
                    await HttpClient.PostAsync(_agentUrl, content);
                }
                catch
                {
                    // Fail silently to prevent throwing exceptions in the logging pipeline
                }
            });
        }
        catch
        {
            // Fail silently
        }
    }
}

public static class DevCompanionSerilogExtensions
{
    public static Serilog.LoggerConfiguration DevCompanionAgent(
        this Serilog.Configuration.LoggerSinkConfiguration sinkConfiguration,
        string projectRoot,
        string agentUrl = "http://localhost:5005")
    {
        return sinkConfiguration.Sink(new DevCompanionSerilogSink(agentUrl, projectRoot));
    }
}
