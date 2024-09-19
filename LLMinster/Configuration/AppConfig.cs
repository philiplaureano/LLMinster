namespace LLMinster;

internal class AppConfig
{
    public string WatchDirectory { get; set; }

    public List<LLMClientConfig> LLMClients { get; set; } = new();

    public bool IsValid(out string validationError)
    {
        if (string.IsNullOrWhiteSpace(WatchDirectory))
        {
            validationError = "WatchDirectory must be specified.";
            return false;
        }

        foreach (var client in LLMClients)
        {
            if (string.IsNullOrWhiteSpace(client.Type) || string.IsNullOrWhiteSpace(client.ApiKey))
            {
                validationError = "Each LLM client must have a Type and ApiKey.";
                return false;
            }

            if (client.Type == "AnthropicClaude" && string.IsNullOrWhiteSpace(client.ModelName))
            {
                validationError = "AnthropicClaude clients must have a ModelName.";
                return false;
            }

            // Add more validation rules as needed
        }

        validationError = string.Empty;
        return true;
    }
}