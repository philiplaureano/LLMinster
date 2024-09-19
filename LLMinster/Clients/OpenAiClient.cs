using LLMinster.Interfaces;
using OpenAI_API;

namespace LLMinster.Clients;

public class OpenAiClient : ILLMClient
{
    private readonly OpenAIAPI _api;
    private string _apiKey;
    private readonly string _modelName;

    public OpenAiClient(string apiKey, string modelName)
    {
        _apiKey = apiKey;
        _modelName = modelName;
        Name = modelName;

        _api = new OpenAIAPI(apiKey);
    }

    public string Name { get; }

    public async Task<string> GenerateContentAsync(string prompt, GenerationOptions options)
    {
        var chat = _api.Chat.CreateConversation();
        chat.Model = _modelName;
        chat.RequestParameters.Temperature = options.Temperature;
        chat.AppendUserInput(prompt);

        var response = await chat.GetResponseFromChatbotAsync();
        return response;
    }
}