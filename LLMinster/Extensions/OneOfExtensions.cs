using OneOf;
using OneOf.Types;

namespace LLMinster.Extensions;

public static class OneOfExtensions
{
    public static bool IsSome<TResult>(this OneOf<TResult, None, LLMinster.Interfaces.Error> value)
    {
        return !value.IsNone();
    }
    
    public static bool IsNone<TResult>(this OneOf<TResult, None, LLMinster.Interfaces.Error> value)
    {
        return value.IsT1;
    }

    public static bool IsError<TResult>(this OneOf<TResult, None, LLMinster.Interfaces.Error> value)
    {
        return value.IsT2;
    }
}