using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiChat.Control.Providers
{
    public sealed class SseEvent
    {
        public string EventName { get; set; }
        public string Data { get; set; }
    }

    /// <summary>
    /// Minimal Server-Sent-Events reader over an HttpResponseMessage. Providers consume
    /// this and translate raw events into normalized ChatDelta values.
    /// </summary>
    internal static class SseReader
    {
        public static async IAsyncEnumerable<SseEvent> ReadAsync(
            HttpResponseMessage response,
            [EnumeratorCancellation] CancellationToken ct)
        {
            using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string eventName = null;
                var data = new StringBuilder();

                // Never touch reader.EndOfStream here: on a live network stream it
                // blocks synchronously. ReadLineAsync returning null signals EOF.
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;

                    if (line.Length == 0)
                    {
                        if (data.Length > 0)
                        {
                            yield return new SseEvent { EventName = eventName, Data = data.ToString() };
                        }
                        eventName = null;
                        data.Clear();
                        continue;
                    }

                    if (line.StartsWith(":", StringComparison.Ordinal)) continue; // comment / keep-alive

                    if (line.StartsWith("event:", StringComparison.Ordinal))
                    {
                        eventName = line.Substring(6).Trim();
                    }
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(line.Substring(5).TrimStart());
                    }
                }

                if (data.Length > 0)
                {
                    yield return new SseEvent { EventName = eventName, Data = data.ToString() };
                }
            }
        }
    }
}
