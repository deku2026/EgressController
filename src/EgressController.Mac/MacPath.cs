// SPDX-License-Identifier: MIT
namespace EgressController.Mac;

internal static class MacPath
{
    // Platform DTOs retain POSIX semantics even when their pure tests run on another OS.
    internal static bool IsAbsolute([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? path)
        => path is { Length: > 0 } && path[0] == '/' && !path.Contains('\0');
    internal static bool HasTraversal(string path) => path.Split('/').Any(part => part is "." or "..");
}
