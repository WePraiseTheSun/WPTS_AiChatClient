using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiChat.Control.Models;

namespace AiChat.Control.Providers
{
    /// <summary>
    /// Single abstraction implemented per provider (spec §3.6). Each provider's SSE shape
    /// is normalized into <see cref="ChatDelta"/> before it ever reaches JS.
    /// </summary>
    public interface IAiProvider
    {
        string Name { get; }

        /// <summary>True when the provider needs an API key before it can be used.</summary>
        bool RequiresApiKey { get; }

        Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken ct = default);

        IAsyncEnumerable<ChatDelta> StreamChatAsync(ChatRequest request, CancellationToken ct);
    }
}
