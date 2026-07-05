using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AiChat.Control.Providers
{
    /// <summary>
    /// Shared plumbing: one static HttpClient, and retry-with-backoff for transient
    /// failures (429 / 5xx / network) surfaced through <see cref="OnRetry"/> so the UI can
    /// show "retrying…" instead of a raw exception (spec §4).
    /// </summary>
    public abstract class ProviderBase
    {
        protected static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // .NET Framework 4.8 defaults may exclude TLS 1.2 depending on OS policy.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan // per-request cancellation is handled by CancellationToken
            };
            return client;
        }

        /// <summary>Raised as (attempt, delay, reason) when a transient failure triggers a retry.</summary>
        public event Action<int, TimeSpan, string> OnRetry;

        protected int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Sends the request built by <paramref name="requestFactory"/>, retrying on 429/5xx with
        /// exponential backoff (honouring Retry-After when present). The factory is invoked per
        /// attempt because HttpRequestMessage instances are single-use.
        /// </summary>
        protected async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<HttpRequestMessage> requestFactory, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                HttpResponseMessage response = null;
                string transientReason = null;

                try
                {
                    response = await Http
                        .SendAsync(requestFactory(), HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    if (response.IsSuccessStatusCode) return response;

                    int status = (int)response.StatusCode;
                    if (status == 429 || status >= 500)
                    {
                        transientReason = $"{status} {response.ReasonPhrase}";
                    }
                    else
                    {
                        string reason = response.ReasonPhrase;
                        string body = await SafeReadBody(response).ConfigureAwait(false);
                        response.Dispose();
                        throw new AiProviderException($"{status} {reason}: {Truncate(body, 600)}", status);
                    }
                }
                catch (AiProviderException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException ex)
                {
                    transientReason = ex.Message;
                }

                if (attempt >= MaxRetries)
                {
                    string body = response != null ? await SafeReadBody(response).ConfigureAwait(false) : "";
                    response?.Dispose();
                    throw new AiProviderException(
                        $"Request failed after {MaxRetries + 1} attempts ({transientReason}). {Truncate(body, 600)}",
                        429);
                }

                TimeSpan delay = GetRetryDelay(response, attempt);
                response?.Dispose();
                OnRetry?.Invoke(attempt + 1, delay, transientReason);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
        {
            if (response?.Headers?.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
                return delta;
            // 1s, 2s, 4s + small jitter
            double seconds = Math.Pow(2, attempt) + new Random().NextDouble() * 0.25;
            return TimeSpan.FromSeconds(seconds);
        }

        private static async Task<string> SafeReadBody(HttpResponseMessage response)
        {
            try { return await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch { return ""; }
        }

        protected static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }

    public sealed class AiProviderException : Exception
    {
        public int StatusCode { get; }
        public AiProviderException(string message, int statusCode = 0) : base(message) => StatusCode = statusCode;
    }
}
