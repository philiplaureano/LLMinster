#r "nuget: Newtonsoft.Json, 13.0.3"
#r "nuget: OpenAI-DotNet, 7.4.3"

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;
using Newtonsoft.Json;
using OpenAI;

const string HashesFilePath = "processed_o1_mini_hashes.json";
private object _hashLock = new object();

// Constants for context file formatting
const string ENTRY_START = "<<ENTRY>>";
const string ENTRY_END = "<</ENTRY>>";
const string QUESTION_START = "<<QUESTION";  // Will be followed by ID
const string QUESTION_END = "<</QUESTION>>";
const string ANSWER_START = "<<ANSWER";      // Will be followed by ID
const string ANSWER_END = "<</ANSWER>>";

async Task Main()
{
    var (apiKeys, appConfig) = LoadConfigurations();
    if (apiKeys == null || appConfig == null)
    {
        Console.WriteLine("Failed to load configuration. Exiting.");
        return;
    }

    using var api = new OpenAIClient(apiKeys.OpenAIKey);

    Directory.CreateDirectory(appConfig.WatchDirectory);

    var processedHashes = LoadProcessedHashes();
    var cts = new CancellationTokenSource();

    Console.WriteLine($"Watching directory: {appConfig.WatchDirectory}");
    Console.WriteLine("Watching for new or changed .o1-mini-q files. Press ENTER to exit.");

    var watcherTask = WatchDirectoryAsync(appConfig.WatchDirectory, api, processedHashes, cts.Token);

    Console.ReadLine();
    cts.Cancel();
    await watcherTask;

    SaveProcessedHashes(processedHashes);
}

(ApiKeys apiKeys, AppConfig appConfig) LoadConfigurations()
{
    const string apiKeysPath = "api-keys.json";
    const string configPath = "config.json";
    
    // Load API Keys
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

        if (string.IsNullOrWhiteSpace(apiKeys.OpenAIKey))
        {
            Console.WriteLine("Invalid API keys configuration: OpenAIKey must be specified.");
            return (null, null);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error loading API keys file: {ex.Message}");
        return (null, null);
    }

    // Load App Config
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
    lock(_hashLock) 
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

async Task WatchDirectoryAsync(string watchDir, OpenAIClient api, 
    ConcurrentDictionary<string, byte> processedHashes, CancellationToken ct)
{
    using var watcher = new FileSystemWatcher(watchDir);
    watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime;
    watcher.Filter = "*.o1-mini-q";

    watcher.Changed += async (sender, e) => await ProcessFileAsync(e.FullPath, api, processedHashes);
    watcher.Created += async (sender, e) => await ProcessFileAsync(e.FullPath, api, processedHashes);

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

async Task ProcessFileAsync(string filePath, OpenAIClient api,
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

            var messages = new List<Message>
            {
                new Message(Role.System, context),
                new Message(Role.User, $"Latest question: {question}")
            };

            var chatRequest = new ChatRequest(messages, "o1-mini")
            {
                Temperature = 0.025f,
                MaxTokens = 32768
            };
            
            var stopwatch = Stopwatch.StartNew();
            var response = await api.ChatEndpoint.GetCompletionAsync(chatRequest);
            stopwatch.Stop();
            
            string answer = response.FirstChoice.Message.Content.Trim();
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

            // Generate a unique ID for this Q&A pair
            string qaId = Guid.NewGuid().ToString();

            // Update [filename].answer.md file with only the answer (no ID or delineators)
            string answerFilePath = Path.Combine(Path.GetDirectoryName(filePath), $"{baseFileName}.answer.md");
            await File.WriteAllTextAsync(answerFilePath, answer);

            // Append the new Q&A to the [filename].context.md file with IDs and delineators
            await AppendToContextFile(contextFilePath, question, answer, qaId);

            string timestamp = GetFormattedTimestamp();
            Console.WriteLine($"{timestamp} Processed file: {Path.GetFileName(filePath)}");
            Console.WriteLine($"{timestamp} Answer written to: {Path.GetFileName(answerFilePath)}");
            Console.WriteLine($"{timestamp} Context updated in: {Path.GetFileName(contextFilePath)}");
            Console.WriteLine($"{timestamp} Time taken for GenerateContent: {elapsedSeconds:F2} seconds");
            SaveProcessedHashes(processedHashes);
        }
        else
        {
            Console.WriteLine($"{GetFormattedTimestamp()} File content already processed: {Path.GetFileName(filePath)}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{GetFormattedTimestamp()} Error processing file {filePath}: {ex.Message}");
    }
}

string GetFormattedTimestamp()
{
    var utcNow = DateTime.UtcNow;
    var localNow = DateTime.Now;
    return $"[UTC: {utcNow:yyyy-MM-dd HH:mm:ss} (Local: {localNow:yyyy-MM-dd HH:mm:ss})]";
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
        byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
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
    public string OpenAIKey { get; set; }
}

class AppConfig
{
    public string WatchDirectory { get; set; }
}

await Main();