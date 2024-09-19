using System.Collections.Concurrent;
using System.Dynamic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMinster.Clients;
using LLMinster.Interfaces;
using Microsoft.CodeAnalysis;
using RazorLight;
using Serilog;

namespace LLMinster
{
    public class Watcher
    {
        private const string HashesFilePath = "processed_hashes.json";
        private readonly object _hashLock = new();

        private readonly Dictionary<string, ProviderConfig> _providers;
        private readonly Dictionary<string, (string Provider, string Model)> _aliasLookup;
        private readonly string _defaultAlias;
        private readonly ConcurrentDictionary<string, byte> _processedHashes = new();

        private readonly ConcurrentDictionary<string, DateTime> _recentlyProcessedFiles = new();

        private readonly RazorLightEngine _razorEngine;

        public Watcher(string configPath)
        {
            var configJson = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<ConfigurationRoot>(configJson);
            _providers = config.Providers;
            _defaultAlias = config.DefaultAlias;
            _aliasLookup = BuildAliasLookup(config.Providers);

            _razorEngine = new RazorLightEngineBuilder()
                .UseMemoryCachingProvider()
                .UseEmbeddedResourcesProject(GetType()) // Use embedded resources if needed
                .AddMetadataReferences(MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location))
                .AddMetadataReferences(MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location))
                .AddMetadataReferences(MetadataReference.CreateFromFile(Assembly.Load("netstandard").Location))
                .Build();
            
            // Initialize Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }

        private Dictionary<string, (string Provider, string Model)> BuildAliasLookup(
            Dictionary<string, ProviderConfig> providers)
        {
            var lookup = new Dictionary<string, (string Provider, string Model)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (providerName, providerConfig) in providers)
            {
                foreach (var (modelName, alias) in providerConfig.Models)
                {
                    lookup[alias] = (providerName, modelName);
                }
            }

            return lookup;
        }

        public async Task Run(string watchDirectory)
        {
            try
            {
                Log.Information("Application Starting");

                if (!Directory.Exists(watchDirectory))
                {
                    Directory.CreateDirectory(watchDirectory);
                    Log.Information("Created watch directory: {WatchDirectory}", watchDirectory);
                }

                var processedHashes = LoadProcessedHashes();
                var cts = new CancellationTokenSource();

                Log.Information("Watching directory: {WatchDirectory}", watchDirectory);
                Log.Information("Watching for new or changed .q and .razorq files. Press ENTER to exit.");

                var watcherTask = WatchDirectoryAsync(watchDirectory, processedHashes, cts.Token);

                Console.ReadLine();
                Log.Information("Shutdown initiated.");
                cts.Cancel();
                await watcherTask;

                SaveProcessedHashes(processedHashes);
                Log.Information("Application Exited Gracefully.");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application terminated unexpectedly");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private async Task WatchDirectoryAsync(string watchDir, ConcurrentDictionary<string, byte> processedHashes,
            CancellationToken ct)
        {
            using var watcher = new FileSystemWatcher(watchDir);
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime;
            watcher.Filter = "*.*";

            watcher.Changed += async (sender, e) => await OnChangedAsync(e.FullPath, processedHashes);
            watcher.Created += async (sender, e) => await OnChangedAsync(e.FullPath, processedHashes);

            watcher.EnableRaisingEvents = true;

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (TaskCanceledException)
            {
                Log.Information("Watcher stopped.");
            }
        }

        private async Task OnChangedAsync(string filePath, ConcurrentDictionary<string, byte> processedHashes)
        {
            var now = DateTime.UtcNow;

            if (_recentlyProcessedFiles.TryGetValue(filePath, out var lastProcessed))
                if ((now - lastProcessed).TotalMilliseconds < 500) // 500ms debounce interval
                    return;

            _recentlyProcessedFiles[filePath] = now;

            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            if (extension == ".q")
                await ProcessQFileAsync(filePath, processedHashes);

            if (extension == ".razorq")
                await ProcessRazorQFileAsync(filePath);
        }

        private async Task ProcessQFileAsync(string filePath, ConcurrentDictionary<string, byte> processedHashes)
        {
            try
            {
                var extension = Path.GetExtension(filePath);
                if (extension.Equals(".answer.md", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".context.md", StringComparison.OrdinalIgnoreCase))
                    return;

                await WaitForFileAccess(filePath);

                var fileContent = await File.ReadAllTextAsync(filePath);
                var hash = ComputeSHA256(fileContent);

                if (processedHashes.TryAdd(hash, 1))
                {
                    var (alias, prompt) = ExtractAliasAndPrompt(fileContent);

                    var client = GetClient(alias);
                    var options = new GenerationOptions
                    {
                        Temperature = 0.025f,
                        MaxTokens = 4096
                    };

                    var answer = await client.GenerateContentAsync(prompt, options);

                    var baseFileName = Path.GetFileNameWithoutExtension(filePath);
                    var directory = Path.GetDirectoryName(filePath) ?? string.Empty;

                    // Update [filename].answer.md file with only the answer
                    var answerFilePath = Path.Combine(directory, $"{baseFileName}.answer.md");
                    await File.WriteAllTextAsync(answerFilePath, answer);

                    // Append the new Q&A to the [filename].context.md file
                    var contextFilePath = Path.Combine(directory, $"{baseFileName}.context.md");
                    await AppendToContextFile(contextFilePath, prompt, answer, client.Name);

                    Log.Information("Processed file: {FileName} using {LLMClient}", Path.GetFileName(filePath),
                        client.Name);
                    Log.Information("Answer written to: {FileName}", Path.GetFullPath(answerFilePath));
                    Log.Information("Context updated in: {FileName}", Path.GetFileName(contextFilePath));

                    SaveProcessedHashes(processedHashes); // Save hashes after each successful processing
                }
                else
                {
                    Log.Information("File content already processed: {FileName}", Path.GetFileName(filePath));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing file {FilePath}", filePath);
            }
        }

        private async Task ProcessRazorQFileAsync(string filePath)
        {
            try
            {
                await WaitForFileAccess(filePath);

                var templateContent = await File.ReadAllTextAsync(filePath);
                var (alias, template) = ExtractAliasAndTemplate(templateContent);

                // Render the Razor template
                var result = await _razorEngine.CompileRenderStringAsync<dynamic>(
                    key: filePath,  // Use the file path as a unique key
                    content: template,
                    model: new ExpandoObject(),  // Use an empty ExpandoObject as the model
                    viewBag: null
                );

                // Process the rendered result as we do with .q files
                var hash = ComputeSHA256(result);

                if (_processedHashes.TryAdd(hash, 1))
                {
                    var client = GetClient(alias);
                    var options = new GenerationOptions
                    {
                        Temperature = 0.025f,
                        MaxTokens = 4096
                    };

                    var answer = await client.GenerateContentAsync(result, options);

                    var baseFileName = Path.GetFileNameWithoutExtension(filePath);
                    var directory = Path.GetDirectoryName(filePath) ?? string.Empty;

                    // Update [filename].answer.md file with only the answer
                    var answerFilePath = Path.Combine(directory, $"{baseFileName}.answer.md");
                    await File.WriteAllTextAsync(answerFilePath, answer);

                    // Append the new Q&A to the [filename].context.md file
                    var contextFilePath = Path.Combine(directory, $"{baseFileName}.context.md");
                    await AppendToContextFile(contextFilePath, result, answer, client.Name);

                    Log.Information("Processed Razor file: {FileName} using {LLMClient}", Path.GetFileName(filePath),
                        client.Name);
                    Log.Information("Answer written to: {FileName}", Path.GetFullPath(answerFilePath));
                    Log.Information("Context updated in: {FileName}", Path.GetFileName(contextFilePath));

                    SaveProcessedHashes(_processedHashes);
                }
                else
                {
                    Log.Information("Razor file content already processed: {FileName}", Path.GetFileName(filePath));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing Razor file {FilePath}", filePath);
            }
        }
        private (string Alias, string Template) ExtractAliasAndTemplate(string content)
        {
            var alias = _defaultAlias;
            var lines = content.Split('\n');
            var templateBuilder = new System.Text.StringBuilder();
            bool modelDirectiveFound = false;

            foreach (var line in lines)
            {
                if (!modelDirectiveFound && line.TrimStart().StartsWith("@usemodel", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { ':' }, 2);
                    if (parts.Length == 2)
                    {
                        alias = parts[1].Trim();
                    }
                    modelDirectiveFound = true;
                }
                else
                {
                    templateBuilder.AppendLine(line);
                }
            }

            return (alias, templateBuilder.ToString().TrimStart());
        }
        private ILLMClient GetClient(string alias)
        {
            if (!_aliasLookup.TryGetValue(alias, out var providerModel))
            {
                if (!_aliasLookup.TryGetValue(_defaultAlias, out providerModel))
                {
                    throw new ArgumentException($"Invalid alias and no valid default alias: {alias}");
                }
            }

            var (providerName, modelName) = providerModel;
            var providerConfig = _providers[providerName];

            switch (providerName)
            {
                case "Anthropic":
                    return new AnthropicClaudeClient(providerConfig.ApiKey, modelName);
                case "OpenAI":
                    return new OpenAiClient(providerConfig.ApiKey, modelName);
                case "Google":
                    return new GoogleGeminiClient(providerConfig.ApiKey, modelName);
                default:
                    throw new ArgumentException($"Unsupported provider: {providerName}");
            }
        }

        private (string Alias, string Prompt) ExtractAliasAndPrompt(string content)
        {
            var lines = content.Split('\n');
            var alias = _defaultAlias;
            var promptStart = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("@usemodel", StringComparison.OrdinalIgnoreCase))
                {
                    alias = lines[i].Split(':', 2)[1].Trim();
                    promptStart = i + 1;
                    break;
                }
            }

            var prompt = string.Join("\n", lines[promptStart..]);
            return (alias, prompt);
        }

        private async Task AppendToContextFile(string contextFilePath, string question, string answer, string llmClientName)
        {
            // Remove @usemodel directive if present
            question = RemoveUseModelDirective(question);

            using (var writer = new StreamWriter(contextFilePath, true))
            {
                await writer.WriteLineAsync($"User:\n\n{question}\n");
                await writer.WriteLineAsync($"AI Assistant ({llmClientName}):\n\n{answer}\n");
                await writer.WriteLineAsync("---\n"); // Add a horizontal rule for separation
            }

            Log.Debug("Appended Q&A to context file {ContextFilePath}", contextFilePath);
        }

        private string RemoveUseModelDirective(string content)
        {
            var lines = content.Split('\n');
            var result = new System.Text.StringBuilder();
            bool directiveFound = false;

            foreach (var line in lines)
            {
                if (!directiveFound && line.TrimStart().StartsWith("@usemodel", StringComparison.OrdinalIgnoreCase))
                {
                    directiveFound = true;
                    continue; // Skip this line
                }
                result.AppendLine(line);
            }

            return result.ToString().TrimStart();
        }

        private string ComputeSHA256(string content)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        private async Task WaitForFileAccess(string filePath, int maxAttempts = 10, int delayMs = 100)
        {
            for (var i = 0; i < maxAttempts; i++)
                try
                {
                    using (var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        stream.Close();
                        Log.Debug("Accessed file {FilePath}", filePath);
                        return;
                    }
                }
                catch (IOException)
                {
                    Log.Debug("File {FilePath} is locked. Attempt {Attempt} of {MaxAttempts}. Retrying in {Delay}ms.",
                        filePath, i + 1, maxAttempts, delayMs);
                    await Task.Delay(delayMs);
                }

            throw new TimeoutException($"Unable to access file {filePath} after {maxAttempts} attempts.");
        }

        private ConcurrentDictionary<string, byte> LoadProcessedHashes()
        {
            if (!File.Exists(HashesFilePath))
            {
                Log.Information("Hashes file not found. Starting with an empty hash set.");
                return new ConcurrentDictionary<string, byte>();
            }

            try
            {
                var json = File.ReadAllText(HashesFilePath);
                var hashes = JsonSerializer.Deserialize<string[]>(json);
                Log.Information("Loaded {HashCount} processed hashes.", hashes?.Length ?? 0);
                return new ConcurrentDictionary<string, byte>(hashes?.ToDictionary(h => h, h => (byte)1) ??
                                                              new Dictionary<string, byte>());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading processed hashes.");
            }

            return new ConcurrentDictionary<string, byte>();
        }

        private void SaveProcessedHashes(ConcurrentDictionary<string, byte> hashes)
        {
            lock (_hashLock)
            {
                try
                {
                    var hashArray = hashes.Keys.ToArray();
                    var json = JsonSerializer.Serialize(hashArray, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(HashesFilePath, json);
                    Log.Debug("Saved {HashCount} processed hashes to {HashesFilePath}", hashArray.Length,
                        HashesFilePath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error saving processed hashes.");
                }
            }
        }
    }
}