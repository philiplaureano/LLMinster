namespace LLMinster.Interfaces;

public interface ILLMClient
{
    string Name { get; }
    Task<string> GenerateContentAsync(string prompt, GenerationOptions options);
}