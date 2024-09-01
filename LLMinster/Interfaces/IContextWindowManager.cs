using OneOf;
using OneOf.Types;

namespace LLMinster.Interfaces;

public interface IContextWindowManager
{
    Task<OneOf<string, None, Error>> ProcessMessageAsync(Guid sessionId, string userMessage, fsEnsemble.ILanguageModelClient languageModelClient, double temperature);
    Task<OneOf<string, None, LLMinster.Interfaces.Error>> ReconstructWindowAsync(Guid sessionId);
}

public record Error(string Message);
public sealed record Unit;