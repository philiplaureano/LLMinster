#r "System.IO"
#r "System.Text.RegularExpressions"

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;

// Constants for patterns - exact format match
const string ENTRY_START = @"<<ENTRY>>\s*";
const string QUESTION_PART = @"<<QUESTION:([^>]+)>>\s*(.*?)\s*<</QUESTION>>";
const string ANSWER_PART = @"<<ANSWER:([^>]+)>>\s*(.*?)\s*<</ANSWER>>";  // Fixed format
const string ENTRY_END = @"\s*<</ENTRY>>";

try
{
    // Get and validate file path
    string filePath = Args.Count > 0 ? Args[0] : GetFilePath();
    Console.WriteLine($"Processing file: {filePath}");
    
    ValidateFilePath(filePath);

    // Read file content and create backup
    string fileContent = File.ReadAllText(filePath);
    string backupPath = filePath + ".backup";
    File.WriteAllText(backupPath, fileContent);
    Console.WriteLine($"Backup created at: {backupPath}");

    Console.WriteLine($"\nFile content length: {fileContent.Length}");
    
    // Parse entries with content verification
    var entries = GetEntries(fileContent);
    if (!entries.Any())
    {
        Console.WriteLine("No valid entries found. No changes will be made.");
        return;
    }

    Console.WriteLine($"\nFound {entries.Count} entries to process.");
    Console.WriteLine($"Original file size: {fileContent.Length / 1024.0:F2} KB");
    
    var keptEntries = ProcessEntries(entries);
    SaveModifiedContent(keptEntries, filePath, fileContent);
}
catch (Exception ex)
{
    Console.WriteLine($"\nError: {ex.Message}\n{ex.StackTrace}");
    Environment.Exit(1);
}

List<(string FullEntry, string Id, string Question, string Answer)> GetEntries(string content)
{
    var entries = new List<(string FullEntry, string Id, string Question, string Answer)>();
    
    // Split into individual entries first
    var entryTexts = Regex.Split(content, @"(?=<<ENTRY>>)").Where(e => e.Trim().StartsWith("<<ENTRY>>")).ToList();
    Console.WriteLine($"\nFound {entryTexts.Count} potential entries");

    foreach (var entryText in entryTexts)
    {
        try
        {
            // Extract question ID and content
            var questionMatch = Regex.Match(entryText, @"<<QUESTION:([^>]+)>>\s*(.*?)\s*<</QUESTION>>", RegexOptions.Singleline);
            if (!questionMatch.Success)
            {
                Console.WriteLine("Failed to match question pattern");
                continue;
            }

            string id = questionMatch.Groups[1].Value.Trim();
            string question = questionMatch.Groups[2].Value.Trim();

            // Extract answer using the same ID
            var answerPattern = $@"<<ANSWER:{id}>>\s*(.*?)\s*<</ANSWER>>";
            var answerMatch = Regex.Match(entryText, answerPattern, RegexOptions.Singleline);
            if (!answerMatch.Success)
            {
                Console.WriteLine($"Failed to match answer pattern for ID: {id}");
                continue;
            }

            string answer = answerMatch.Groups[1].Value.Trim();
            
            entries.Add((
                FullEntry: entryText.Trim(), 
                Id: id, 
                Question: question, 
                Answer: answer));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to parse entry: {ex.Message}");
            continue;
        }
    }

    Console.WriteLine($"\nSuccessfully parsed {entries.Count} valid entries");
    
    // Debug output for first parsed entry if available
    if (entries.Any())
    {
        var first = entries.First();
        Console.WriteLine("\nFirst parsed entry details:");
        Console.WriteLine($"ID: {first.Id}");
        Console.WriteLine("Question (first 100 chars):");
        Console.WriteLine(first.Question.Substring(0, Math.Min(100, first.Question.Length)));
        Console.WriteLine("Answer (first 100 chars):");
        Console.WriteLine(first.Answer.Substring(0, Math.Min(100, first.Answer.Length)));
    }

    return entries;
}

List<string> ProcessEntries(List<(string FullEntry, string Id, string Question, string Answer)> entries)
{
    var keptEntries = new List<string>();
    int totalEntries = entries.Count;
    int processedCount = 0;

    foreach (var entry in entries)
    {
        processedCount++;
        Console.WriteLine($"\nProcessing entry {processedCount} of {totalEntries}");
        Console.WriteLine(new string('=', 80));
        Console.WriteLine($"Question/Answer ID: {entry.Id}");
        
        // Show question preview
        string questionPreview = entry.Question.Length > 100 
            ? entry.Question.Substring(0, 100) + "..." 
            : entry.Question;
        Console.WriteLine("\nQuestion Preview (first 100 chars):");
        Console.WriteLine(questionPreview);
        if (entry.Question.Length > 100)
        {
            Console.WriteLine("(Full question content preserved in original format)");
        }

        // Show answer preview
        string answerPreview = entry.Answer.Length > 100 
            ? entry.Answer.Substring(0, 100) + "..." 
            : entry.Answer;
        Console.WriteLine("\nAnswer Preview (first 100 chars):");
        Console.WriteLine(answerPreview);
        if (entry.Answer.Length > 100)
        {
            Console.WriteLine("(Full answer content preserved in original format)");
        }

        if (!ShouldDeleteEntry())
        {
            keptEntries.Add(entry.FullEntry);
            DisplayCurrentStats(keptEntries);
        }
    }

    Console.WriteLine($"\nKept {keptEntries.Count} out of {totalEntries} entries.");
    return keptEntries;
}

bool ShouldDeleteEntry()
{
    while (true)
    {
        Console.Write("\nDelete this entry? (Y/N - Press Enter to keep): ");
        string input = Console.ReadLine()?.Trim().ToUpper() ?? "";
        
        if (input == "Y") return true;
        if (input == "N" || input == "") return false;
        
        Console.WriteLine("Please enter Y, N, or press Enter to keep.");
    }
}

void DisplayCurrentStats(List<string> entries)
{
    string currentContent = string.Join("\n", entries);
    long sizeInBytes = System.Text.Encoding.UTF8.GetByteCount(currentContent);
    double sizeInKB = sizeInBytes / 1024.0;
    long tokenApprox = sizeInBytes / 4;

    Console.WriteLine($"\nCurrent file size: {sizeInKB:F2} KB");
    Console.WriteLine($"Approximate tokens: {tokenApprox:N0}");
}

string GetFilePath()
{
    while (true)
    {
        Console.Write("Enter the path to the .context.md file: ");
        string path = Console.ReadLine();
        if (!string.IsNullOrEmpty(path))
        {
            return path.Trim();
        }
        Console.WriteLine("Please enter a valid file path.");
    }
}

void ValidateFilePath(string path)
{
    if (!File.Exists(Path.GetFullPath(path)))
    {
        throw new FileNotFoundException($"File not found: {path}");
    }
    
    if (!Path.GetFileName(path).EndsWith(".context.md", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("File must have .context.md extension");
    }
}

void SaveModifiedContent(List<string> keptEntries, string originalFilePath, string originalContent)
{
    while (true)
    {
        try
        {
            string saveFilePath = GetSaveFilePath(originalFilePath);
            string newContent = string.Join("\n", keptEntries);

            // Verify we're not saving an empty file
            if (string.IsNullOrWhiteSpace(newContent))
            {
                Console.WriteLine("WARNING: New content is empty! Operation cancelled.");
                return;
            }

            // Size verification
            if (newContent.Length < originalContent.Length * 0.1)  // If new content is less than 10% of original
            {
                Console.WriteLine("\nWARNING: New content is significantly smaller than original!");
                Console.WriteLine($"Original size: {originalContent.Length / 1024.0:F2} KB");
                Console.WriteLine($"New size: {newContent.Length / 1024.0:F2} KB");
                Console.Write("Are you sure you want to continue? (Y/N): ");
                if (Console.ReadLine()?.Trim().ToUpper() != "Y")
                {
                    Console.WriteLine("Operation cancelled.");
                    return;
                }
            }

            File.WriteAllText(saveFilePath, newContent);

            // Display final statistics
            var fileInfo = new FileInfo(saveFilePath);
            Console.WriteLine($"\nFile saved successfully to: {saveFilePath}");
            Console.WriteLine($"Final file size: {fileInfo.Length / 1024.0:F2} KB");
            Console.WriteLine($"Final token estimate: {fileInfo.Length / 4:N0}");
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError saving file: {ex.Message}");
            if (!PromptRetry())
            {
                throw;
            }
        }
    }
}

string GetSaveFilePath(string originalFilePath)
{
    Console.Write("\nEnter new filename (press Enter to overwrite original): ");
    string newName = Console.ReadLine() ?? "";
    newName = newName.Trim();
    
    if (string.IsNullOrEmpty(newName))
    {
        return originalFilePath;
    }

    // Ensure the filename ends with .context.md
    if (!newName.EndsWith(".context.md", StringComparison.OrdinalIgnoreCase))
    {
        newName += ".context.md";
    }

    // Use the same directory as the original file
    string directory = Path.GetDirectoryName(originalFilePath) ?? ".";
    string newPath = Path.Combine(directory, newName);

    // Check if file exists and confirm overwrite
    if (File.Exists(newPath))
    {
        Console.Write("File already exists. Overwrite? (Y/N): ");
        string response = Console.ReadLine() ?? "N";
        if (response.Trim().ToUpper() != "Y")
        {
            return GetSaveFilePath(originalFilePath);
        }
    }

    return newPath;
}

bool PromptRetry()
{
    Console.Write("Would you like to try again? (Y/N): ");
    string response = Console.ReadLine() ?? "N";
    return response.Trim().ToUpper() == "Y";
}