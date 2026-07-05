namespace DevCompanion.Agent.Services.Analyzer;

public interface ILlmService
{
    Task<string> ChatAsync(string systemPrompt, string userPrompt);
    Task<string> ChatWithGroqAsync(string systemPrompt, string userPrompt);
    Task<string> ChatWithCodeReviewLlmAsync(string systemPrompt, string userPrompt);
}
