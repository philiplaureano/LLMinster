namespace LLMinster.Models;

public class SessionEvent
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public long SequenceNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public string ModelName { get; set; }
    public string Content { get; set; }
}