# Context Cleanup Script for LLMinster

This script helps LLMinster users manage and reduce the size of their LLM context files (`.context.md` files). These files represent the conversation history and context window of interactions with Language Learning Models in LLMinster.

## Purpose

As conversations with LLMs grow, the context files can become quite large, potentially containing thousands of question-answer pairs. This script allows you to:
- Review each question-answer pair in your context file
- Selectively remove entries you no longer need
- Keep track of file size and approximate token count
- Preserve the entries that are important to maintain context
- Save the cleaned-up context while maintaining the original file format

## Features

- **Preview Mode**: Shows the first 100 characters of each question and answer while preserving the full content
- **Easy Navigation**: Press Enter to keep an entry, 'Y' to delete, or 'N' to explicitly keep
- **Size Tracking**: Displays current file size and estimated token count after each decision
- **Safety Features**:
  - Creates a backup of the original file
  - Warns about significant size reductions
  - Confirms before overwriting existing files
  - Validates file format and structure

## Usage

1. Run the script with your context file:
   ```powershell
   dotnet-script context-cleanup.csx "path/to/your/file.context.md"
   ```

2. For each entry, you'll see:
   - A preview of the question and answer
   - Current file statistics
   - Option to keep or delete the entry

3. Navigation options:
   - Press Enter: Keep the entry and move to next
   - Type 'Y': Delete the entry
   - Type 'N': Keep the entry

4. When finished, either:
   - Enter a new filename to save
   - Press Enter to overwrite the original file

## File Format

The script processes files containing entries in this format:
```
<<ENTRY>>
<<QUESTION:[guid]>>
[question content]
<</QUESTION>>
<<ANSWER:[guid]>>
[answer content]
<</ANSWER>>
<</ENTRY>>
```

## Safety Features

- **Backup Creation**: Creates a `.backup` file before making changes
- **Size Warnings**: Alerts if the new file is significantly smaller than the original
- **Format Validation**: Ensures proper file structure and extension
- **Content Preservation**: Maintains exact formatting of kept entries

## Requirements

- .NET Core SDK
- dotnet-script tool installed
- Write permissions in the target directory

## Tips

1. Review your context regularly to maintain optimal size
2. Keep entries that provide important context for ongoing conversations
3. Remove redundant or outdated entries
4. Consider saving different versions for different conversation threads

## Note

This script helps manage context file size while preserving important conversation history. Smaller context files can lead to more efficient LLM interactions and better resource utilization in LLMinster.
