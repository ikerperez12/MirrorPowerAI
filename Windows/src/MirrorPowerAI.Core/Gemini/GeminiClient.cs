using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MirrorPowerAI.Core.Audio;

namespace MirrorPowerAI.Core.Gemini;

/// <summary>
/// Calls Gemini <c>generateContent</c> with bounded typed REST payloads and no automatic retries.
/// </summary>
public sealed class GeminiClient
{
    private const string AnswerSystemPrompt =
        "Estás ayudando a alguien que está haciendo una demo en directo delante de compañeros. " +
        "Acabas de recibir la transcripción de una pregunta que le han hecho durante la demo. " +
        "Responde de forma breve, concreta y técnica, como si fueran apuntes rápidos para que " +
        "la persona pueda contestar con seguridad. No repitas la pregunta, ve directo a la respuesta.";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly IGeminiApiKeyProvider _apiKeyProvider;
    private readonly GeminiClientOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiClient"/> class.
    /// </summary>
    /// <param name="httpClient">A reusable HTTP client owned by the application.</param>
    /// <param name="apiKeyProvider">The protected API key provider.</param>
    /// <param name="options">Bounded request options.</param>
    public GeminiClient(
        HttpClient httpClient,
        IGeminiApiKeyProvider apiKeyProvider,
        GeminiClientOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.EnsureValid();
    }

    /// <summary>
    /// Generates the same concise textual answer used by the macOS implementation.
    /// </summary>
    /// <param name="question">The transcribed question.</param>
    /// <param name="context">Optional project context.</param>
    /// <param name="cancellationToken">A token used to cancel key retrieval and the HTTP request.</param>
    /// <returns>The plain-text answer.</returns>
    public Task<string> GenerateAnswerAsync(
        string question,
        string? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        EnsureCharacterLimit(question, _options.MaximumQuestionCharacters, "question");

        var normalizedContext = string.IsNullOrWhiteSpace(context) ? null : context.Trim();
        if (normalizedContext is not null)
        {
            EnsureCharacterLimit(normalizedContext, _options.MaximumContextCharacters, "context");
        }

        var systemPrompt = normalizedContext is null
            ? AnswerSystemPrompt
            : $"{AnswerSystemPrompt}\n\nContexto del sistema que se está enseñando:\n{normalizedContext}";

        var request = new GeminiGenerateContentRequest
        {
            SystemInstruction = new GeminiContent
            {
                Parts = [new GeminiPart { Text = systemPrompt }],
            },
            Contents =
            [
                new GeminiContent
                {
                    Role = "user",
                    Parts = [new GeminiPart { Text = question.Trim() }],
                },
            ],
        };

        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Uploads one normalized WAV recording for transcription. Consent must be enforced by the caller.
    /// </summary>
    /// <param name="audio">The normalized in-memory WAV recording.</param>
    /// <param name="language">A language code or <c>auto</c>.</param>
    /// <param name="cancellationToken">A token used to cancel key retrieval and the HTTP request.</param>
    /// <returns>The plain-text transcript.</returns>
    public Task<string> TranscribeAudioAsync(
        CapturedAudio audio,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        if (language.Length > 35 || language.Any(char.IsControl))
        {
            throw new ArgumentException("El código de idioma no es válido.", nameof(language));
        }

        if (audio.WavData.IsEmpty || audio.WavData.Length > _options.MaximumAudioBytes)
        {
            throw new GeminiApiException(
                GeminiErrorKind.InputTooLarge,
                "El audio está vacío o supera el límite seguro para una petición Gemini inline.");
        }

        var instruction = string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? "Transcribe fielmente este audio. Detecta el idioma y devuelve únicamente la transcripción en texto plano."
            : $"Transcribe fielmente este audio en el idioma {language}. Devuelve únicamente la transcripción en texto plano.";

        var request = new GeminiGenerateContentRequest
        {
            Contents =
            [
                new GeminiContent
                {
                    Role = "user",
                    Parts =
                    [
                        new GeminiPart { Text = instruction },
                        new GeminiPart
                        {
                            InlineData = new GeminiInlineData
                            {
                                MimeType = "audio/wav",
                                Data = Convert.ToBase64String(audio.WavData.Span),
                            },
                        },
                    ],
                },
            ],
        };

        return SendAsync(request, cancellationToken);
    }

    private async Task<string> SendAsync(
        GeminiGenerateContentRequest payload,
        CancellationToken cancellationToken)
    {
        var apiKey = (await _apiKeyProvider.GetApiKeyAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Length > 512 ||
            apiKey.Any(char.IsControl))
        {
            throw new GeminiApiException(
                GeminiErrorKind.MissingApiKey,
                "Configura una API key de Gemini válida.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint());
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = JsonContent.Create(payload, options: SerializerOptions);

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(_options.RequestTimeout);

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpException(response.StatusCode);
            }

            var responseBytes = await ReadLimitedAsync(
                    response.Content,
                    _options.MaximumResponseBytes,
                    requestCancellation.Token)
                .ConfigureAwait(false);

            try
            {
                GeminiGenerateContentResponse? responsePayload;
                try
                {
                    responsePayload = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(
                        responseBytes,
                        SerializerOptions);
                }
                catch (JsonException)
                {
                    throw new GeminiApiException(
                        GeminiErrorKind.InvalidResponse,
                        "Gemini devolvió una respuesta JSON no válida.");
                }

                if (!string.IsNullOrWhiteSpace(responsePayload?.PromptFeedback?.BlockReason) ||
                    responsePayload?.Candidates?.Any(static candidate => IsBlocked(candidate.FinishReason)) == true)
                {
                    throw new GeminiApiException(
                        GeminiErrorKind.Blocked,
                        "Gemini bloqueó la solicitud o la respuesta por sus políticas de seguridad.");
                }

                var text = responsePayload?.Candidates?
                    .SelectMany(static candidate => candidate.Content?.Parts ?? [])
                    .Select(static part => part.Text)
                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?
                    .Trim();

                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new GeminiApiException(
                        GeminiErrorKind.EmptyResponse,
                        "Gemini no devolvió texto utilizable.");
                }

                return text;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseBytes);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GeminiApiException(
                GeminiErrorKind.Timeout,
                "La petición a Gemini agotó el tiempo de espera.");
        }
        catch (HttpRequestException)
        {
            throw new GeminiApiException(
                GeminiErrorKind.ServiceUnavailable,
                "No se pudo conectar de forma segura con Gemini.");
        }
    }

    private Uri BuildEndpoint()
    {
        var baseUri = _options.ApiBaseUri.AbsoluteUri.EndsWith('/')
            ? _options.ApiBaseUri
            : new Uri($"{_options.ApiBaseUri.AbsoluteUri}/");
        return new Uri(baseUri, $"models/{Uri.EscapeDataString(_options.Model)}:generateContent");
    }

    private static GeminiApiException CreateHttpException(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
            GeminiErrorKind.Unauthorized,
            "Gemini rechazó la API key o sus permisos.",
            statusCode),
        HttpStatusCode.TooManyRequests => new(
            GeminiErrorKind.RateLimited,
            "Gemini ha alcanzado el límite de solicitudes. Inténtalo más tarde.",
            statusCode),
        >= HttpStatusCode.InternalServerError => new(
            GeminiErrorKind.ServiceUnavailable,
            "Gemini no está disponible temporalmente.",
            statusCode),
        _ => new(
            GeminiErrorKind.HttpError,
            "Gemini devolvió un error HTTP inesperado.",
            statusCode),
    };

    private static bool IsBlocked(string? finishReason) =>
        finishReason is "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII";

    private static void EnsureCharacterLimit(string value, int limit, string fieldName)
    {
        if (value.Length > limit)
        {
            throw new GeminiApiException(
                GeminiErrorKind.InputTooLarge,
                $"El campo {fieldName} supera el límite configurado.");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new GeminiApiException(
                GeminiErrorKind.InvalidResponse,
                "La respuesta de Gemini supera el límite permitido.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new GeminiApiException(
                        GeminiErrorKind.InvalidResponse,
                        "La respuesta de Gemini supera el límite permitido.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return output.ToArray();
        }
        finally
        {
            if (output.TryGetBuffer(out var outputBuffer))
            {
                CryptographicOperations.ZeroMemory(outputBuffer.AsSpan());
            }

            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed class GeminiGenerateContentRequest
    {
        [JsonPropertyName("system_instruction")]
        public GeminiContent? SystemInstruction { get; init; }

        [JsonPropertyName("contents")]
        public required IReadOnlyList<GeminiContent> Contents { get; init; }
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("parts")]
        public required IReadOnlyList<GeminiPart> Parts { get; init; }
    }

    private sealed class GeminiPart
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("inline_data")]
        public GeminiInlineData? InlineData { get; init; }
    }

    private sealed class GeminiInlineData
    {
        [JsonPropertyName("mime_type")]
        public required string MimeType { get; init; }

        [JsonPropertyName("data")]
        public required string Data { get; init; }
    }

    private sealed class GeminiGenerateContentResponse
    {
        [JsonPropertyName("candidates")]
        public IReadOnlyList<GeminiCandidate>? Candidates { get; init; }

        [JsonPropertyName("promptFeedback")]
        public GeminiPromptFeedback? PromptFeedback { get; init; }
    }

    private sealed class GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiContent? Content { get; init; }

        [JsonPropertyName("finishReason")]
        public string? FinishReason { get; init; }
    }

    private sealed class GeminiPromptFeedback
    {
        [JsonPropertyName("blockReason")]
        public string? BlockReason { get; init; }
    }
}
