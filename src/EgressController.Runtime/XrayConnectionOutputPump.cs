// SPDX-License-Identifier: MIT
namespace EgressController.Runtime;

/// <summary>Always drains inherited child output; optional parsing cannot backpressure on the UI or throw into lifecycle code.</summary>
public static class XrayConnectionOutputPump
{
    public static async Task DrainAsync(TextReader reader, XrayConnectionObserver? observer = null)
    {
        try
        {
            var buffer = new char[2048];
            var line = observer is null ? [] : new char[XrayConnectionObserver.MaximumLineCharacters];
            int length = 0; bool oversized = false;
            while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) is var count && count > 0)
            {
                if (observer is null) continue;
                for (int index = 0; index < count; index++)
                {
                    char character = buffer[index];
                    if (character == '\n')
                    {
                        if (oversized) observer.RecordDroppedLine();
                        else ObserveSafely(observer, line, length);
                        length = 0; oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (length == line.Length) oversized = true;
                        else line[length++] = character;
                    }
                }
            }
            if (observer is not null)
            {
                if (oversized) observer.RecordDroppedLine();
                else if (length > 0) ObserveSafely(observer, line, length);
            }
        }
        catch (Exception) { } // Source failure affects only observation availability, never the core or guardian.
        finally { observer?.MarkUnavailable(); }
    }
    private static void ObserveSafely(XrayConnectionObserver observer, char[] buffer, int length)
    {
        if (length > 0 && buffer[length - 1] == '\r') length--;
        try { observer.ObserveLine(new string(buffer, 0, length), DateTimeOffset.UtcNow); }
        catch (Exception) { observer.RecordDroppedLine(); }
    }
}
