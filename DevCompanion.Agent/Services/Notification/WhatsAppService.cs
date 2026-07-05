using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DevCompanion.Agent.Configuration;

namespace DevCompanion.Agent.Services.Notification;

public class WhatsAppService
{
    private readonly HttpClient _httpClient;
    private readonly AgentSettings _settings;
    private readonly ILogger<WhatsAppService> _logger;

    public WhatsAppService(HttpClient httpClient, IOptions<AgentSettings> settings, ILogger<WhatsAppService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendNotificationAsync(string message)
    {
        if (!_settings.WhatsApp.Enabled || 
            string.IsNullOrWhiteSpace(_settings.WhatsApp.ApiKey) || 
            _settings.WhatsApp.ApiKey == "YOUR_WHATSAPP_API_KEY")
        {
            _logger.LogInformation("[SIMULATED WHATSAPP NOTIFICATION] (WhatsApp disabled or ApiKey missing):\n{Message}", message);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.WhatsApp.ApiUrl) || 
            string.IsNullOrWhiteSpace(_settings.WhatsApp.RecipientPhoneNumber))
        {
            _logger.LogWarning("WhatsApp notification is enabled but ApiUrl or RecipientPhoneNumber is empty.");
            return;
        }

        try
        {
            HttpResponseMessage response;
            if (_settings.WhatsApp.ApiUrl.Contains("api.callmebot.com", StringComparison.OrdinalIgnoreCase))
            {
                // CallMeBot GET format
                var callMeBotUrl = $"{_settings.WhatsApp.ApiUrl}?phone={_settings.WhatsApp.RecipientPhoneNumber}&apikey={_settings.WhatsApp.ApiKey}&text={Uri.EscapeDataString(message)}";
                _logger.LogInformation("Sending CallMeBot WhatsApp message to {Recipient}...", _settings.WhatsApp.RecipientPhoneNumber);
                response = await _httpClient.GetAsync(callMeBotUrl);
            }
            else
            {
                var request = new HttpRequestMessage(HttpMethod.Post, _settings.WhatsApp.ApiUrl);
                
                if (!string.IsNullOrEmpty(_settings.WhatsApp.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.WhatsApp.ApiKey);
                }

                object payload;
                if (_settings.WhatsApp.ApiUrl.Contains("graph.facebook.com", StringComparison.OrdinalIgnoreCase))
                {
                    // Standard Meta WhatsApp Cloud API format
                    payload = new
                    {
                        messaging_product = "whatsapp",
                        to = _settings.WhatsApp.RecipientPhoneNumber,
                        type = "text",
                        text = new { body = message }
                    };
                }
                else
                {
                    // Generic POST payload (e.g. for developer gateways)
                    payload = new
                    {
                        to = _settings.WhatsApp.RecipientPhoneNumber,
                        message = message
                    };
                }

                var json = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending WhatsApp message to {Recipient} via {Url}...", _settings.WhatsApp.RecipientPhoneNumber, _settings.WhatsApp.ApiUrl);
                response = await _httpClient.SendAsync(request);
            }

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("WhatsApp message sent successfully.");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send WhatsApp message. Status code: {Status}, Details: {Details}", response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred while sending WhatsApp message.");
        }
    }
}
