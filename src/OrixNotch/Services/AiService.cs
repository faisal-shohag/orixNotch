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

/// <summary>An image attached to a chat turn: <c>image/jpeg</c> or <c>image/png</c>, base64-encoded.</summary>
public sealed record AiImage(string MediaType, string Base64);

/// <summary>One turn of a conversation sent to the model; only user turns carry images.</summary>
public sealed record AiTurn(bool FromUser, string Text, IReadOnlyList<AiImage>? Images = null)
{
    public bool HasImages => Images is { Count: > 0 };
}

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
    /// <summary>Built-in Gemini list until the key's own list loads; the reliable free default comes first.</summary>
    public static readonly string[] GeminiModels =
    [
        "gemini-3.5-flash-lite", "gemini-3.8-flash", "gemini-3.6-flash", "gemini-3.1-flash-lite", "gemini-3-flash-preview",
        "gemini-2.5-flash", "gemini-2.5-flash-lite", "gemma-4-31b-it", "gemini-3.1-pro-preview", "gemini-2.5-pro",
    ];
    public static readonly string[] GeminiLabels =
    [
        "Gemini 3.5 Flash-Lite", "Gemini 3.8 Flash", "Gemini 3.6 Flash", "Gemini 3.1 Flash-Lite", "Gemini 3 Flash",
        "Gemini 2.5 Flash", "Gemini 2.5 Flash-Lite", "Gemma 4 31B", "Gemini 3.1 Pro", "Gemini 2.5 Pro",
    ];

    /// <summary>High daily quota on the free tier, so it rarely runs out mid-chat.</summary>
    public const string GeminiFreeDefault = "gemini-3.5-flash-lite";

    /// <summary>
    /// Gemini API free tier (requests per day), from AI Studio's rate-limit page, October 2026.
    /// First prefix match wins, so the Flash-Lite entries sit before their Flash siblings.
    /// Models not listed (Pro, image, audio) need billing.
    /// </summary>
    private static readonly (string Prefix, string PerDay)[] GeminiFreeTier =
    [
        ("gemini-3.5-flash-lite", "500/day"),
        ("gemini-3.1-flash-lite", "500/day"),
        ("gemini-2.5-flash-lite", "20/day"),
        ("gemini-3.8-flash", "20/day"),
        ("gemini-3.7-flash", "20/day"),
        ("gemini-3.6-flash", "20/day"),
        ("gemini-3.5-flash", "20/day"),
        ("gemini-3-flash", "20/day"),
        ("gemini-2.5-flash", "20/day"),
        ("gemma-4-", "14.4K/day"),
    ];

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
            GeminiModels, GeminiLabels, "Free models work with a free AI Studio key; Pro needs billing."),
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

    // Live model lists fetched from the provider (OpenAI / DeepSeek / Gemini), so new models show up without an app update.
    private static readonly Dictionary<string, (string[] Ids, string[] Labels)> LiveModels = new();
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
        LiveModels.TryGetValue(provider, out var live) && live.Ids.Length > 0 ? live.Ids : Info(provider).DefaultModels;

    public static string[] LabelsFor(string provider) =>
        LiveModels.TryGetValue(provider, out var live) && live.Ids.Length > 0 ? live.Labels : Info(provider).DefaultLabels;

    /// <summary>Display name for a model id of a provider (falls back to the id).</summary>
    public static string LabelFor(string provider, string model)
    {
        var index = Array.IndexOf(ModelsFor(provider), model);
        if (index >= 0) return LabelsFor(provider)[index];
        index = Array.IndexOf(Info(provider).DefaultModels, model);
        return index >= 0 ? Info(provider).DefaultLabels[index] : model;
    }

    /// <summary>Free-tier daily quota ("500/day") when the model is free with a Gemini key; null otherwise.</summary>
    public static string? FreeTier(string provider, string model) =>
        provider == ProviderGemini
            ? GeminiFreeTier.FirstOrDefault(f => model.StartsWith(f.Prefix, StringComparison.Ordinal)).PerDay
            : null;

    /// <summary>
    /// Fetches the chat models this key can use (OpenAI / DeepSeek <c>GET /models</c>, Gemini
    /// <c>GET /v1beta/models</c>). Returns false (keeping the built-in list) when there is no key,
    /// no network, or the provider has no such endpoint.
    /// </summary>
    public static async Task<bool> RefreshModelsAsync(string provider)
    {
        if (provider == ProviderGemini) return await RefreshGeminiModelsAsync();
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
            LiveModels[provider] = (ids, ids);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gemini's model list, free models first (the reliable default on top), then the rest newest first.
    /// Labels are Google's display names.
    /// </summary>
    private static async Task<bool> RefreshGeminiModelsAsync()
    {
        if (LoadKey(GeminiKeyFile) is not { } key) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000");
            request.Headers.Add("x-goog-api-key", key);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var models = new List<(string Id, string Label)>();
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                var id = (m.GetProperty("name").GetString() ?? "").Replace("models/", "");
                var chats = m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                            methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                if (!chats || !IsChatModel(ProviderGemini, id)) continue;
                var label = m.TryGetProperty("displayName", out var d) && d.GetString() is { Length: > 0 } name ? name : id;
                models.Add((id, label));
            }
            if (models.Count == 0) return false;
            var ordered = models
                .OrderByDescending(m => m.Id == GeminiFreeDefault)
                .ThenByDescending(m => FreeTier(ProviderGemini, m.Id) is not null)
                .ThenByDescending(m => m.Id, StringComparer.Ordinal)
                .ToArray();
            LiveModels[ProviderGemini] = (ordered.Select(m => m.Id).ToArray(), ordered.Select(m => m.Label).ToArray());
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>OpenAI and Gemini list every model they have (images, audio, embeddings…); keep the ones that chat.</summary>
    internal static bool IsChatModel(string provider, string id)
    {
        if (provider == ProviderGemini)
        {
            if (!id.StartsWith("gemini-", StringComparison.Ordinal) && !id.StartsWith("gemma-", StringComparison.Ordinal)) return false;
            string[] skip = ["tts", "image", "embedding", "live", "audio", "transcribe", "translate", "robotics", "computer-use", "aqa", "nano-banana", "omni"];
            return !skip.Any(x => id.Contains(x, StringComparison.Ordinal));
        }
        if (provider != ProviderOpenAi) return id.Length > 0;
        if (!(id.StartsWith("gpt-", StringComparison.Ordinal) || (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1])))) return false;
        string[] excluded = ["audio", "realtime", "transcribe", "tts", "image", "search", "instruct", "embedding", "codex", "moderation"];
        return !excluded.Any(x => id.Contains(x, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether the model reads images. Claude and Gemini (including Gemma 4) all do; OpenAI's GPT-4o,
    /// GPT-4.1, GPT-5 and o-series reasoning models do (not o1-mini / o3-mini); DeepSeek's API is text only.
    /// </summary>
    public static bool SupportsImages(string provider, string model) => provider switch
    {
        ProviderAnthropic => true,
        ProviderGemini => !model.StartsWith("gemma-2", StringComparison.Ordinal),
        ProviderOpenAi => (model.StartsWith("gpt-4o", StringComparison.Ordinal) || model.StartsWith("gpt-4.1", StringComparison.Ordinal) ||
                           model.StartsWith("gpt-5", StringComparison.Ordinal) || model.StartsWith("chatgpt-", StringComparison.Ordinal) ||
                           (model.Length > 1 && model[0] == 'o' && char.IsDigit(model[1])))
                          && model is not ("o1-mini" or "o3-mini") && !model.StartsWith("o1-mini", StringComparison.Ordinal) && !model.StartsWith("o3-mini", StringComparison.Ordinal),
        _ => false,
    };

    public static string DefaultModelFor(string provider)
    {
        var models = ModelsFor(provider);
        if (provider == ProviderGemini && models.Contains(GeminiFreeDefault)) return GeminiFreeDefault;
        return models[0];
    }

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
        IReadOnlyList<AiTurn> conversation,
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
            .Select(m => new BetaMessageParam { Role = m.FromUser ? Role.User : Role.Assistant, Content = ClaudeContent(m) })
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

    /// <summary>Plain text, or image blocks followed by the text when the turn has images.</summary>
    private static BetaMessageParamContent ClaudeContent(AiTurn turn)
    {
        if (!turn.HasImages) return turn.Text;
        var blocks = turn.Images!
            .Select(i => (BetaContentBlockParam)new BetaImageBlockParam(new BetaBase64ImageSource
            {
                Data = i.Base64,
                MediaType = i.MediaType == "image/png" ? MediaType.ImagePng : MediaType.ImageJpeg,
            }))
            .ToList();
        if (turn.Text.Length > 0) blocks.Add(new BetaTextBlockParam(turn.Text));
        return blocks;
    }

    /// <summary>Gemini parts: inline images, then the text.</summary>
    private static object[] GeminiParts(AiTurn turn)
    {
        var parts = new List<object>();
        if (turn.HasImages)
            parts.AddRange(turn.Images!.Select(i => (object)new { inline_data = new { mime_type = i.MediaType, data = i.Base64 } }));
        if (turn.Text.Length > 0 || parts.Count == 0) parts.Add(new { text = turn.Text });
        return parts.ToArray();
    }

    /// <summary>Chat Completions content: a plain string, or text + data-URL image parts when there are images.</summary>
    private static object CompatibleContent(AiTurn turn)
    {
        if (!turn.HasImages) return turn.Text;
        var parts = new List<object> { new { type = "text", text = turn.Text } };
        parts.AddRange(turn.Images!.Select(i => (object)new { type = "image_url", image_url = new { url = $"data:{i.MediaType};base64,{i.Base64}" } }));
        return parts.ToArray();
    }

    /// <summary>Streams a Gemini reply via the generateContent SSE endpoint (no SDK needed).</summary>
    private static async IAsyncEnumerable<string> StreamGeminiAsync(
        IReadOnlyList<AiTurn> conversation,
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
                parts = GeminiParts(m),
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
        IReadOnlyList<AiTurn> conversation,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = LoadKey(provider.KeyFile) ?? throw new InvalidOperationException($"Add your {provider.Company} API key in Settings → Ask AI.");
        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        messages.AddRange(conversation.Select(m => (object)new { role = m.FromUser ? "user" : "assistant", content = CompatibleContent(m) }));
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
