#r "nuget: Newtonsoft.Json, 13.0.3"

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

// Constants and Global Variables
const string HashesFilePath = "processed_open_router_hashes.json";
object _hashLock = new object();

const string ENTRY_START = "<<ENTRY>>";
const string ENTRY_END = "<</ENTRY>>";
const string QUESTION_START = "<<QUESTION"; // Followed by ID
const string QUESTION_END = "<</QUESTION>>";
const string ANSWER_START = "<<ANSWER";     // Followed by ID
const string ANSWER_END = "<</ANSWER>>";

await MainAsync();

async Task MainAsync()
{
    // Load configurations
    var (apiKeys, appConfig) = LoadConfigurations();
    if (apiKeys == null || appConfig == null)
    {
        Console.WriteLine("Failed to load configuration. Exiting.");
        return;
    }

    // Initialize the OpenRouterClient
    var client = new OpenRouterClient(apiKeys.OpenRouterKey, appConfig.OpenRouterModel);
    Directory.CreateDirectory(appConfig.WatchDirectory);

    // Load processed hashes and set up cancellation token
    var processedHashes = LoadProcessedHashes();
    var cts = new CancellationTokenSource();

    Console.WriteLine($"Watching directory: {appConfig.WatchDirectory}");
    Console.WriteLine("Watching for new or changed .open-router-q files. Press ENTER to exit.");

    var watcherTask = WatchDirectoryAsync(appConfig.WatchDirectory, client, processedHashes, cts.Token);

    Console.ReadLine();
    cts.Cancel();
    await watcherTask;

    SaveProcessedHashes(processedHashes);
}

// Class Definitions for API Keys, Config, and Client

public class ApiKeys
{
    [JsonProperty("OpenRouterKey")]
    public string OpenRouterKey { get; set; }
}

public class AppConfig
{
    [JsonProperty("WatchDirectory")]
    public string WatchDirectory { get; set; }

    [JsonProperty("OpenRouterModel")]
    public string OpenRouterModel { get; set; }
}

public interface ILLMClient
{
    Task<string> GenerateContentAsync(string prompt, GenerationOptions options);
}

public class GenerationOptions
{
    public double Temperature { get; set; }
    public int MaxTokens { get; set; }
}

public class OpenRouterClient : ILLMClient
{
    private const string ApiBaseUrl = "https://openrouter.ai/api/v1/chat/completions";
    private readonly string _apiKey;
    private readonly string _modelName;

    public OpenRouterClient(string apiKey, string modelName)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
    }

    public async Task<string> GenerateContentAsync(string prompt, GenerationOptions options)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt cannot be null or empty.", nameof(prompt));

        if (options == null)
            throw new ArgumentNullException(nameof(options));

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var requestPayload = new
        {
            model = _modelName,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature = options.Temperature,
            max_tokens = options.MaxTokens
        };

        var jsonPayload = JsonConvert.SerializeObject(requestPayload);
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(ApiBaseUrl, content);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"API Error: {response.StatusCode}");
            Console.WriteLine($"Error Details: {responseContent}");
            throw new InvalidOperationException($"API request failed with status code {response.StatusCode}.");
        }

        var chatResponse = JsonConvert.DeserializeObject<ChatCompletionResponse>(responseContent);

        if (chatResponse?.Choices != null && chatResponse.Choices.Length > 0)
        {
            return chatResponse.Choices[0].Message.Content;
        }

        throw new InvalidOperationException("No response received from the API.");
    }
}

// Chat Completion API Classes
public class ChatCompletionResponse
{
    [JsonProperty("choices")]
    public Choice[] Choices { get; set; }
}

public class Choice
{
    [JsonProperty("message")]
    public Message Message { get; set; }
}

public class Message
{
    [JsonProperty("role")]
    public string Role { get; set; }

    [JsonProperty("content")]
    public string Content { get; set; }
}

// Supporting Functions
(ApiKeys apiKeys, AppConfig appConfig) LoadConfigurations()
{
    const string apiKeysPath = "api-keys.json";
    const string configPath = "config.json";

    ApiKeys apiKeys = null;
    AppConfig appConfig = null;

    if (!File.Exists(apiKeysPath))
    {
        Console.WriteLine($"API keys file not found: {apiKeysPath}");
        return (null, null);
    }

    if (!File.Exists(configPath))
    {
        Console.WriteLine($"Configuration file not found: {configPath}");
        return (null, null);
    }

    try
    {
        apiKeys = JsonConvert.DeserializeObject<ApiKeys>(File.ReadAllText(apiKeysPath));
        appConfig = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(configPath));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error loading configuration: {ex.Message}");
        return (null, null);
    }

    return (apiKeys, appConfig);
}

ConcurrentDictionary<string, byte> LoadProcessedHashes()
{
    if (File.Exists(HashesFilePath))
    {
        try
        {
            var hashes = JsonConvert.DeserializeObject<string[]>(File.ReadAllText(HashesFilePath));
            return new ConcurrentDictionary<string, byte>(hashes.ToDictionary(h => h, h => (byte)1));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading processed hashes: {ex.Message}");
        }
    }

    return new ConcurrentDictionary<string, byte>();
}

void SaveProcessedHashes(ConcurrentDictionary<string, byte> hashes)
{
    lock (_hashLock)
    {
        try
        {
            File.WriteAllText(HashesFilePath, JsonConvert.SerializeObject(hashes.Keys.ToArray()));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving processed hashes: {ex.Message}");
        }
    }
}

async Task WatchDirectoryAsync(string watchDir, ILLMClient client, ConcurrentDictionary<string, byte> processedHashes, CancellationToken ct)
{
    using var watcher = new FileSystemWatcher(watchDir)
    {
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        Filter = "*.open-router-q"
    };

    watcher.Changed += async (sender, e) => await ProcessFileAsync(e.FullPath, client, processedHashes);
    watcher.Created += async (sender, e) => await ProcessFileAsync(e.FullPath, client, processedHashes);

    watcher.EnableRaisingEvents = true;

    try
    {
        await Task.Delay(Timeout.Infinite, ct);
    }
    catch (TaskCanceledException)
    {
        Console.WriteLine("Watcher stopped.");
    }
}

async Task ProcessFileAsync(string filePath, ILLMClient client, ConcurrentDictionary<string, byte> processedHashes)
{
    try
    {
        await WaitForFileAccess(filePath);
        var question = await File.ReadAllTextAsync(filePath);
        var hash = ComputeSHA256(question);

        if (processedHashes.TryAdd(hash, 1))
        {
            var options = new GenerationOptions { Temperature = 0.025, MaxTokens = 16384 };
            var answer = await client.GenerateContentAsync(question, options);

            var answerFilePath = $"{filePath}.answer.md";
            await File.WriteAllTextAsync(answerFilePath, answer);

            Console.WriteLine($"Processed: {filePath}, Answer: {answerFilePath}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
    }
}

async Task WaitForFileAccess(string filePath, int maxAttempts = 10, int delayMs = 100)
{
    for (int i = 0; i < maxAttempts; i++)
    {
        try
        {
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                return;
        }
        catch (IOException)
        {
            await Task.Delay(delayMs);
        }
    }

    throw new TimeoutException($"Unable to access file {filePath} after {maxAttempts} attempts.");
}

string ComputeSHA256(string content)
{
    using var sha256 = SHA256.Create();
    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
}
