using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace OrixNotch.Services;

/// <summary>Everything the UI needs to know about one AI provider.</summary>
public sealed record AiProviderInfo(
    string Id, string Name, string Company, string KeyFile, string KeyPlaceholder, string KeyLabel, string KeySource,
    string[] DefaultModels, string[] DefaultLabels, string ModelHint);

/// <summary>
/// Ask AI backend: Claude Messages API via the official Anthropic SDK, the Gemini API, or any
/// OpenAI-compatible Chat Completions API (OpenAI / ChatGPT, DeepSeek) via plain HTTPS (SSE), all
/// streamed. API keys are encrypted with Windows DPAPI (current user) — never stored in plain text.
/// </summary>
public static class AiService
{
    public const string ProviderAnthropic = "anthropic";
    public const string ProviderGemini = "gemini";
    public const string ProviderOpenAi = "openai";
    public const string ProviderDeepSeek = "deepseek";

    public static readonly string[] ClaudeModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5"];
    public static readonly string[] ClaudeLabels = ["Claude Opus 5.5", "Claude Sonnet 5.5", "Claude Haiku 4.5"];
    public static readonly string[] GeminiModels = ["gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash"];
    public static readonly string[] GeminiLabels = ["Gemini 2.5 Flash", "Gemini 2.5 Pro", "Gemini 2.0 Flash"];

    private const string KeyFile = "ai-key.bin";
    private const string GeminiKeyFile = "ai-gemini-key.bin";

    public static readonly AiProviderInfo[] Providers =
    [
        new(ProviderAnthropic, "Claude", "Anthropic", KeyFile, "sk-ant-…", "Anthropic API key (sk-ant-…)", "console.anthropic.com → API keys",
            ClaudeModels, ClaudeLabels, "Opus is the most capable; Sonnet and Haiku are faster and cheaper."),
        new(ProviderOpenAi, "ChatGPT", "OpenAI", "ai-openai-key.bin", "sk-…", "OpenAI API key (sk-…)", "platform.openai.com → API keys",
            ["gpt-5.5", "gpt-5.4-mini", "gpt-4.1"], ["GPT-5.5", "GPT-5.4 mini", "GPT-4.1"],
            "Models your key can use are listed from OpenAI. Mini models are faster and cheaper."),
        new(ProviderGemini, "Gemini", "Google", GeminiKeyFile, "AIza…", "Google AI Studio API key (AIza…)", "aistudio.google.com → Get API key",
            GeminiModels, GeminiLabels, "Flash is fast and cheap; Pro is the most capable."),
        new(ProviderDeepSeek, "DeepSeek", "DeepSeek", "ai-deepseek-key.bin", "sk-…", "DeepSeek API key (sk-…)", "platform.deepseek.com → API keys",
            ["deepseek-v4-pro", "deepseek-flash"], ["DeepSeek V4 Pro", "DeepSeek Flash"],
            "Models your key can use are listed from DeepSeek. Flash is faster and cheaper."),
    ];

    /// <summary>
    /// OpenAI-compatible Chat Completions endpoints. ORIXNOTCH_OPENAI_BASE / ORIXNOTCH_DEEPSEEK_BASE
    /// override them (end-to-end tests point these at a local mock server).
    /// </summary>
    private static string? CompatibleBase(string provider) => provider switch
    {
        ProviderOpenAi => Environment.GetEnvironmentVariable("ORIXNOTCH_OPENAI_BASE") is { Length: > 0 } o ? o : "https://api.openai.com/v1",
        ProviderDeepSeek => Environment.GetEnvironmentVariable("ORIXNOTCH_DEEPSEEK_BASE") is { Length: > 0 } d ? d : "https://api.deepseek.com",
        _ => null,
    };

    /// <summary>Saved key for a provider (decrypted), or null.</summary>
    internal static string? LoadKeyFor(string provider) => LoadKey(Info(provider).KeyFile);

    /// <summary>API base URL for OpenAI-compatible providers (honours the test overrides).</summary>
    internal static string? BaseUrlFor(string provider) => CompatibleBase(provider);

    public static AiProviderInfo Info(string provider) => Providers.FirstOrDefault(p => p.Id == provider) ?? Providers[0];
    public static AiProviderInfo Active => Info(ActiveProvider);

    // Live model lists fetched from the provider (OpenAI / DeepSeek), so new models show up without an app update.
    private static readonly Dictionary<string, string[]> LiveModels = new();
    private const string BasePrompt =
        "You are a quick assistant living in a small panel at the top of the user's Windows screen. " +
        "Answer concisely: a few sentences or a short list unless the user asks for more. " +
        "Use fenced code blocks for code or commands.";

    /// <summary>Base instructions plus what the model can't know on its own: who is asking, and when.</summary>
    private static string SystemPrompt
    {
        get
        {
            var now = DateTime.Now;
            var name = App.Settings.DisplayName;
            if (string.IsNullOrWhiteSpace(name)) name = Environment.UserName;
            return BasePrompt +
                   $"\n\nContext: today is {now:dddd, d MMMM yyyy} and the local time is {now:HH:mm} " +
                   $"({TimeZoneInfo.Local.DisplayName}). The user's name is {name}.";
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OrixNotch.AiKey");
    private static readonly HttpClient Http = new();

    public static string ActiveProvider =>
        Providers.Any(p => p.Id == App.Settings.AiProvider) ? App.Settings.AiProvider : ProviderAnthropic;

    public static bool IsGemini => ActiveProvider == ProviderGemini;

    public static string[] ModelsFor(string provider) =>
        LiveModels.TryGetValue(provider, out var live) && live.Length > 0 ? live : Info(provider).DefaultModels;

    public static string[] LabelsFor(string provider) =>
        LiveModels.TryGetValue(provider, out var live) && live.Length > 0 ? live : Info(provider).DefaultLabels;

    /// <summary>
    /// Fetches the chat models this key can use (OpenAI / DeepSeek <c>GET /models</c>). Returns false
    /// (keeping the built-in list) when there is no key, no network, or the provider has no such endpoint.
    /// </summary>
    public static async Task<bool> RefreshModelsAsync(string provider)
    {
        if (CompatibleBase(provider) is not { } baseUrl || LoadKey(Info(provider).KeyFile) is not { } key) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var ids = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => IsChatModel(provider, id))
                .OrderByDescending(id => id, StringComparer.Ordinal)
                .ToArray();
            if (ids.Length == 0) return false;
            LiveModels[provider] = ids;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>OpenAI lists every model it has (images, audio, embeddings…); keep the ones that chat.</summary>
    internal static bool IsChatModel(string provider, string id)
    {
        if (provider != ProviderOpenAi) return id.Length > 0;
        if (!(id.StartsWith("gpt-", StringComparison.Ordinal) || (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1])))) return false;
        string[] excluded = ["audio", "realtime", "transcribe", "tts", "image", "search", "instruct", "embedding", "codex", "moderation"];
        return !excluded.Any(x => id.Contains(x, StringComparison.Ordinal));
    }

    public static string DefaultModelFor(string provider) => ModelsFor(provider)[0];

    public static bool HasKey => HasKeyFor(ProviderAnthropic);
    public static bool HasGeminiKey => HasKeyFor(ProviderGemini);

    public static bool HasKeyFor(string provider) => File.Exists(Storage.PathOf(Info(provider).KeyFile));

    public static void SaveKey(string apiKey) => SaveKeyFor(ProviderAnthropic, apiKey);
    public static void SaveGeminiKey(string apiKey) => SaveKeyFor(ProviderGemini, apiKey);

    public static void SaveKeyFor(string provider, string apiKey)
    {
        var path = Storage.PathOf(Info(provider).KeyFile);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var key = CleanKey(apiKey);
        // The key travels in an HTTP header, which only allows ASCII.
        if (key.Length == 0 || key.Any(c => c < 0x21 || c > 0x7E))
            throw new ArgumentException("Invalid characters in key. Copy it again.");
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Drops whitespace and invisible characters that copy-paste from web pages and chats tends
    /// to carry along (zero-width spaces, no-break spaces, BOMs, line breaks).
    /// </summary>
    private static string CleanKey(string key) =>
        new(key.Where(c => !char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());

    private static string? LoadKey(string file)
    {
        try
        {
            var path = Storage.PathOf(file);
            if (!File.Exists(path)) return null;
            // Cleaned on load too, so keys saved before validation existed still work.
            return CleanKey(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Streams the assistant's reply text for the conversation so far, via the active provider.</summary>
    public static async IAsyncEnumerable<string> StreamReplyAsync(
        IReadOnlyList<(bool FromUser, string Text)> conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (IsGemini)
        {
            await foreach (var chunk in StreamGeminiAsync(conversation, App.Settings.AiModel, cancellationToken))
                yield return chunk;
            yield break;
        }
        if (CompatibleBase(ActiveProvider) is { } baseUrl)
        {
            await foreach (var chunk in StreamCompatibleAsync(Active, baseUrl, conversation, App.Settings.AiModel, cancellationToken))
                yield return chunk;
            yield break;
        }

        var key = LoadKey(KeyFile) ?? throw new InvalidOperationException("Add your Anthropic API key in Settings → Ask AI.");
        var client = new AnthropicClient { ApiKey = key };

        var messages = conversation
            .Select(m => new BetaMessageParam { Role = m.FromUser ? Role.User : Role.Assistant, Content = m.Text })
            .ToList();

        var parameters = new MessageCreateParams
        {
            Model = App.Settings.AiModel,
            MaxTokens = 16000,
            System = SystemPrompt,
            Messages = messages,
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            // If a safety classifier declines, the request is re-served by a fallback model in the same call.
            Betas = ["server-side-fallback-2026-06-01"],
            Fallbacks = new List<BetaFallbackParam> { new() { Model = "claude-opus-4-8" } },
        };

        await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, cancellationToken))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
            else if (ev.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason?.ToString() == "refusal")
                yield return "\n\n_(Claude declined to answer this request.)_";
        }
    }

    /// <summary>Streams a Gemini reply via the generateContent SSE endpoint (no SDK needed).</summary>
    private static async IAsyncEnumerable<string> StreamGeminiAsync(
        IReadOnlyList<(bool FromUser, string Text)> conversation,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = LoadKey(GeminiKeyFile) ?? throw new InvalidOperationException("Add your Gemini API key in Settings → Ask AI.");
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = conversation.Select(m => new
            {
                role = m.FromUser ? "user" : "model",
                parts = new[] { new { text = m.Text } },
            }).ToArray(),
            generationConfig = new { maxOutputTokens = 4096 },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse");
        request.Headers.Add("x-goog-api-key", key);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Gemini request failed ({(int)response.StatusCode}): {TrimOneLine(error, 300)}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var declined = false;
        while (await reader.ReadLineAsync().WaitAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line["data:".Length..].Trim();
            if (json.Length == 0 || json == "[DONE]") continue;

            string? text = null;
            var blocked = false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("candidates", out var candidates) &&
                    candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0 &&
                    candidates[0].TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetProperty("text", out var t))
                            text += t.GetString();
                }
                else if (!declined && root.TryGetProperty("promptFeedback", out var feedback) &&
                         feedback.TryGetProperty("blockReason", out _))
                {
                    blocked = true;
                }
            }
            catch (JsonException)
            {
                continue; // partial chunk; the next line completes it
            }

            if (!string.IsNullOrEmpty(text)) yield return text;
            else if (blocked)
            {
                declined = true;
                yield return "\n\n_(Gemini declined to answer this request.)_";
            }
        }
    }

    /// <summary>
    /// Streams a reply from an OpenAI-compatible Chat Completions endpoint (OpenAI, DeepSeek):
    /// <c>POST /chat/completions</c> with <c>stream: true</c>, reading <c>choices[0].delta.content</c>
    /// from the SSE lines. Reasoning text (DeepSeek's <c>reasoning_content</c>) is not shown.
    /// </summary>
    private static async IAsyncEnumerable<string> StreamCompatibleAsync(
        AiProviderInfo provider,
        string baseUrl,
        IReadOnlyList<(bool FromUser, string Text)> conversation,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = LoadKey(provider.KeyFile) ?? throw new InvalidOperationException($"Add your {provider.Company} API key in Settings → Ask AI.");
        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        messages.AddRange(conversation.Select(m => (object)new { role = m.FromUser ? "user" : "assistant", content = m.Text }));
        var body = new { model, messages, stream = true };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"{provider.Company} request failed ({(int)response.StatusCode}): {TrimOneLine(error, 300)}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync().WaitAsync(cancellationToken) is { } line)
        {
            if (ParseCompatibleChunk(line) is not { } chunk) continue;
            if (chunk.Text is { Length: > 0 } text) yield return text;
            else if (chunk.Refused) yield return $"\n\n_({provider.Name} declined to answer this request.)_";
        }
    }

    /// <summary>One SSE line of a Chat Completions stream → its text delta (or a refusal); null for anything else.</summary>
    internal static (string? Text, bool Refused)? ParseCompatibleChunk(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var json = line["data:".Length..].Trim();
        if (json.Length == 0 || json == "[DONE]") return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("delta", out var delta)) return null;
            var text = delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var refused = delta.TryGetProperty("refusal", out var r) && r.ValueKind == JsonValueKind.String;
            return (text, refused);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TrimOneLine(string value, int max)
    {
        var oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length > max ? oneLine[..max] + "…" : oneLine;
    }
}
