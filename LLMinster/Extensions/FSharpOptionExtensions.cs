using Microsoft.FSharp.Core;

namespace LLMinster.Extensions;

public static class FSharpOptionExtensions
{
    public static bool IsSome<T>(this FSharpOption<T> option)
    {
        try
        {
            var value = option.Value;
            return !Equals(value, default(T));
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }

    public static bool IsNone<T>(this FSharpOption<T> option)
    {
        return !option.IsSome();
    }
}