namespace LLMinster.Interfaces;

public interface IContextService
{
    Task<string> GetPromptWithContextAsync(Guid sessionId, string userMessage);
}