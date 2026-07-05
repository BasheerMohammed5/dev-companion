using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DevCompanion.Agent.Configuration;
using DevCompanion.Agent.Services.Notification;

namespace DevCompanion.Agent.Services.Analyzer;

public class LlmService : ILlmService
{
    private readonly HttpClient _httpClient;
    private readonly AgentSettings _settings;
    private readonly TelegramService _telegramService;
    private readonly WhatsAppService _whatsAppService;
    private readonly ILogger<LlmService> _logger;

    private static DateTime _lastAlertTime = DateTime.MinValue;

    public LlmService(
        HttpClient httpClient, 
        IOptions<AgentSettings> settings, 
        TelegramService telegramService,
        WhatsAppService whatsAppService,
        ILogger<LlmService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _telegramService = telegramService;
        _whatsAppService = whatsAppService;
        _logger = logger;
    }

    private static readonly string[] FallbackModels = new[]
    {
        "openrouter/free",
        "meta-llama/llama-3.2-3b-instruct:free"
    };

    public async Task<string> ChatAsync(string systemPrompt, string userPrompt)
    {
        var primaryProvider = _settings.Llm.Provider ?? "Gemini";
        var providersToTry = new List<string> { "Gemini", "Groq", "OpenRouter", "Bynara" };

        // Prioritize the user's selected primary provider
        providersToTry.Remove(primaryProvider);
        providersToTry.Insert(0, primaryProvider);

        var errors = new List<Exception>();

        foreach (var prov in providersToTry)
        {
            try
            {
                if (prov.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    var apiKey = !string.IsNullOrWhiteSpace(_settings.Llm.Gemini.ApiKey) ? _settings.Llm.Gemini.ApiKey : "";
                    var primaryModel = !string.IsNullOrWhiteSpace(_settings.Llm.Gemini.Model) ? _settings.Llm.Gemini.Model : "gemini-2.5-flash";
                    
                    var models = new List<string> { primaryModel };
                    if (primaryModel != "gemini-flash-latest") models.Add("gemini-flash-latest");

                    foreach (var model in models)
                    {
                        try
                        {
                            return await CallGeminiAsync(model, apiKey, systemPrompt, userPrompt);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Gemini model {Model} failed: {Msg}", model, ex.Message);
                            errors.Add(ex);
                        }
                    }
                }
                else if (prov.Equals("Groq", StringComparison.OrdinalIgnoreCase))
                {
                    return await ChatWithGroqAsync(systemPrompt, userPrompt);
                }
                else if (prov.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
                {
                    return await ChatWithOpenRouterAsync(systemPrompt, userPrompt);
                }
                else if (prov.Equals("Bynara", StringComparison.OrdinalIgnoreCase))
                {
                    var targetBaseUrl = !string.IsNullOrWhiteSpace(_settings.Llm.Bynara.BaseUrl) ? _settings.Llm.Bynara.BaseUrl : "https://router.bynara.id/v1";
                    var apiKey = !string.IsNullOrWhiteSpace(_settings.Llm.Bynara.ApiKey) ? _settings.Llm.Bynara.ApiKey : _settings.Llm.ApiKey;
                    var primaryModel = !string.IsNullOrWhiteSpace(_settings.Llm.Bynara.Model) ? _settings.Llm.Bynara.Model : "mistral-large";

                    return await CallOpenAiCompatibleAsync(targetBaseUrl, apiKey, primaryModel, systemPrompt, userPrompt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("LLM Provider {Provider} failed: {Msg}", prov, ex.Message);
                errors.Add(ex);
            }
        }

        var exceptionMessage = $"All configured LLM providers failed to respond. (Primary provider was: {primaryProvider})";
        var finalException = new AggregateException(exceptionMessage, errors);

        // Notify user via Telegram/WhatsApp on first global failure
        if (DateTime.UtcNow - _lastAlertTime > TimeSpan.FromMinutes(5))
        {
            _lastAlertTime = DateTime.UtcNow;
            var alertMsg = $"⚠️ *DevCompanion AI Connection Alert!*\n" +
                           $"Failed to communicate with any AI models (Gemini, Groq, OpenRouter, Bynara).\n\n" +
                           $"Last Error: {finalException.Message}\n\n" +
                           $"Please check your API keys or internet connection.";

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(
                        _telegramService.SendNotificationAsync(alertMsg),
                        _whatsAppService.SendNotificationAsync(alertMsg)
                    );
                }
                catch {}
            });
        }

        return $"Error: {finalException.Message}";
    }

    public async Task<string> ChatWithOpenRouterAsync(string systemPrompt, string userPrompt)
    {
        var targetBaseUrl = !string.IsNullOrWhiteSpace(_settings.Llm.OpenRouter.BaseUrl) ? _settings.Llm.OpenRouter.BaseUrl : "https://openrouter.ai/api/v1";
        var apiKey = !string.IsNullOrWhiteSpace(_settings.Llm.OpenRouter.ApiKey) ? _settings.Llm.OpenRouter.ApiKey : "";
        var primaryModel = !string.IsNullOrWhiteSpace(_settings.Llm.OpenRouter.Model) ? _settings.Llm.OpenRouter.Model : "openrouter/free";

        var models = new List<string> { primaryModel };
        foreach (var m in FallbackModels)
        {
            if (!models.Contains(m)) models.Add(m);
        }

        Exception? lastEx = null;
        foreach (var model in models)
        {
            try
            {
                return await CallOpenAiCompatibleAsync(targetBaseUrl, apiKey, model, systemPrompt, userPrompt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenRouter model {Model} failed. Trying fallback...", model);
                lastEx = ex;
            }
        }
        throw lastEx ?? new Exception("No OpenRouter models were tried.");
    }

    private async Task<string> CallOpenAiCompatibleAsync(string baseUrl, string apiKey, string model, string systemPrompt, string userPrompt)
    {
        var requestUrl = NormalizeUrl(baseUrl);
        var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);

        if (!string.IsNullOrEmpty(apiKey) && apiKey != "YOUR_BYNARA_API_KEY")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (requestUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Add("HTTP-Referer", "https://github.com/basheer/dev-companion");
            request.Headers.Add("X-Title", "DevCompanion");
        }

        var requestBody = new ChatCompletionRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPrompt }
            }
        };

        var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var response = await _httpClient.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        var chatResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var result = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        return result.Trim();
    }

    public async Task<string> ChatWithGroqAsync(string systemPrompt, string userPrompt)
    {
        string targetBaseUrl = !string.IsNullOrWhiteSpace(_settings.Llm.Groq.BaseUrl) ? _settings.Llm.Groq.BaseUrl : "https://api.groq.com/openai/v1";
        string apiKey = !string.IsNullOrWhiteSpace(_settings.Llm.Groq.ApiKey) ? _settings.Llm.Groq.ApiKey : "";
        string primaryModel = !string.IsNullOrWhiteSpace(_settings.Llm.Groq.Model) ? _settings.Llm.Groq.Model : "llama-3.3-70b-versatile";

        var modelsToTry = new List<string> { primaryModel };
        if (!modelsToTry.Contains("llama-3.1-8b-instant"))
        {
            modelsToTry.Add("llama-3.1-8b-instant");
        }

        Exception? lastException = null;

        foreach (var model in modelsToTry)
        {
            try
            {
                var requestUrl = NormalizeUrl(targetBaseUrl);
                var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);

                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var requestBody = new ChatCompletionRequest
                {
                    Model = model,
                    Messages = new List<ChatMessage>
                    {
                        new() { Role = "system", Content = systemPrompt },
                        new() { Role = "user", Content = userPrompt }
                    }
                };

                var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending request to Groq LLM: with model {Model} at {Url}", model, requestUrl);
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var response = await _httpClient.SendAsync(request, cts.Token);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var chatResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var result = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
                return result.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to call Groq LLM model {Model}. Trying fallback...", model);
                lastException = ex;
            }
        }

        if (lastException != null)
        {
            _logger.LogError(lastException, "All Groq LLM models failed to respond.");
            return $"Error: {lastException.Message}";
        }

        return "Error: No model was tried on Groq.";
    }

    public async Task<string> ChatWithCodeReviewLlmAsync(string systemPrompt, string userPrompt)
    {
        if (!string.IsNullOrWhiteSpace(_settings.Llm.Groq.ApiKey) && 
            _settings.Llm.Groq.ApiKey != "YOUR_GROQ_API_KEY" && 
            !_settings.Llm.Groq.ApiKey.StartsWith("YOUR_"))
        {
            try
            {
                return await ChatWithGroqAsync(systemPrompt, userPrompt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Groq review failed. Falling back to Gemini...");
            }
        }
        
        var geminiKey = !string.IsNullOrWhiteSpace(_settings.Llm.Gemini.ApiKey) ? _settings.Llm.Gemini.ApiKey : "";
        var geminiModel = !string.IsNullOrWhiteSpace(_settings.Llm.Gemini.Model) ? _settings.Llm.Gemini.Model : "gemini-2.5-flash";
        
        return await CallGeminiAsync(geminiModel, geminiKey, systemPrompt, userPrompt);
    }

    private async Task<string> CallGeminiAsync(string model, string apiKey, string systemPrompt, string userPrompt)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        
        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = $"{systemPrompt}\n\nUser Input:\n{userPrompt}" }
                    }
                }
            }
        };

        var jsonContent = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        _logger.LogInformation("Sending request to Gemini: with model {Model}", model);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var response = await _httpClient.PostAsync(url, content, cts.Token);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        
        var text = root.GetProperty("candidates")[0]
                       .GetProperty("content")
                       .GetProperty("parts")[0]
                       .GetProperty("text")
                       .GetString();

        return text?.Trim() ?? string.Empty;
    }

    private string NormalizeUrl(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');
        if (!url.EndsWith("/chat/completions") && !url.EndsWith("/v1"))
        {
            url = $"{url}/v1/chat/completions";
        }
        else if (url.EndsWith("/v1"))
        {
            url = $"{url}/chat/completions";
        }
        return url;
    }

    private class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new();
    }

    private class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private class ChatCompletionResponse
    {
        public List<Choice>? Choices { get; set; }
    }

    private class Choice
    {
        public ChatMessage? Message { get; set; }
    }
}
