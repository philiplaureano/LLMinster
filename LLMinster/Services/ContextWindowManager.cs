using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Threading.Tasks;
using LLMinster.Extensions;
using LLMinster.Interfaces;
using LLMinster.Models;
using Microsoft.FSharp.Core;
using Mscc.GenerativeAI;
using OneOf;
using OneOf.Types;

namespace LLMinster
{
    public class ContextWindowManager : IContextWindowManager
    {
        private readonly IEventStore _eventStore;

        public ContextWindowManager(IEventStore eventStore)
        {
            _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        }

        public async Task<OneOf<string, None, LLMinster.Interfaces.Error>> ReconstructWindowAsync(Guid sessionId)
        {
            var events = (await _eventStore.GetEventsAsync(sessionId)).ToArray();

            if (events.Length == 0)
                return new None();
            
            return FormatContextWindow(events);
        }

        public async Task<OneOf<string, None, LLMinster.Interfaces.Error>> ProcessMessageAsync(Guid sessionId,
            string userMessage, fsEnsemble.ILanguageModelClient languageModelClient, double temperature)
        {
            // Append user message to event store
            var appendResult = await AppendEventAsync(new SessionEvent
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                ModelName = "User",
                Content = userMessage,
                Timestamp = DateTime.UtcNow,
                SequenceNumber = await GetNextSequenceNumber(sessionId)
            });

            if (appendResult.IsT1)
            {
                return appendResult.AsT1;
            }

            // Reconstruct context window
            var contextWindowResult = await ReconstructWindowAsync(sessionId);
            if (contextWindowResult.IsError())
                return contextWindowResult.AsT2;

            var contextWindow = contextWindowResult.IsSome()? contextWindowResult.AsT0 : string.Empty;

            // Generate AI response
            var contentRequest = new fsEnsemble.ContentRequest(contextWindow, temperature);
            var result = await languageModelClient.GenerateContentAsync(contentRequest);

            if (!result.HasResponse())
                return new LLMinster.Interfaces.Error($"Failed to generate AI response: {result.ErrorValue}");
            
            var aiResponse = result.ResultValue.Response.Value;

            // Append AI response to event store
            appendResult = await AppendEventAsync(new SessionEvent
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                ModelName = languageModelClient.GetType().Name,
                Content = aiResponse,
                Timestamp = DateTime.UtcNow,
                SequenceNumber = await GetNextSequenceNumber(sessionId)
            });

            if (appendResult.IsT1)
            {
                return appendResult.AsT1;
            }

            return aiResponse;
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
        
        private async Task<OneOf<LLMinster.Interfaces.Unit, LLMinster.Interfaces.Error>> AppendEventAsync(SessionEvent @event)
        {
            try
            {
                await _eventStore.AppendEventAsync(@event);
                return new LLMinster.Interfaces.Unit();
            }
            catch (Exception ex)
            {
                return new LLMinster.Interfaces.Error($"Failed to append event: {ex.Message}");
            }
        }
    }
}