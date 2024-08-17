#r "nuget: OpenAI, 1.11.0"
#r "nuget: fsEnsemble, 0.1.5" 

using System;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using OpenAI_API;
using OpenAI_API.Chat;
using OpenAI_API.Models;
using static fsEnsemble;

public class StreamingChatGptClient(string apiKey, Model chatModel)
    : ChatGptClient(apiKey, chatModel), IStreamingLanguageModelClient
{
    private string _apiKey = apiKey;

    public async Task<IAsyncEnumerable<string>> GenerateContentStreamAsync(ContentRequest request)
    {
        var api = new OpenAIAPI(_apiKey);
        var chatMessage = new ChatMessage(ChatMessageRole.User, request.Prompt);
        var messages = new List<ChatMessage>([chatMessage]);
        var chatRequest = new ChatRequest() { Model = "gpt-4o-mini", Temperature = request.Temperature, Messages = messages};

        // Create a channel to stream the chunks
        var channel = Channel.CreateUnbounded<string>();

        // Start a task to asynchronously write to the channel
        await Task.Run(async () => await StreamCompletionToChannel(api, chatRequest, channel));

        // Return the channel reader as IAsyncEnumerable
        return channel.Reader.ReadAllAsync();
    }

    private async Task StreamCompletionToChannel(OpenAIAPI api, ChatRequest chatRequest, Channel<string> channel)
    {
        try
        {
            await foreach (var msg in api.Chat.StreamChatEnumerableAsync(chatRequest.Messages, model: chatRequest.Model,
                               temperature: chatRequest.Temperature))
            {
                if (msg?.Choices == null || !msg.Choices.Any())
                    continue;

                var firstChoice = msg.Choices[0];
                var content = firstChoice?.Delta?.TextContent;
                if (string.IsNullOrEmpty(content))
                    continue;

                foreach (var ch in content)
                {
                    await channel.Writer.WriteAsync(ch.ToString());
                }
            }

            // Signal completion
            channel.Writer.Complete();
        }
        catch (Exception ex)
        {
            // Handle exceptions (e.g., log the error, write an error to the channel)
            channel.Writer.Complete(ex);
        }
    }
}

// Main script logic
var apiKey = "YOUR_API_KEY";
var model = "gpt-4o-mini"; // Or your preferred model

var chatGptClient = new StreamingChatGptClient(apiKey, model);

var conversationHistory = new StringBuilder("Hello, ChatGPT!");

Console.WriteLine("----- Conversation History -----");

int GetRandomKeyDelay()
{
    var random = new Random();
    var upperDelay = 50;
    var lowerDelay = 0;
    return random.Next(lowerDelay, upperDelay);
}

async Task PrintString(string text)
{
	foreach(var ch in text)
	{
		Console.Write(ch);
		await Task.Delay(GetRandomKeyDelay());
	}
}

while (true)
{
    var currentHistory = conversationHistory.ToString();
    Console.WriteLine(currentHistory);

    var request = new ContentRequest(prompt: currentHistory, temperature: 0.7f);

    // Stream the response
    try
    {
    	await PrintString($"ChatGPT {model}: ");
        await foreach (var chunk in await chatGptClient.GenerateContentStreamAsync(request))
        {
            if(chunk?.Length >0)
            {            
            	await PrintString(chunk);
            	conversationHistory.Append(chunk);
            }            
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\nError: {ex.Message}");
    }

    await PrintString("\nPrompt (press ENTER to continue): ");

    string userInput = Console.ReadLine();
    if (userInput?.ToLower() == "exit")
    {
        break;
    }

    conversationHistory.Append($"\nMe: {userInput}");
}

Console.WriteLine("-------------------------------");