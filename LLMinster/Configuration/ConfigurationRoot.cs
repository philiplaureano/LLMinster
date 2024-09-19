namespace LLMinster;

public class ConfigurationRoot
{
    public Dictionary<string, ProviderConfig> Providers { get; set; }
    public string DefaultAlias { get; set; }
}