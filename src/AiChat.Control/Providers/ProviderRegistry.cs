using System;
using System.Collections.Generic;
using AiChat.Control.Security;

namespace AiChat.Control.Providers
{
    /// <summary>
    /// Central registry. Each provider receives a *lazy* key accessor into the DPAPI-backed
    /// SecretStore, so keys are only decrypted at the moment a request is sent and never
    /// cached in provider instances.
    /// </summary>
    public sealed class ProviderRegistry
    {
        private readonly Dictionary<string, IAiProvider> _providers =
            new Dictionary<string, IAiProvider>(StringComparer.OrdinalIgnoreCase);

        public ProviderRegistry(SecretStore secrets)
        {
            Register(new AnthropicProvider(() => secrets.GetApiKey("anthropic")));
            Register(new OpenAiProvider(() => secrets.GetApiKey("openai")));
            Register(new DeepSeekProvider(() => secrets.GetApiKey("deepseek")));
            Register(new GeminiProvider(() => secrets.GetApiKey("gemini")));
            Register(new OllamaProvider());
        }

        public void Register(IAiProvider provider) => _providers[provider.Name] = provider;

        public IAiProvider Get(string name)
        {
            if (name != null && _providers.TryGetValue(name, out var p)) return p;
            throw new AiProviderException($"Unknown provider '{name}'.");
        }

        public bool TryGet(string name, out IAiProvider provider)
            => _providers.TryGetValue(name ?? "", out provider);

        public IEnumerable<IAiProvider> All => _providers.Values;
    }
}
