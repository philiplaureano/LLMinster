using LLMinster.Interfaces;
using Mscc.GenerativeAI;

namespace LLMinster.Clients;

public class GoogleGeminiClient : ILLMClient
{
    private readonly GoogleAI _googleAI;
    private readonly GenerativeModel _model;

    public GoogleGeminiClient(string apiKey, string modelName = Model.Gemini15FlashLatest)
    {
        _googleAI = new GoogleAI(apiKey);
        _model = _googleAI.GenerativeModel(modelName);
    }

    public string Name => "GoogleGemini";

    public async Task<string> GenerateContentAsync(string prompt, GenerationOptions options)
    {
        // Map GenerationOptions to Mscc.GenerativeAI.GenerationConfig
        var config = new GenerationConfig
        {
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxTokens
        };

        var response = await _model.GenerateContent(prompt, config);
        return response?.Text?.Trim() ?? string.Empty;
    }
}