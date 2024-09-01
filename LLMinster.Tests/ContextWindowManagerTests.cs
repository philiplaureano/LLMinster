using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using FakeItEasy;
using LLMinster.Interfaces;
using LLMinster.Models;
using Microsoft.FSharp.Core;
using Mscc.GenerativeAI;
using OneOf;
using OneOf.Types;

namespace LLMinster.Tests
{
    public class ContextWindowManagerTests
    {
        private readonly IEventStore _fakeEventStore;
        private readonly IContextWindowManager _contextWindowManager;

        public ContextWindowManagerTests()
        {
            _fakeEventStore = A.Fake<IEventStore>();
            _contextWindowManager = new ContextWindowManager(_fakeEventStore);
        }

        [Fact(DisplayName =
            "ReconstructWindowAsync should return a properly formatted context window from event history")]
        public async Task ReconstructWindowAsync_ShouldReturnFormattedContextWindow()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var events = new List<SessionEvent>
            {
                new SessionEvent { SessionId = sessionId, ModelName = "User", Content = "Hello", SequenceNumber = 1 },
                new SessionEvent { SessionId = sessionId, ModelName = "AI", Content = "Hi there!", SequenceNumber = 2 },
                new SessionEvent
                    { SessionId = sessionId, ModelName = "User", Content = "How are you?", SequenceNumber = 3 }
            };

            A.CallTo(() => _fakeEventStore.GetEventsAsync(sessionId, 0))
                .Returns(Task.FromResult((IEnumerable<SessionEvent>)events));

            // Act
            var result = await _contextWindowManager.ReconstructWindowAsync(sessionId);

            // Assert
            var newLine = Environment.NewLine;
            Assert.Equal($"User: Hello{newLine}AI: Hi there!{newLine}User: How are you?", result.Trim());
        }

        [Fact(DisplayName = "ProcessMessageAsync should append user message, get AI response, and update event store")]
        public async Task ProcessMessageAsync_ShouldAppendUserMessageAndReturnAIResponse()
        {
            // Arrange
            var sessionId = Guid.NewGuid();
            var userMessage = "What's the weather like?";
            var aiResponse =
                "I'm sorry, but as an AI language model, I don't have access to real-time weather information. You might want to check a weather website or app for the most up-to-date information about the weather in your area.";
            var temperature = 0.7;

            var fakeLanguageModelClient = A.Fake<fsEnsemble.ILanguageModelClient>();

            A.CallTo(() => _fakeEventStore.GetEventsAsync(sessionId, 0))
                .Returns(Task.FromResult((IEnumerable<SessionEvent>)new List<SessionEvent>()));

            A.CallTo(() => _fakeEventStore.AppendEventAsync(A<SessionEvent>._))
                .Returns(Task.CompletedTask);

            var contentResponse = new fsEnsemble.ContentResponse(aiResponse);
            var successResult = FSharpResult<fsEnsemble.ContentResponse, string>.NewOk(contentResponse);
            A.CallTo(() =>
                    fakeLanguageModelClient.GenerateContentAsync(
                        A<fsEnsemble.ContentRequest>.That.Matches(r =>
                            Math.Abs(r.Temperature - (float)temperature) < 0.001)))
                .Returns(Task.FromResult(successResult));

            // Act
            var result =
                await _contextWindowManager.ProcessMessageAsync(sessionId, userMessage, fakeLanguageModelClient,
                    temperature);

            // Assert
            Assert.Equal(aiResponse, result);

            A.CallTo(() => _fakeEventStore.AppendEventAsync(A<SessionEvent>.That.Matches(e =>
                e.SessionId == sessionId &&
                e.ModelName == "User" &&
                e.Content == userMessage))).MustHaveHappenedOnceExactly();

            A.CallTo(() => _fakeEventStore.AppendEventAsync(A<SessionEvent>.That.Matches(e =>
                e.SessionId == sessionId &&
                e.ModelName == fakeLanguageModelClient.GetType().Name &&
                e.Content == aiResponse))).MustHaveHappenedOnceExactly();
        }
    }
}