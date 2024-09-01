using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LLMinster.Extensions;
using LLMinster.Interfaces;
using LLMinster.Models;
using Mscc.GenerativeAI;

namespace LLMinster
{
    public class ContextWindowManager : IContextWindowManager
    {
        private readonly IEventStore _eventStore;

        public ContextWindowManager(IEventStore eventStore)
        {
            _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        }

        public async Task<string> ReconstructWindowAsync(Guid sessionId)
        {
            var events = await _eventStore.GetEventsAsync(sessionId);
            return FormatContextWindow(events);
        }

        public async Task<string> ProcessMessageAsync(Guid sessionId, string userMessage, fsEnsemble.ILanguageModelClient languageModelClient, double temperature)
        {
            // Append user message to event store
            await _eventStore.AppendEventAsync(new SessionEvent
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                ModelName = "User",
                Content = userMessage,
                Timestamp = DateTime.UtcNow,
                SequenceNumber = await GetNextSequenceNumber(sessionId)
            });

            // Reconstruct context window
            var contextWindow = await ReconstructWindowAsync(sessionId);

            // Generate AI response
            var contentRequest = new fsEnsemble.ContentRequest(contextWindow, temperature);
            var result = await languageModelClient.GenerateContentAsync(contentRequest);

            if (result.HasResponse())
            {
                var aiResponse = result.ResultValue.Response.Value;

                // Append AI response to event store
                await _eventStore.AppendEventAsync(new SessionEvent
                {
                    Id = Guid.NewGuid(),
                    SessionId = sessionId,
                    ModelName = languageModelClient.GetType().Name, // Changed from "AI" to use the actual type name
                    Content = aiResponse,
                    Timestamp = DateTime.UtcNow,
                    SequenceNumber = await GetNextSequenceNumber(sessionId)
                });

                return aiResponse;
            }

            throw new Exception($"Failed to generate AI response: {result.ErrorValue}");
        }

        private string FormatContextWindow(IEnumerable<SessionEvent> events)
        {
            var sb = new StringBuilder();
            foreach (var @event in events.OrderBy(e => e.SequenceNumber))
            {
                sb.AppendLine($"{@event.ModelName}: {@event.Content}");
            }
            return sb.ToString().TrimEnd();
        }

        private async Task<long> GetNextSequenceNumber(Guid sessionId)
        {
            var events = await _eventStore.GetEventsAsync(sessionId);
            return events.Any() ? events.Max(e => e.SequenceNumber) + 1 : 1;
        }
    }
}