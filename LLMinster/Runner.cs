using System.Text.Json;

namespace LLMinster;

public static class Runner
{
    public static async Task RunAsync()
    {
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        var ep = new Watcher(configPath);
        var watchDir =
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText("config.json"))?
                ["WatchDirectory"].GetString();
        
        await ep.Run(watchDir);
    }
}