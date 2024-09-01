namespace LLMinster.Interfaces;

public interface IContextWindowManager
{
    Task<string> ProcessMessageAsync(Guid sessionId, string userMessage, double temperature);
    Task<string> ReconstructWindowAsync(Guid sessionId);
}