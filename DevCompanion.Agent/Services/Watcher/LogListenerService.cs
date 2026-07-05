using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DevCompanion.Agent.Configuration;
using DevCompanion.Agent.Integration;
using DevCompanion.Agent.Services.Analyzer;

namespace DevCompanion.Agent.Services.Watcher;

public class LogListenerService : BackgroundService
{

    private readonly AgentSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LogListenerService> _logger;
    private readonly WebSocketLogQueue _webSocketQueue;
    private HttpListener? _httpListener;

    public LogListenerService(
        IOptions<AgentSettings> settings,
        IServiceScopeFactory scopeFactory,
        ILogger<LogListenerService> logger,
        WebSocketLogQueue webSocketQueue)
    {
        _settings = settings.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _webSocketQueue = webSocketQueue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _httpListener = new HttpListener();
        var prefix = $"http://localhost:{_settings.ListenerPort}/";
        _httpListener.Prefixes.Add(prefix);

        try
        {
            _httpListener.Start();
            _logger.LogInformation("Log Listener Service started on {Prefix}", prefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start HttpListener on prefix {Prefix}. Check permissions or port usage.", prefix);
            return;
        }

        // Run the listener loop in a background thread to allow ExecuteAsync to yield immediately
        _ = Task.Run(() => ListenLoop(stoppingToken), stoppingToken);

        // Keep the BackgroundService alive
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }

        try
        {
            _httpListener.Stop();
            _httpListener.Close();
            _logger.LogInformation("Log Listener Service stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while stopping HttpListener.");
        }
    }

    private async Task ListenLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && _httpListener?.IsListening == true)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context), stoppingToken);
            }
            catch (HttpListenerException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error in HTTP listener request loop.");
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath.ToLower() ?? "/";

        // Enable CORS for frontend API requests
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.Close();
            return;
        }

        try
        {
            // Server-Sent Events Logs Stream Request
            if (path == "/api/logs-stream")
            {
                response.ContentType = "text/event-stream";
                response.Headers.Add("Cache-Control", "no-cache");
                response.Headers.Add("Connection", "keep-alive");
                response.StatusCode = (int)HttpStatusCode.OK;

                var writer = new StreamWriter(response.OutputStream, Encoding.UTF8);
                _webSocketQueue.AddClient(response, writer);

                try
                {
                    while (response.OutputStream != null)
                    {
                        // Send keep-alive comment every 15s to prevent timeouts
                        await writer.WriteAsync(":\n\n");
                        await writer.FlushAsync();
                        await Task.Delay(15000);
                    }
                }
                catch
                {
                    // Client disconnected
                }
                finally
                {
                    _webSocketQueue.RemoveClient(writer);
                    try { writer.Dispose(); } catch {}
                }
                return;
            }

            // GET endpoints
            if (request.HttpMethod == "GET")
            {
                if (path == "/" || path == "/dashboard")
                {
                    await ServeDashboardAsync(response);
                }
                else if (path == "/api/status")
                {
                    await HandleGetStatusAsync(response);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                
                response.Close();
                return;
            }

            // POST endpoints
            if (request.HttpMethod == "POST")
            {
                if (path == "/api/features")
                {
                    await HandlePostFeaturesAsync(context);
                }
                else if (path == "/api/fix")
                {
                    await HandlePostFixAsync(response);
                }
                else if (path == "/api/score")
                {
                    await HandlePostScoreAsync(response);
                }
                else if (path == "/api/apply-fix")
                {
                    await HandleApplyFixAsync(context);
                }
                else
                {
                    // Default Exception Log Ingestion
                    await HandleExceptionLogAsync(context);
                }

                response.Close();
                return;
            }

            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling HTTP request");
            try
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                response.Close();
            }
            catch {}
        }
    }

    private async Task ServeDashboardAsync(HttpListenerResponse response)
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("DevCompanion.Agent.dashboard.html");
            if (stream == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                var errBytes = Encoding.UTF8.GetBytes("dashboard.html resource not found.");
                await response.OutputStream.WriteAsync(errBytes);
                return;
            }

            response.ContentType = "text/html; charset=utf-8";
            response.StatusCode = (int)HttpStatusCode.OK;
            await stream.CopyToAsync(response.OutputStream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serve dashboard.html");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
    }

    private async Task HandleGetStatusAsync(HttpListenerResponse response)
    {
        try
        {
            var currentDir = Directory.GetCurrentDirectory();
            
            var auditPath = Path.Combine(currentDir, "AI_PROJECT_AUDIT.md");
            var errorsPath = Path.Combine(currentDir, "AI_CODE_ERRORS.md");
            var modificationsPath = Path.Combine(currentDir, "AI_MODIFICATIONS.md");
            var contextPath = Path.Combine(currentDir, ".ai", "context.json");

            var auditReport = File.Exists(auditPath) ? await File.ReadAllTextAsync(auditPath) : "";
            var codeErrors = File.Exists(errorsPath) ? await File.ReadAllTextAsync(errorsPath) : "";
            var modificationsLog = File.Exists(modificationsPath) ? await File.ReadAllTextAsync(modificationsPath) : "";
            var customContext = File.Exists(contextPath) ? await File.ReadAllTextAsync(contextPath) : "";

            ScorecardData? scorecard = null;
            using (var scope = _scopeFactory.CreateScope())
            {
                var advancedService = scope.ServiceProvider.GetRequiredService<IAdvancedFeaturesService>();
                scorecard = advancedService.GetScorecardAndHeatmap(currentDir);
            }

            var status = new
            {
                projectPath = currentDir,
                activeFeatures = _settings.ActiveFeatureIds,
                auditReport,
                codeErrors,
                modificationsLog,
                customContext,
                scorecard,
                heatmap = scorecard?.Heatmap
            };

            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var bytes = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize status data");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
    }

    private async Task HandlePostFeaturesAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var featureIds = JsonSerializer.Deserialize<List<int>>(body);
            if (featureIds != null)
            {
                _settings.ActiveFeatureIds = new HashSet<int>(featureIds);
                _logger.LogInformation("Updated active features: {Features}", string.Join(",", featureIds));
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            var bytes = Encoding.UTF8.GetBytes("Features updated");
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update features list");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
    }

    private async Task HandlePostFixAsync(HttpListenerResponse response)
    {
        try
        {
            var currentDir = Directory.GetCurrentDirectory();
            
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var advancedService = scope.ServiceProvider.GetRequiredService<IAdvancedFeaturesService>();
                    await advancedService.RunAutonomousRepairAsync(currentDir);
                    
                    _webSocketQueue.BroadcastStatusUpdate();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background autonomous repair failed");
                }
            });

            response.StatusCode = (int)HttpStatusCode.Accepted;
            var bytes = Encoding.UTF8.GetBytes("Autonomous repair started");
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start autonomous repair");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
    }

    private async Task HandlePostScoreAsync(HttpListenerResponse response)
    {
        try
        {
            var currentDir = Directory.GetCurrentDirectory();
            ScorecardData? scorecard = null;
            using (var scope = _scopeFactory.CreateScope())
            {
                var advancedService = scope.ServiceProvider.GetRequiredService<IAdvancedFeaturesService>();
                scorecard = advancedService.GetScorecardAndHeatmap(currentDir);
            }

            var json = JsonSerializer.Serialize(scorecard, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var bytes = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate technical scorecard");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
    }

    private async Task HandleExceptionLogAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        var logPayload = JsonSerializer.Deserialize<LogEventPayload>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (logPayload != null)
        {
            _logger.LogInformation("Received exception log from project: {ProjectRoot}", logPayload.ProjectRoot);
            
            using var scope = _scopeFactory.CreateScope();
            var codeReviewer = scope.ServiceProvider.GetRequiredService<CodeReviewer>();
            
            _ = Task.Run(async () =>
            {
                try
                {
                    await codeReviewer.AnalyzeExceptionAsync(logPayload);
                    _webSocketQueue.BroadcastStatusUpdate();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to analyze exception payload.");
                }
            });
        }

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        var responseBytes = Encoding.UTF8.GetBytes("Log received");
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
    }

    private async Task HandleApplyFixAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var payload = JsonSerializer.Deserialize<ApplyFixPayload>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (payload != null && !string.IsNullOrWhiteSpace(payload.FilePath))
            {
                var currentDir = Directory.GetCurrentDirectory();
                bool success = false;
                
                using (var scope = _scopeFactory.CreateScope())
                {
                    var advancedService = scope.ServiceProvider.GetRequiredService<IAdvancedFeaturesService>();
                    success = await advancedService.ApplyTargetedFixAsync(currentDir, payload.FilePath, payload.Details);
                }

                if (success)
                {
                    response.StatusCode = (int)HttpStatusCode.OK;
                    var bytes = Encoding.UTF8.GetBytes("Fix successfully applied!");
                    await response.OutputStream.WriteAsync(bytes);
                    _webSocketQueue.BroadcastStatusUpdate();
                    return;
                }
            }

            response.StatusCode = (int)HttpStatusCode.BadRequest;
            var errBytes = Encoding.UTF8.GetBytes("Failed to apply targeted fix.");
            await response.OutputStream.WriteAsync(errBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply targeted fix API request");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
    }
}

public class LogEventPayload
{
    public string Message { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string ExceptionType { get; set; } = string.Empty;
    public string ExceptionMessage { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
    public string SourceContext { get; set; } = string.Empty;
    public string ProjectRoot { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
}

public class ApplyFixPayload
{
    public string FilePath { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}
