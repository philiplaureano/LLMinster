#r "nuget: Newtonsoft.Json, 13.0.3"
#r "nuget: LLMinster.Interfaces, 0.0.3"

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Collections.Generic;
using LLMinster.Interfaces;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

// Constants and Global Variables
const string HashesFilePath = "processed_open_router_hashes.json";
object _hashLock = new object();

// Constants for context file formatting
const string ENTRY_START = "<<ENTRY>>";
const string ENTRY_END = "<</ENTRY>>";
const string QUESTION_START = "<<QUESTION";  // Will be followed by ID
const string QUESTION_END = "<</QUESTION>>";
const string ANSWER_START = "<<ANSWER";      // Will be followed by ID
const string ANSWER_END = "<</ANSWER>>";

await Main();

async Task Main()
{
    var (apiKeys, appConfig) = LoadConfigurations();
    if (apiKeys == null || appConfig == null)
    {
        Console.WriteLine("Failed to load configuration. Exiting.");
        return;
    }

    var client = new OpenRouterClient(apiKeys.OpenRouterKey, appConfig.OpenRouterModel);

    Directory.CreateDirectory(appConfig.WatchDirectory);

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

(ApiKeys apiKeys, AppConfig appConfig) LoadConfigurations()
{
    const string apiKeysPath = "api-keys.json";
    const string configPath = "config.json";

    ApiKeys apiKeys = null;
    if (!File.Exists(apiKeysPath))
    {
        Console.WriteLine($"API keys file not found: {apiKeysPath}");
        return (null, null);
    }

    try
    {
        string jsonString = File.ReadAllText(apiKeysPath);
        apiKeys = JsonConvert.DeserializeObject<ApiKeys>(jsonString);

        if (string.IsNullOrWhiteSpace(apiKeys.OpenRouterKey))
        {
            Console.WriteLine("Invalid API keys configuration: OpenRouterKey must be specified.");
            return (null, null);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error loading API keys file: {ex.Message}");
        return (null, null);
    }

    AppConfig appConfig = null;
    if (!File.Exists(configPath))
    {
        Console.WriteLine($"Configuration file not found: {configPath}");
        return (null, null);
    }

    try
    {
        string jsonString = File.ReadAllText(configPath);
        appConfig = JsonConvert.DeserializeObject<AppConfig>(jsonString);

        if (string.IsNullOrWhiteSpace(appConfig.WatchDirectory))
        {
            Console.WriteLine("Invalid configuration: WatchDirectory must be specified.");
            return (null, null);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error loading configuration file: {ex.Message}");
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
            string json = File.ReadAllText(HashesFilePath);
            var hashes = JsonConvert.DeserializeObject<string[]>(json);
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
            var hashArray = hashes.Keys.ToArray();
            var json = JsonConvert.SerializeObject(hashArray);
            File.WriteAllText(HashesFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving processed hashes: {ex.Message}");
        }
    }
}

async Task WatchDirectoryAsync(string watchDir, OpenRouterClient client,
    ConcurrentDictionary<string, byte> processedHashes, CancellationToken ct)
{
    using var watcher = new FileSystemWatcher(watchDir);
    watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime;
    watcher.Filter = "*.open-router-q";

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

async Task ProcessFileAsync(string filePath, OpenRouterClient client,
    ConcurrentDictionary<string, byte> processedHashes)
{
    try
    {
        string extension = Path.GetExtension(filePath);
        if (extension.Equals(".answer.md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".context.md", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await WaitForFileAccess(filePath);

        string question = await File.ReadAllTextAsync(filePath);
        string hash = ComputeSHA256(question);

        if (processedHashes.TryAdd(hash, 1))
        {
            string baseFileName = Path.GetFileNameWithoutExtension(filePath);
            string contextFilePath = Path.Combine(Path.GetDirectoryName(filePath), $"{baseFileName}.context.md");
            string context = await LoadOrCreateContextFile(contextFilePath);

            var options = new GenerationOptions
            {
                Temperature = 1,
                MaxTokens = 16384
            };

            var stopwatch = Stopwatch.StartNew();
            var answer = await client.GenerateContentAsync(question, options);
            stopwatch.Stop();

            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

            // Generate a unique ID for this Q&A pair
            string qaId = Guid.NewGuid().ToString();

            // Update [filename].answer.md file with only the answer (no ID or delineators)
            string answerFilePath = Path.Combine(Path.GetDirectoryName(filePath), $"{baseFileName}.answer.md");
            await File.WriteAllTextAsync(answerFilePath, answer);

            // Append the new Q&A to the [filename].context.md file with IDs and delineators
            await AppendToContextFile(contextFilePath, question, answer, qaId);

            Console.WriteLine($"Processed file: {Path.GetFileName(filePath)}");
            Console.WriteLine($"Answer written to: {Path.GetFileName(answerFilePath)}");
            Console.WriteLine($"Context updated in: {Path.GetFileName(contextFilePath)}");
            Console.WriteLine($"Time taken for GenerateContent: {elapsedSeconds:F2} seconds");
            SaveProcessedHashes(processedHashes);
        }
        else
        {
            Console.WriteLine($"File content already processed: {Path.GetFileName(filePath)}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
    }
}

async Task<string> LoadOrCreateContextFile(string contextFilePath)
{
    if (File.Exists(contextFilePath))
    {
        return await File.ReadAllTextAsync(contextFilePath);
    }

    return "# Conversation Context\n\nThis file contains question-answer pairs with unique IDs.\n\n";
}

async Task AppendToContextFile(string contextFilePath, string question, string answer, string qaId)
{
    using (var writer = new StreamWriter(contextFilePath, append: true))
    {
        await writer.WriteLineAsync($"{ENTRY_START}");
        await writer.WriteLineAsync($"{QUESTION_START}:{qaId}>>");
        await writer.WriteLineAsync(question);
        await writer.WriteLineAsync(QUESTION_END);
        await writer.WriteLineAsync($"{ANSWER_START}:{qaId}>>");
        await writer.WriteLineAsync(answer);
        await writer.WriteLineAsync(ANSWER_END);
        await writer.WriteLineAsync($"{ENTRY_END}\n");
    }
}

string ComputeSHA256(string content)
{
    using (SHA256 sha256 = SHA256.Create())
    {
        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}

async Task WaitForFileAccess(string filePath, int maxAttempts = 10, int delayMs = 100)
{
    for (int i = 0; i < maxAttempts; i++)
    {
        try
        {
            using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                return;
            }
        }
        catch (IOException)
        {
            await Task.Delay(delayMs);
        }
    }

    throw new TimeoutException($"Unable to access file {filePath} after {maxAttempts} attempts.");
}

class ApiKeys
{
    public string OpenRouterKey { get; set; }
}

class AppConfig
{
    public string WatchDirectory { get; set; }
    public string OpenRouterModel { get; set; }
}

// OpenRouter Client Class
public class OpenRouterClient
{
    private const string ApiBaseUrl = "https://openrouter.ai/api/v1/chat/completions";
    private readonly string _apiKey;
    private readonly string _modelName;

    public OpenRouterClient(string apiKey, string modelName)
    {
        _apiKey = apiKey;
        _modelName = modelName;
    }

    public async Task<string> GenerateContentAsync(string prompt, GenerationOptions options)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var requestPayload = new
        {
            model = _modelName,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = options.Temperature,
            max_tokens = options.MaxTokens
        };

        var jsonPayload = JsonConvert.SerializeObject(requestPayload);
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(ApiBaseUrl, content);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        var chatResponse = JsonConvert.DeserializeObject<ChatCompletionResponse>(responseContent);

        return chatResponse.Choices[0].Message.Content;
    }

    private class ChatCompletionResponse
    {
        [JsonProperty("choices")]
        public Choice[] Choices { get; set; }
    }

    private class Choice
    {
        [JsonProperty("message")]
        public Message Message { get; set; }
    }

    private class Message
    {
        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }
    }
}
