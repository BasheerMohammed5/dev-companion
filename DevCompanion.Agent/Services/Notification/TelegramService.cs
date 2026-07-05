using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DevCompanion.Agent.Configuration;

namespace DevCompanion.Agent.Services.Notification;

public class TelegramService
{
    private readonly HttpClient _httpClient;
    private readonly AgentSettings _settings;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(HttpClient httpClient, IOptions<AgentSettings> settings, ILogger<TelegramService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendNotificationAsync(string message)
    {
        if (!_settings.Telegram.Enabled || 
            string.IsNullOrWhiteSpace(_settings.Telegram.BotToken) || 
            string.IsNullOrWhiteSpace(_settings.Telegram.ChatId) ||
            _settings.Telegram.BotToken == "YOUR_TELEGRAM_BOT_TOKEN" ||
            _settings.Telegram.ChatId == "YOUR_TELEGRAM_CHAT_ID")
        {
            _logger.LogInformation("[SIMULATED TELEGRAM NOTIFICATION] (Telegram disabled or credentials missing):\n{Message}", message);
            return;
        }

        try
        {
            var url = $"https://api.telegram.org/bot{_settings.Telegram.BotToken}/sendMessage";
            
            const int maxChunkSize = 4000;
            var messageChunks = new List<string>();

            if (message.Length <= maxChunkSize)
            {
                messageChunks.Add(message);
            }
            else
            {
                var text = message;
                while (text.Length > 0)
                {
                    if (text.Length <= maxChunkSize)
                    {
                        messageChunks.Add(text);
                        break;
                    }

                    var splitIndex = text.LastIndexOf('\n', maxChunkSize);
                    if (splitIndex < 1000)
                    {
                        splitIndex = maxChunkSize;
                    }

                    messageChunks.Add(text.Substring(0, splitIndex));
                    text = text.Substring(splitIndex);
                }
            }

            foreach (var chunk in messageChunks)
            {
                var payload = new
                {
                    chat_id = _settings.Telegram.ChatId,
                    text = chunk,
                    parse_mode = "Markdown"
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending Telegram message chunk via bot to chat {ChatId}...", _settings.Telegram.ChatId);
                var response = await _httpClient.PostAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Telegram message chunk sent successfully.");
                }
                else
                {
                    var details = await response.Content.ReadAsStringAsync();
                    
                    if (details.Contains("bad request", StringComparison.OrdinalIgnoreCase) || 
                        response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        _logger.LogWarning("Markdown formatting failed. Retrying chunk as plain text...");
                        var fallbackPayload = new
                        {
                            chat_id = _settings.Telegram.ChatId,
                            text = chunk
                        };
                        var fallbackJson = JsonSerializer.Serialize(fallbackPayload);
                        var fallbackContent = new StringContent(fallbackJson, Encoding.UTF8, "application/json");
                        var fallbackResponse = await _httpClient.PostAsync(url, fallbackContent);
                        if (fallbackResponse.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("Telegram message chunk sent successfully as plain text.");
                            continue;
                        }
                        details = await fallbackResponse.Content.ReadAsStringAsync();
                    }

                    _logger.LogError("Failed to send Telegram message chunk. Status code: {Code}, Details: {Details}", response.StatusCode, details);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred while sending Telegram message.");
        }
    }
}
