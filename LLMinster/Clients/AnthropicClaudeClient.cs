using Claudia;
using LLMinster.Interfaces;

namespace LLMinster.Clients;

public class AnthropicClaudeClient : ILLMClient
{
    private readonly Anthropic _anthropicClient;

    public AnthropicClaudeClient(string apiKey, string modelName)
    {
        _anthropicClient = new Anthropic { ApiKey = apiKey };
        Name = modelName;
    }

    public string Name { get; }

    public async Task<string> GenerateContentAsync(string prompt, GenerationOptions options)
    {
        // Map GenerationOptions to Anthropic's specific request format
        var message = new Message { Role = "user", Content = prompt };

        var messageRequest = new MessageRequest
        {
            Model = Name,
            MaxTokens = options.MaxTokens,
            Messages = [message],
            Temperature = options.Temperature
        };

        var response = await _anthropicClient.Messages.CreateAsync(messageRequest);
        return response.Content?.ToString().Trim() ?? string.Empty;
    }
}