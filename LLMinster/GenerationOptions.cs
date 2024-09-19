namespace LLMinster;

public class GenerationOptions
{
    /// <summary>
    ///     Controls the randomness of the output. Lower values make the output more deterministic.
    /// </summary>
    public float Temperature { get; set; } = 0.025f;

    /// <summary>
    ///     The maximum number of tokens to generate.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    // Add more generic generation parameters as needed
}