using System;
using System.Collections.Generic;
using System.Linq;
using AiChat.Control.Models;

namespace AiChat.Control.Services
{
    /// <summary>
    /// Token and cost estimation (spec §4). Uses provider-reported usage when available;
    /// otherwise falls back to the standard chars/4 heuristic. Prices come from the model
    /// catalog so estimates track whichever model is active.
    /// </summary>
    public sealed class CostEstimator
    {
        private readonly object _lock = new object();
        private Dictionary<string, ModelInfo> _catalog = new Dictionary<string, ModelInfo>();

        public void UpdateCatalog(IEnumerable<ModelInfo> models)
        {
            lock (_lock)
            {
                foreach (var m in models.Where(m => m?.Id != null))
                    _catalog[m.Id] = m;
            }
        }

        public static int EstimateTokens(string text)
            => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);

        public decimal EstimateCostUsd(string modelId, int inputTokens, int outputTokens)
        {
            ModelInfo info;
            lock (_lock) _catalog.TryGetValue(modelId ?? "", out info);
            if (info == null) return 0m;
            return inputTokens * info.InputPricePerMTok / 1_000_000m
                 + outputTokens * info.OutputPricePerMTok / 1_000_000m;
        }
    }
}
