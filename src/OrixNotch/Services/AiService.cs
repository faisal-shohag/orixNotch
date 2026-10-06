using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace OrixNotch.Services;

/// <summary>
/// Ask AI backend: Claude Messages API via the official Anthropic SDK, or the Gemini
/// API via plain HTTPS (SSE), both streamed. API keys are encrypted with Windows DPAPI
/// (current user) — never stored in plain text.
/// </summary>
public static class AiService
{
    public const string ProviderAnthropic = "anthropic";
    public const string ProviderGemini = "gemini";

    public static readonly string[] ClaudeModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5"];
    public static readonly string[] ClaudeLabels = ["Claude Opus 5.5", "Claude Sonnet 5.5", "Claude Haiku 4.5"];
    public static readonly string[] GeminiModels = ["gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash"];
    public static readonly string[] GeminiLabels = ["Gemini 2.5 Flash", "Gemini 2.5 Pro", "Gemini 2.0 Flash"];

    private const string KeyFile = "ai-key.bin";
    private const string GeminiKeyFile = "ai-gemini-key.bin";
    private const string SystemPrompt =
        "You are a quick assistant living in a small panel at the top of the user's Windows screen. " +
        "Answer concisely: a few sentences or a short list unless the user asks for more. " +
        "Use fenced code blocks for code or commands.";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OrixNotch.AiKey");
    private static readonly HttpClient Http = new();

    public static string ActiveProvider =>
        App.Settings.AiProvider == ProviderGemini ? ProviderGemini : ProviderAnthropic;

    public static bool IsGemini => ActiveProvider == ProviderGemini;

    public static string[] ModelsFor(string provider) =>
        provider == ProviderGemini ? GeminiModels : ClaudeModels;

    public static string[] LabelsFor(string provider) =>
        provider == ProviderGemini ? GeminiLabels : ClaudeLabels;

    public static string DefaultModelFor(string provider) => ModelsFor(provider)[0];

    public static bool HasKey => HasKeyFor(ProviderAnthropic);
    public static bool HasGeminiKey => HasKeyFor(ProviderGemini);

    public static bool HasKeyFor(string provider) =>
        File.Exists(Storage.PathOf(provider == ProviderGemini ? GeminiKeyFile : KeyFile));

    public static void SaveKey(string apiKey) => SaveKeyFor(ProviderAnthropic, apiKey);
    public static void SaveGeminiKey(string apiKey) => SaveKeyFor(ProviderGemini, apiKey);

    public static void SaveKeyFor(string provider, string apiKey)
    {
        var path = Storage.PathOf(provider == ProviderGemini ? GeminiKeyFile : KeyFile);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, data);
    }

    private static string? LoadKey(string file)
    {
        try
        {
            var path = Storage.PathOf(file);
            if (!File.Exists(path)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
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

    private static string TrimOneLine(string value, int max)
    {
        var oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length > max ? oneLine[..max] + "…" : oneLine;
    }
}
