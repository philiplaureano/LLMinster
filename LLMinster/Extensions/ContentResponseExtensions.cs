using Microsoft.FSharp.Core;

namespace LLMinster.Extensions;

public static class ContentResponseExtensions
{
    public static bool HasResponse(this FSharpResult<fsEnsemble.ContentResponse, string> result)
    {
        return result is { IsError: false, ResultValue: not null } && result.ResultValue.Response.IsSome();
    }
}