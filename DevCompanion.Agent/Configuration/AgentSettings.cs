namespace DevCompanion.Agent.Configuration;

public class AgentSettings
{
    public LlmSettings Llm { get; set; } = new();
    public WhatsAppSettings WhatsApp { get; set; } = new();
    public TelegramSettings Telegram { get; set; } = new();
    public int ListenerPort { get; set; } = 5005;
    public List<ProjectConfig> Projects { get; set; } = new();
    public HashSet<int> ActiveFeatureIds { get; set; } = new() { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
}

public class LlmSettings
{
    public string Provider { get; set; } = "Bynara"; // "Bynara", "OpenRouter", "Ollama", "OpenAI-compatible"
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    public LlmEndpointConfig Bynara { get; set; } = new()
    {
        BaseUrl = "https://router.bynara.id/v1",
        Model = "mistral-large"
    };

    public LlmEndpointConfig OpenRouter { get; set; } = new()
    {
        BaseUrl = "https://openrouter.ai/api/v1",
        Model = "qwen/qwen-2.5-coder-32b-instruct:free"
    };

    public LlmEndpointConfig Groq { get; set; } = new()
    {
        BaseUrl = "https://api.groq.com/openai/v1",
        Model = "llama-3.3-70b-versatile"
    };

    public LlmEndpointConfig Gemini { get; set; } = new()
    {
        BaseUrl = "https://generativelanguage.googleapis.com",
        Model = "gemini-2.5-flash"
    };
}

public class LlmEndpointConfig
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}

public class WhatsAppSettings
{
    public string ApiUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string RecipientPhoneNumber { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
}

public class TelegramSettings
{
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
}
