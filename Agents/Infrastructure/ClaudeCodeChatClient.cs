using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace CobolToQuarkusMigration.Agents.Infrastructure;

/// <summary>
/// IChatClient adapter that wraps the Claude Code CLI (claude -p).
/// Uses the user's existing Claude Code subscription — no API key required.
///
/// This follows the same pattern as CopilotChatClient: shell out to the CLI,
/// pass the prompt via stdin, and parse the JSON response.
///
/// Required: 'claude' CLI in PATH with an active subscription (Max/Pro plan).
/// </summary>
public sealed class ClaudeCodeChatClient : IChatClient
{
    private readonly string _model;
    private readonly ILogger? _logger;
    private readonly string _claudePath;

    /// <summary>
    /// Creates a new ClaudeCodeChatClient.
    /// </summary>
    /// <param name="model">Model alias or full name (e.g. "sonnet", "opus", "claude-sonnet-4-20250514").</param>
    /// <param name="claudePath">Optional path to the claude CLI binary. Defaults to "claude" (found via PATH).</param>
    /// <param name="logger">Optional logger.</param>
    public ClaudeCodeChatClient(string model, string? claudePath = null, ILogger? logger = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _claudePath = claudePath ?? "claude";
        _logger = logger;
    }

    /// <inheritdoc />
    public ChatClientMetadata Metadata => new(nameof(ClaudeCodeChatClient), null, _model);

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var model = options?.ModelId ?? _model;

        // Extract system message and build user prompt from the conversation
        string? systemMessage = null;
        var userPromptBuilder = new StringBuilder();

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
            {
                systemMessage = msg.Text;
            }
            else
            {
                if (userPromptBuilder.Length > 0) userPromptBuilder.AppendLine();
                userPromptBuilder.Append(msg.Text);
            }
        }

        var userPrompt = userPromptBuilder.ToString();
        _logger?.LogDebug("ClaudeCodeChatClient: sending {PromptLength} chars to model {Model}", userPrompt.Length, model);

        // Build command arguments
        var args = new List<string>
        {
            "-p",                          // Print mode (non-interactive)
            "--output-format", "json",     // Structured JSON response
            "--model", model,              // Model selection
            "--max-turns", "1",            // Single-turn completion (no tool use loops)
            "--no-session-persistence",    // Don't save session to disk
        };

        if (!string.IsNullOrEmpty(systemMessage))
        {
            args.Add("--system-prompt");
            args.Add(systemMessage);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _claudePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            // Write prompt to stdin and close it
            await process.StandardInput.WriteAsync(userPrompt);
            process.StandardInput.Close();

            // Read stdout and stderr concurrently
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger?.LogError("ClaudeCodeChatClient: claude CLI exited with code {ExitCode}. stderr: {Stderr}",
                    process.ExitCode, stderr);
                throw new InvalidOperationException(
                    $"Claude Code CLI failed (exit code {process.ExitCode}): {stderr}");
            }

            // Parse JSON response: {"type":"result","result":"<response text>","is_error":false,...}
            var responseText = ParseJsonResponse(stdout);

            _logger?.LogDebug("ClaudeCodeChatClient: received {Length} chars from model {Model}",
                responseText.Length, model);

            var responseMessage = new AIChatMessage(ChatRole.Assistant, responseText);
            return new ChatResponse(responseMessage);
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            _logger?.LogError(ex, "ClaudeCodeChatClient: failed to execute claude CLI");
            throw new InvalidOperationException(
                $"Failed to execute Claude Code CLI at '{_claudePath}'. " +
                "Ensure the 'claude' CLI is installed and in PATH. " +
                "Install: https://docs.anthropic.com/en/docs/claude-code", ex);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Streaming via stream-json is possible but adds complexity.
        // For now, fall back to non-streaming and yield the full result.
        var response = await GetResponseAsync(messages, options, cancellationToken);

        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate
            {
                Role = message.Role,
                Contents = message.Contents,
            };
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
        // No resources to dispose — each request spawns a fresh process.
    }

    /// <summary>
    /// Parses the JSON response from claude CLI's --output-format json mode.
    /// Expected shape: {"type":"result","subtype":"success","is_error":false,"result":"..."}
    /// </summary>
    private string ParseJsonResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check for errors
            if (root.TryGetProperty("is_error", out var isError) && isError.GetBoolean())
            {
                var errorResult = root.TryGetProperty("result", out var errMsg)
                    ? errMsg.GetString() ?? "Unknown error"
                    : "Unknown error";
                throw new InvalidOperationException($"Claude Code returned an error: {errorResult}");
            }

            // Extract the result text
            if (root.TryGetProperty("result", out var result))
            {
                return result.GetString() ?? string.Empty;
            }

            _logger?.LogWarning("ClaudeCodeChatClient: JSON response missing 'result' field. Raw: {Json}",
                json.Length > 500 ? json[..500] + "..." : json);
            return json;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "ClaudeCodeChatClient: failed to parse JSON response, returning raw text");
            return json;
        }
    }
}
