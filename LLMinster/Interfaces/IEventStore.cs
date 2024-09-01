using LLMinster.Models;

namespace LLMinster.Interfaces;

public interface IEventStore
{
    Task AppendEventAsync(SessionEvent @event);
    Task<IEnumerable<SessionEvent>> GetEventsAsync(Guid sessionId, long fromSequence = 0);
}