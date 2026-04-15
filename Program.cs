using CobolToQuarkusMigration.Helpers;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.Persistence;
using CobolToQuarkusMigration.Processes;
using CobolToQuarkusMigration.Agents;
using CobolToQuarkusMigration.Agents.Infrastructure;
using CobolToQuarkusMigration.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging.Console;

namespace CobolToQuarkusMigration;

/// <summary>
/// Main entry point for the COBOL to Java/C# migration tool.
/// Uses dual-API architecture:
/// - ResponsesApiClient for code agents (gpt-5.1-codex-mini via Responses API)
/// - IChatClient for chat/reports (gpt-5.1-chat via Chat Completions API)
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Start live log capture for portal's Live Run Log panel
        var logsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
        Directory.CreateDirectory(logsDirectory);
        
        // Only enable live logging for migration runs (not MCP server, conversation, or utility modes)
        var isMigrationRun = !args.Contains("mcp") && !args.Contains("conversation") && !args.Contains("list-models");
        LiveLogWriter? liveLogWriter = null;
        
        if (isMigrationRun)
        {
            liveLogWriter = LiveLogWriter.Start(logsDirectory);
        }
        
        try
        {
            // Configure logger to write to Stderr to avoid breaking MCP JSON-RPC on Stdout
            using var loggerFactory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole(options => 
                {
                    options.LogToStandardErrorThreshold = LogLevel.Trace;
                });
                // Suppress verbose Copilot SDK internal tracing
                builder.AddFilter("GitHub.Copilot.SDK", LogLevel.Warning);
            });
            var logger = loggerFactory.CreateLogger(nameof(Program));
            var fileHelper = new FileHelper(loggerFactory.CreateLogger<FileHelper>());
            var settingsHelper = new SettingsHelper(loggerFactory.CreateLogger<SettingsHelper>());

            // list-models only needs the Copilot CLI, skip full config validation
            if (!args.Contains("list-models") && !ValidateAndLoadConfiguration())
            {
                return 1;
            }

            var rootCommand = BuildRootCommand(loggerFactory, logger, fileHelper, settingsHelper);
            return await rootCommand.InvokeAsync(args);
        }
        finally
        {
            // Stop live logging and restore console
            if (liveLogWriter != null)
            {
                LiveLogWriter.Stop();
            }
        }
    }

    private static RootCommand BuildRootCommand(ILoggerFactory loggerFactory, ILogger logger, FileHelper fileHelper, SettingsHelper settingsHelper)
    {
        var rootCommand = new RootCommand("COBOL to Java Quarkus Migration Tool (Agent Framework)");

        var cobolSourceOption = new Option<string>("--source", "Path to the folder containing COBOL source files and copybooks")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        cobolSourceOption.AddAlias("-s");
        rootCommand.AddOption(cobolSourceOption);

        var javaOutputOption = new Option<string>("--java-output", () => "output", "Path to the folder for Java output files")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        javaOutputOption.AddAlias("-j");
        rootCommand.AddOption(javaOutputOption);

        var reverseEngineerOutputOption = new Option<string>("--reverse-engineer-output", () => "output", "Path to the folder for reverse engineering output")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        reverseEngineerOutputOption.AddAlias("-reo");
        rootCommand.AddOption(reverseEngineerOutputOption);

        var reverseEngineerOnlyOption = new Option<bool>("--reverse-engineer-only", () => false, "Run only reverse engineering (skip Java conversion)")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        reverseEngineerOnlyOption.AddAlias("-reo-only");
        rootCommand.AddOption(reverseEngineerOnlyOption);

        var skipReverseEngineeringOption = new Option<bool>("--skip-reverse-engineering", () => false, "Skip reverse engineering and run only Java conversion")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        skipReverseEngineeringOption.AddAlias("-skip-re");
        rootCommand.AddOption(skipReverseEngineeringOption);

        var reuseReOption = new Option<bool>("--reuse-re", () => false, "When combined with --skip-reverse-engineering, loads business logic persisted from the latest previous RE run and injects it into the conversion prompts")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        reuseReOption.AddAlias("-reuse-re");
        rootCommand.AddOption(reuseReOption);

        var configOption = new Option<string>("--config", () => "Config/appsettings.json", "Path to the configuration file")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        configOption.AddAlias("-c");
        rootCommand.AddOption(configOption);

        var resumeOption = new Option<bool>("--resume", () => false, "Resume from the last migration run if possible")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        resumeOption.AddAlias("-r");
        rootCommand.AddOption(resumeOption);

        var conversationCommand = BuildConversationCommand(loggerFactory);
        rootCommand.AddCommand(conversationCommand);

        var mcpCommand = BuildMcpCommand(loggerFactory, settingsHelper);
        rootCommand.AddCommand(mcpCommand);

        var reverseEngineerCommand = BuildReverseEngineerCommand(loggerFactory, fileHelper, settingsHelper);
        rootCommand.AddCommand(reverseEngineerCommand);

        var listModelsCommand = BuildListModelsCommand();
        rootCommand.AddCommand(listModelsCommand);

        rootCommand.SetHandler(async (string cobolSource, string javaOutput, string reverseEngineerOutput, bool reverseEngineerOnly, bool skipReverseEngineering, bool reuseRe, string configPath, bool resume) =>
        {
            await RunMigrationAsync(loggerFactory, logger, fileHelper, settingsHelper, cobolSource, javaOutput, reverseEngineerOutput, reverseEngineerOnly, skipReverseEngineering, reuseRe, configPath, resume);
        }, cobolSourceOption, javaOutputOption, reverseEngineerOutputOption, reverseEngineerOnlyOption, skipReverseEngineeringOption, reuseReOption, configOption, resumeOption);

        return rootCommand;
    }

    private static Command BuildListModelsCommand()
    {
        var listModelsCommand = new Command("list-models", "List available GitHub Copilot models for the authenticated user");

        listModelsCommand.SetHandler(async () =>
        {
            await using var client = new CopilotClient();
            await client.StartAsync();
            var models = await client.ListModelsAsync();
            foreach (var model in models)
            {
                Console.WriteLine(model.Id);
            }
        });

        return listModelsCommand;
    }

    private static Command BuildConversationCommand(ILoggerFactory loggerFactory)
    {
        var conversationCommand = new Command("conversation", "Generate a readable conversation log from migration logs");

        var sessionIdOption = new Option<string>("--session-id", "Specific session ID to generate conversation for (optional)")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        sessionIdOption.AddAlias("-sid");
        conversationCommand.AddOption(sessionIdOption);

        var logDirOption = new Option<string>("--log-dir", () => "Logs", "Path to the logs directory")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        logDirOption.AddAlias("-ld");
        conversationCommand.AddOption(logDirOption);

        var liveOption = new Option<bool>("--live", () => false, "Enable live conversation feed that updates in real-time");
        liveOption.AddAlias("-l");
        conversationCommand.AddOption(liveOption);

        conversationCommand.SetHandler(async (string sessionId, string logDir, bool live) =>
        {
            await GenerateConversationAsync(loggerFactory, sessionId, logDir, live);
        }, sessionIdOption, logDirOption, liveOption);

        return conversationCommand;
    }

    private static Command BuildMcpCommand(ILoggerFactory loggerFactory, SettingsHelper settingsHelper)
    {
        var mcpCommand = new Command("mcp", "Expose migration insights over the Model Context Protocol");

        var runIdOption = new Option<int?>("--run-id", () => null, "Specific run ID to expose via MCP (defaults to latest)")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        runIdOption.AddAlias("-r");
        mcpCommand.AddOption(runIdOption);

        var configOption = new Option<string>("--config", () => "Config/appsettings.json", "Path to the configuration file")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        configOption.AddAlias("-c");
        mcpCommand.AddOption(configOption);

        mcpCommand.SetHandler(async (int? runId, string configPath) =>
        {
            await RunMcpServerAsync(loggerFactory, settingsHelper, runId, configPath);
        }, runIdOption, configOption);

        return mcpCommand;
    }

    private static Command BuildReverseEngineerCommand(ILoggerFactory loggerFactory, FileHelper fileHelper, SettingsHelper settingsHelper)
    {
        var reverseEngineerCommand = new Command("reverse-engineer", "Extract business logic from COBOL applications");

        var cobolSourceOption = new Option<string>("--source", "Path to the folder containing COBOL source files")
        {
            Arity = ArgumentArity.ExactlyOne
        };
        cobolSourceOption.AddAlias("-s");
        reverseEngineerCommand.AddOption(cobolSourceOption);

        var outputOption = new Option<string>("--output", () => "output", "Path to the output folder")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        outputOption.AddAlias("-o");
        reverseEngineerCommand.AddOption(outputOption);

        var configOption = new Option<string>("--config", () => "Config/appsettings.json", "Path to the configuration file")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        configOption.AddAlias("-c");
        reverseEngineerCommand.AddOption(configOption);

        reverseEngineerCommand.SetHandler(async (string cobolSource, string output, string configPath) =>
        {
            await RunReverseEngineeringAsync(loggerFactory, fileHelper, settingsHelper, cobolSource, output, configPath);
        }, cobolSourceOption, outputOption, configOption);

        return reverseEngineerCommand;
    }

    private static async Task GenerateConversationAsync(ILoggerFactory loggerFactory, string sessionId, string logDir, bool live)
    {
        try
        {
            var enhancedLogger = new EnhancedLogger(loggerFactory.CreateLogger<EnhancedLogger>());
            var logCombiner = new LogCombiner(logDir, enhancedLogger);

            Console.WriteLine("🤖 Generating conversation log from migration data...");

            string outputPath;
            if (live)
            {
                Console.WriteLine("📡 Starting live conversation feed...");
                outputPath = await logCombiner.CreateLiveConversationFeedAsync();
                Console.WriteLine($"✅ Live conversation feed created: {outputPath}");
                Console.WriteLine("📝 The conversation will update automatically as new logs are generated.");
                Console.WriteLine("Press Ctrl+C to stop monitoring.");

                await Task.Delay(-1);
            }
            else
            {
                outputPath = await logCombiner.CreateConversationNarrativeAsync(sessionId);
                Console.WriteLine($"✅ Conversation narrative created: {outputPath}");

                if (File.Exists(outputPath))
                {
                    var preview = await File.ReadAllTextAsync(outputPath);
                    var lines = preview.Split('\n').Take(20).ToArray();

                    Console.WriteLine("\n📖 Preview of conversation:");
                    Console.WriteLine("═══════════════════════════════════════");
                    foreach (var line in lines)
                    {
                        Console.WriteLine(line);
                    }

                    if (preview.Split('\n').Length > 20)
                    {
                        Console.WriteLine("... (and more)");
                    }

                    Console.WriteLine("═══════════════════════════════════════");
                }
            }
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(nameof(Program)).LogError(ex, "Error generating conversation log");
            Environment.Exit(1);
        }
    }

    private static async Task RunMcpServerAsync(ILoggerFactory loggerFactory, SettingsHelper settingsHelper, int? runId, string configPath)
    {
        try
        {
            AppSettings? loadedSettings = await settingsHelper.LoadSettingsAsync<AppSettings>(configPath);
            var settings = loadedSettings ?? new AppSettings();
            LoadEnvironmentVariables();
            OverrideSettingsFromEnvironment(settings);

            var databasePath = settings.ApplicationSettings.MigrationDatabasePath;
            if (!Path.IsPathRooted(databasePath))
            {
                databasePath = Path.GetFullPath(databasePath);
            }

            var repositoryLogger = loggerFactory.CreateLogger<SqliteMigrationRepository>();
            var sqliteRepository = new SqliteMigrationRepository(databasePath, repositoryLogger);
            await sqliteRepository.InitializeAsync();

            // Initialize Neo4j if enabled
            Neo4jMigrationRepository? neo4jRepository = null;
            var mcpLogger = loggerFactory.CreateLogger(nameof(Program));
            if (settings.ApplicationSettings.Neo4j?.Enabled == true)
            {
                try
                {
                    var neo4jDriver = Neo4j.Driver.GraphDatabase.Driver(
                        settings.ApplicationSettings.Neo4j.Uri,
                        Neo4j.Driver.AuthTokens.Basic(
                            settings.ApplicationSettings.Neo4j.Username,
                            settings.ApplicationSettings.Neo4j.Password
                        )
                    );
                    var neo4jLogger = loggerFactory.CreateLogger<Neo4jMigrationRepository>();
                    neo4jRepository = new Neo4jMigrationRepository(neo4jDriver, neo4jLogger);
                    mcpLogger.LogInformation("Neo4j graph database enabled at {Uri}", settings.ApplicationSettings.Neo4j.Uri);
                }
                catch (Exception ex)
                {
                    mcpLogger.LogWarning(ex, "Failed to connect to Neo4j, continuing with SQLite only");
                }
            }

            var hybridLogger = loggerFactory.CreateLogger<HybridMigrationRepository>();
            var repository = new HybridMigrationRepository(sqliteRepository, neo4jRepository, hybridLogger);
            await repository.InitializeAsync();

            var targetRunId = runId;
            if (!targetRunId.HasValue)
            {
                var latest = await repository.GetLatestRunAsync();
                if (latest is null)
                {
                    Console.Error.WriteLine("No migration runs available in the database. Run the migration process first.");
                    return;
                }

                targetRunId = latest.RunId;
            }

            // Create EnhancedLogger early so it can track ALL API calls
            var enhancedLogger = new EnhancedLogger(loggerFactory.CreateLogger<EnhancedLogger>());
            // Get API version from environment or default
            var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2025-04-01-preview";

            // Validate AI settings before using them
            var aiSettings = settings.AISettings;
            if (aiSettings is null)
            {
                Console.Error.WriteLine("AI settings are not configured. Cannot initialize ResponsesApiClient.");
                return;
            }

            // Create ResponsesApiClient for code agents - Use same logic as RunMigrationAsync
            ResponsesApiClient? responsesApiClient = null;
            if (IsAzureOpenAIMode(aiSettings) &&
                !string.IsNullOrEmpty(aiSettings.Endpoint) && !string.IsNullOrEmpty(aiSettings.DeploymentName))
            {
                // Force Entra ID (DefaultAzureCredential) by passing empty API Key as requested
                responsesApiClient = new ResponsesApiClient(
                    aiSettings.Endpoint,
                    string.Empty, // Forces DefaultAzureCredential
                    aiSettings.DeploymentName,
                    loggerFactory.CreateLogger<ResponsesApiClient>(),
                    enhancedLogger,
                    profile: settings.CodexProfile,
                    apiVersion: apiVersion,
                    rateLimitSafetyFactor: settings.ChunkingSettings.RateLimitSafetyFactor);
                mcpLogger.LogInformation("ResponsesApiClient initialized for codex model: {DeploymentName} (Entra ID)", aiSettings.DeploymentName);
            }

            // Create IChatClient for MCP server
            IChatClient? chatClient = null;
            var chatDeployment = aiSettings.ChatDeploymentName ?? aiSettings.ChatModelId ?? aiSettings.DeploymentName;

            if (IsGitHubCopilotMode(aiSettings) || IsAnthropicMode(aiSettings) || IsClaudeCodeMode(aiSettings))
            {
                chatClient = CreateChatClientFromSettings(aiSettings, mcpLogger, forChat: true);
            }
            else if (!string.IsNullOrEmpty(aiSettings.ChatEndpoint ?? aiSettings.Endpoint) && !string.IsNullOrEmpty(chatDeployment))
            {
                var chatEndpoint = aiSettings.ChatEndpoint ?? aiSettings.Endpoint;
                var chatApiKey = aiSettings.ChatApiKey ?? aiSettings.ApiKey;
                bool useEntraId = string.IsNullOrEmpty(chatApiKey) || chatApiKey.Contains("your-api-key");
                if (useEntraId)
                {
                    chatClient = ChatClientFactory.CreateAzureOpenAIChatClientWithDefaultCredential(chatEndpoint!, chatDeployment);
                    mcpLogger.LogInformation("IChatClient initialized for MCP server with deployment: {Deployment} (Entra ID)", chatDeployment);
                }
                else if (!string.IsNullOrEmpty(chatApiKey))
                {
                    chatClient = ChatClientFactory.CreateAzureOpenAIChatClient(chatEndpoint!, chatApiKey, chatDeployment);
                    mcpLogger.LogInformation("IChatClient initialized for MCP server with deployment: {Deployment} (API Key)", chatDeployment);
                }
            }

            var serverLogger = loggerFactory.CreateLogger<McpServer>();
            var server = new McpServer(repository, targetRunId.Value, serverLogger, settings.AISettings, chatClient, responsesApiClient);
            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (sender, args) =>
            {
                args.Cancel = true;
                cts.Cancel();
            };

            await server.RunAsync(cts.Token);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(nameof(Program)).LogError(ex, "Error running MCP server");
            Environment.Exit(1);
        }
    }

    private static bool IsGitHubCopilotMode(AISettings ai)
        => string.Equals(ai.ServiceType, "GitHubCopilot", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnthropicMode(AISettings ai)
        => string.Equals(ai.ServiceType, "Anthropic", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaudeCodeMode(AISettings ai)
        => string.Equals(ai.ServiceType, "ClaudeCode", StringComparison.OrdinalIgnoreCase);

    private static bool IsAzureOpenAIMode(AISettings ai)
        => !IsGitHubCopilotMode(ai) && !IsAnthropicMode(ai) && !IsClaudeCodeMode(ai);

    private static string GetProviderName(AISettings ai)
        => IsGitHubCopilotMode(ai) ? "GitHub Copilot"
         : IsAnthropicMode(ai)     ? "Anthropic Claude"
         : IsClaudeCodeMode(ai)    ? "Claude Code"
         :                            "Azure OpenAI";

    private static IChatClient CreateChatClientFromSettings(
        AISettings ai,
        ILogger logger,
        bool forChat = true)
    {
        var endpoint  = forChat ? (ai.ChatEndpoint  ?? ai.Endpoint)        : ai.Endpoint;
        var apiKey    = forChat ? (ai.ChatApiKey     ?? ai.ApiKey)          : ai.ApiKey;
        var deployment = forChat ? (ai.ChatDeploymentName ?? ai.DeploymentName) : ai.DeploymentName;
        var modelId   = forChat ? (ai.ChatModelId   ?? ai.ModelId)         : ai.ModelId;

        if (IsGitHubCopilotMode(ai))
        {
            var ghToken = Environment.GetEnvironmentVariable("GITHUB_COPILOT_TOKEN");
            var client = ChatClientFactory.CreateGitHubCopilotChatClient(
                modelId ?? deployment,
                githubToken: ghToken,
                logger);
            logger.LogInformation("IChatClient initialized via GitHub Copilot SDK for model: {Model}", modelId ?? deployment);
            return client;
        }

        if (IsAnthropicMode(ai))
        {
            var client = ChatClientFactory.CreateAnthropicChatClient(apiKey, modelId, logger);
            logger.LogInformation("IChatClient initialized via Anthropic for model: {Model}", modelId);
            return client;
        }

        if (IsClaudeCodeMode(ai))
        {
            var client = ChatClientFactory.CreateClaudeCodeChatClient(modelId ?? deployment, logger);
            logger.LogInformation("IChatClient initialized via Claude Code CLI for model: {Model}", modelId ?? deployment);
            return client;
        }

        bool useEntraId = string.IsNullOrEmpty(apiKey) || apiKey.Contains("your-api-key");
        if (useEntraId)
        {
            var client = ChatClientFactory.CreateAzureOpenAIChatClientWithDefaultCredential(endpoint, deployment);
            logger.LogInformation("IChatClient initialized for model: {Deployment} (Entra ID)", deployment);
            return client;
        }
        else
        {
            var client = ChatClientFactory.CreateAzureOpenAIChatClient(endpoint, apiKey, deployment);
            logger.LogInformation("IChatClient initialized for model: {Deployment} (API Key)", deployment);
            return client;
        }
    }

    private static void ConfigureSmartChunking(AppSettings settings, string chatDeployment, ILogger logger)
    {
        // 1. TRUST THE CONFIGURATION FIRST:
        // If the user has explicitly set ContextWindowSize in appsettings.json, use that numeric value
        // to determine if we can enable Whole-Program Analysis.
        // This decouples the logic from "magic strings" and allows future models to work automatically.
        if (settings.AISettings.ContextWindowSize.HasValue) 
        {
             var size = settings.AISettings.ContextWindowSize.Value;
             if (size >= 100_000) // 100k+ tokens (covers 128k, 200k, 1M models)
             {
                 // Apply high-context optimization
                settings.ChunkingSettings.AutoChunkLineThreshold = 25_000; 
                settings.ChunkingSettings.AutoChunkCharThreshold = 400_000; 
                settings.ChunkingSettings.MaxTokensPerChunk = 90_000;
                
                logger.LogInformation("🚀 Optimized Strategy: 'Whole-Program Analysis' enabled based on configured ContextWindowSize ({Size} tokens).", size);
                return;
             }
        }

        // 2. FALLBACK TO DETECTION (Legacy/Convenience):
        // If no explicit context size is provided, we try to detect known high-context models.
        var targetModel = chatDeployment ?? settings.AISettings.ChatModelId ?? settings.AISettings.DeploymentName;

        if (!string.IsNullOrEmpty(targetModel))
        {
            // Detect High-Context Models dynamically from config strings
            if (targetModel.Contains("gpt-5", StringComparison.OrdinalIgnoreCase) || 
                targetModel.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
                targetModel.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
                targetModel.Contains("o1-", StringComparison.OrdinalIgnoreCase) ||
                targetModel.Contains("128k", StringComparison.OrdinalIgnoreCase))
            {
                // Increase transparency for next-gen models:
                settings.ChunkingSettings.AutoChunkLineThreshold = 25_000; 
                settings.ChunkingSettings.AutoChunkCharThreshold = 400_000; 
                settings.ChunkingSettings.MaxTokensPerChunk = 90_000;
                
                logger.LogInformation("🚀 Next-Gen Model Detected ({Model}). Optimized strategy: 'Whole-Program Analysis' enabled for files up to {NewLines} lines.", 
                    targetModel, settings.ChunkingSettings.AutoChunkLineThreshold);
            }
        }
    }

    private static async Task RunMigrationAsync(ILoggerFactory loggerFactory, ILogger logger, FileHelper fileHelper, SettingsHelper settingsHelper, string cobolSource, string javaOutput, string reverseEngineerOutput, bool reverseEngineerOnly, bool skipReverseEngineering, bool reuseRe, string configPath, bool resume)
    {
        try
        {
            logger.LogInformation("Loading settings from {ConfigPath}", configPath);
            AppSettings? loadedSettings = await settingsHelper.LoadSettingsAsync<AppSettings>(configPath);
            var settings = loadedSettings ?? new AppSettings();

            LoadEnvironmentVariables();
            OverrideSettingsFromEnvironment(settings);

            if (string.IsNullOrEmpty(settings.ApplicationSettings.CobolSourceFolder))
            {
                logger.LogError("COBOL source folder not specified. Use --source option or set in config file.");
                Environment.Exit(1);
            }

            // Output folder validation - check both Java and C# based on target
            var targetLanguage = settings.ApplicationSettings.TargetLanguage;
            if (targetLanguage == TargetLanguage.CSharp)
            {
                // For C#, use CSharpOutputFolder if set, otherwise fall back to JavaOutputFolder or default
                if (string.IsNullOrEmpty(settings.ApplicationSettings.CSharpOutputFolder) && 
                    string.IsNullOrEmpty(settings.ApplicationSettings.JavaOutputFolder))
                {
                    settings.ApplicationSettings.CSharpOutputFolder = "output/csharp";
                    logger.LogInformation("Using default C# output folder: output/csharp");
                }
            }
            else
            {
                // For Java, require JavaOutputFolder
                if (string.IsNullOrEmpty(settings.ApplicationSettings.JavaOutputFolder))
                {
                    logger.LogError("Java output folder not specified. Use --java-output option or set in config file.");
                    Environment.Exit(1);
                }
            }

            // Validate required AI configuration (relaxed for non-Azure providers)
            if (IsAzureOpenAIMode(settings.AISettings))
            {
                if (string.IsNullOrEmpty(settings.AISettings.Endpoint) ||
                    string.IsNullOrEmpty(settings.AISettings.DeploymentName))
                {
                    logger.LogError("Azure OpenAI configuration incomplete. Please ensure endpoint and deployment name are configured.");
                    logger.LogError("You can set them in Config/ai-config.local.env or as environment variables.");
                    Environment.Exit(1);
                }
            }
            else if (IsAnthropicMode(settings.AISettings))
            {
                if (string.IsNullOrEmpty(settings.AISettings.ApiKey))
                {
                    logger.LogError("Anthropic configuration incomplete. Please ensure API key is configured.");
                    logger.LogError("Set AISETTINGS__APIKEY in Config/ai-config.local.env or as an environment variable.");
                    Environment.Exit(1);
                }
            }

            if (string.IsNullOrEmpty(settings.AISettings.ModelId))
            {
                logger.LogError("ModelId is not configured. Set AISettings:ModelId in appsettings.json or AZURE_OPENAI_MODEL_ID environment variable.");
                Environment.Exit(1);
            }

            // Create EnhancedLogger early so it can track ALL API calls
            var enhancedLogger = new EnhancedLogger(loggerFactory.CreateLogger<EnhancedLogger>());
            var providerName = GetProviderName(settings.AISettings);
            var chatLogger = new ChatLogger(loggerFactory.CreateLogger<ChatLogger>(), providerName: providerName);

            // Get API version from environment or default
            var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2025-04-01-preview";

            ResponsesApiClient? responsesApiClient = null;
            if (IsAzureOpenAIMode(settings.AISettings))
            {
                responsesApiClient = new ResponsesApiClient(
                    settings.AISettings.Endpoint,
                    string.Empty, // Forces DefaultAzureCredential
                    settings.AISettings.DeploymentName,
                    loggerFactory.CreateLogger<ResponsesApiClient>(),
                    enhancedLogger,
                    profile: settings.CodexProfile,
                    apiVersion: apiVersion,
                    rateLimitSafetyFactor: settings.ChunkingSettings.RateLimitSafetyFactor);

                logger.LogInformation("ResponsesApiClient initialized for codex model: {DeploymentName} (API: {ApiVersion}, Entra ID)", 
                    settings.AISettings.DeploymentName, apiVersion);
            }

            var chatDeployment = settings.AISettings.ChatDeploymentName ?? settings.AISettings.DeploymentName;
            IChatClient chatClient = CreateChatClientFromSettings(settings.AISettings, logger, forChat: true);

            var databasePath = settings.ApplicationSettings.MigrationDatabasePath;
            if (!Path.IsPathRooted(databasePath))
            {
                databasePath = Path.GetFullPath(databasePath);
            }

            var migrationRepositoryLogger = loggerFactory.CreateLogger<SqliteMigrationRepository>();
            var sqliteMigrationRepository = new SqliteMigrationRepository(databasePath, migrationRepositoryLogger);
            await sqliteMigrationRepository.InitializeAsync();

            // Initialize Neo4j if enabled
            Neo4jMigrationRepository? neo4jMigrationRepository = null;
            if (settings.ApplicationSettings.Neo4j?.Enabled == true)
            {
                try
                {
                    var neo4jDriver = Neo4j.Driver.GraphDatabase.Driver(
                        settings.ApplicationSettings.Neo4j.Uri,
                        Neo4j.Driver.AuthTokens.Basic(
                            settings.ApplicationSettings.Neo4j.Username,
                            settings.ApplicationSettings.Neo4j.Password
                        )
                    );
                    var neo4jLogger = loggerFactory.CreateLogger<Neo4jMigrationRepository>();
                    neo4jMigrationRepository = new Neo4jMigrationRepository(neo4jDriver, neo4jLogger);
                    logger.LogInformation("✅ Neo4j graph database connected at {Uri}", settings.ApplicationSettings.Neo4j.Uri);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "⚠️  Neo4j connection failed, using SQLite only");
                }
            }

            var hybridLogger = loggerFactory.CreateLogger<HybridMigrationRepository>();
            var migrationRepository = new HybridMigrationRepository(sqliteMigrationRepository, neo4jMigrationRepository, hybridLogger);
            await migrationRepository.InitializeAsync();

            // Cleanup stale runs on startup
            if (!resume) 
            {
                await migrationRepository.CleanupStaleRunsAsync();
            }

            // Holds business logic extracted during reverse engineering to be passed into migration
            ReverseEngineeringResult? reverseEngResultForMigration = null;

            // Step 1: Run reverse engineering if requested (and not skipped)
            if (!skipReverseEngineering || reverseEngineerOnly)
            {
                // EnhancedLogger and ChatLogger already created above for API tracking

                ConfigureSmartChunking(settings, chatDeployment, logger);

                // Create orchestrator early so agents can use it
                var targetLang = settings.ApplicationSettings.TargetLanguage;
                var chunkingOrchestrator = new CobolToQuarkusMigration.Chunking.ChunkingOrchestrator(
                    settings.ChunkingSettings,
                    settings.ConversionSettings,
                    databasePath,
                    loggerFactory.CreateLogger<CobolToQuarkusMigration.Chunking.ChunkingOrchestrator>(),
                    targetLang);

                var cobolAnalyzerAgent = CobolAnalyzerAgent.Create(
                    responsesApiClient, chatClient,
                    loggerFactory.CreateLogger<CobolAnalyzerAgent>(),
                    settings.AISettings.CobolAnalyzerModelId,
                    enhancedLogger, chatLogger, settings: settings);

                var businessLogicExtractorAgent = BusinessLogicExtractorAgent.Create(
                    responsesApiClient, chatClient,
                    loggerFactory.CreateLogger<BusinessLogicExtractorAgent>(),
                    chatDeployment,
                    enhancedLogger, chatLogger,
                    chunkingOrchestrator: chunkingOrchestrator, settings: settings);

                var dependencyMapperAgent = DependencyMapperAgent.Create(
                    responsesApiClient, chatClient,
                    loggerFactory.CreateLogger<DependencyMapperAgent>(),
                    settings.AISettings.DependencyMapperModelId ?? settings.AISettings.CobolAnalyzerModelId,
                    enhancedLogger, chatLogger, settings: settings);

                // Smart routing: check for large files to decide between chunked vs direct RE
                var cobolFiles = await fileHelper.ScanDirectoryForCobolFilesAsync(settings.ApplicationSettings.CobolSourceFolder);
                var hasLargeFiles = cobolFiles.Any(f => 
                    settings.ChunkingSettings.RequiresChunking(f.Content.Length, f.Content.Split('\n').Length));

                ReverseEngineeringResult reverseEngResult;

                if (hasLargeFiles)
                {
                    // Use ChunkedReverseEngineeringProcess for large files
                    Console.WriteLine("📦 Large files detected - using smart chunked reverse engineering");
                    logger.LogInformation("Large files detected, using ChunkedReverseEngineeringProcess");

                    // chunkingOrchestrator and targetLang initialized in outer scope

                    var chunkedREProcess = new ChunkedReverseEngineeringProcess(
                        cobolAnalyzerAgent,
                        businessLogicExtractorAgent,
                        dependencyMapperAgent,
                        fileHelper,
                        settings.ChunkingSettings,
                        chunkingOrchestrator,
                        loggerFactory.CreateLogger<ChunkedReverseEngineeringProcess>(),
                        enhancedLogger,
                        databasePath,
                        migrationRepository);

                    reverseEngResult = await chunkedREProcess.RunAsync(
                        settings.ApplicationSettings.CobolSourceFolder,
                        reverseEngineerOutput,
                        (status, current, total) =>
                        {
                            Console.WriteLine($"{status} - {current}/{total}");
                        });
                }
                else
                {
                    // Use standard ReverseEngineeringProcess for small files
                    Console.WriteLine("⚡ All files below chunking threshold - using direct reverse engineering");

                    var reverseEngineeringProcess = new ReverseEngineeringProcess(
                        cobolAnalyzerAgent,
                        businessLogicExtractorAgent,
                        dependencyMapperAgent,
                        fileHelper,
                        loggerFactory.CreateLogger<ReverseEngineeringProcess>(),
                        enhancedLogger,
                        migrationRepository);

                    reverseEngResult = await reverseEngineeringProcess.RunAsync(
                        settings.ApplicationSettings.CobolSourceFolder,
                        reverseEngineerOutput,
                        (status, current, total) =>
                        {
                            Console.WriteLine($"{status} - {current}/{total}");
                        });
                }

                if (!reverseEngResult.Success)
                {
                    logger.LogError("Reverse engineering failed: {Error}", reverseEngResult.ErrorMessage);
                    Environment.Exit(1);
                }

                // Store reverse engineering result so migration can use the extracted business logic
                reverseEngResultForMigration = reverseEngResult;

                // If reverse-engineer-only mode, exit here
                if (reverseEngineerOnly)
                {
                    Console.WriteLine("Reverse engineering completed successfully. Skipping Java conversion as requested.");
                    return;
                }
            }
            else
            {
                // --skip-reverse-engineering: only load persisted business logic when --reuse-re is also set
                if (reuseRe)
                {
                    var latestRun = await migrationRepository.GetLatestRunAsync();
                    if (latestRun != null && latestRun.RunId > 0)
                    {
                        var savedLogic = await migrationRepository.GetBusinessLogicAsync(latestRun.RunId);
                        if (savedLogic.Count > 0)
                        {
                            Console.WriteLine($"♻️  Loaded {savedLogic.Count} business logic entries from Run #{latestRun.RunId} (use --reuse-re with a specific run by passing --resume).");
                            reverseEngResultForMigration = new ReverseEngineeringResult
                            {
                                Success = true,
                                RunId = latestRun.RunId,
                                BusinessLogicExtracts = savedLogic.ToList()
                            };
                        }
                        else
                        {
                            Console.WriteLine($"⚠️  --reuse-re: no persisted business logic found for Run #{latestRun.RunId}. Migration will proceed without business logic context.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("⚠️  --reuse-re: no previous run found. Migration will proceed without business logic context.");
                    }
                }
                else
                {
                    Console.WriteLine("ℹ️  Skipping reverse engineering. Pass --reuse-re to inject business logic from a previous RE run.");
                }
            }

            // Step 2: Run code conversion (unless reverse-engineer-only mode)
            if (!reverseEngineerOnly)
            {
                // Determine output folder based on target language
                var targetLang = settings.ApplicationSettings.TargetLanguage;
                var outputFolder = targetLang == TargetLanguage.CSharp 
                    ? settings.ApplicationSettings.CSharpOutputFolder 
                    : settings.ApplicationSettings.JavaOutputFolder;

                // Ensure output folder has a default value
                if (string.IsNullOrEmpty(outputFolder))
                {
                    outputFolder = targetLang == TargetLanguage.CSharp ? "output/csharp" : "output/java";
                }

                var langName = targetLang == TargetLanguage.CSharp ? "C# .NET" : "Java Quarkus";
                
                // ═══════════════════════════════════════════════════════════════════
                // QUALITY GATE: Final verification before migration starts
                // ═══════════════════════════════════════════════════════════════════
                Console.WriteLine("");
                Console.WriteLine("═══════════════════════════════════════════════════════════════════");
                Console.WriteLine("                    🔒 QUALITY GATE: PRE-MIGRATION CHECK");
                Console.WriteLine("═══════════════════════════════════════════════════════════════════");
                Console.WriteLine($"  Target Language:  {langName}");
                Console.WriteLine($"  Output Folder:    {outputFolder}");
                Console.WriteLine($"  ENV Variable:     TARGET_LANGUAGE={Environment.GetEnvironmentVariable("TARGET_LANGUAGE") ?? "(not set)"}");
                Console.WriteLine($"  Smart Chunking:   ENABLED (auto-routes large files)");
                Console.WriteLine($"  Chunk Threshold:  {settings.ChunkingSettings.AutoChunkCharThreshold:N0} chars / {settings.ChunkingSettings.AutoChunkLineThreshold:N0} lines");
                
                // Verify env var matches what we're about to do
                var envLang = Environment.GetEnvironmentVariable("TARGET_LANGUAGE");
                if (!string.IsNullOrEmpty(envLang))
                {
                    var expectedCSharp = envLang.Equals("CSharp", StringComparison.OrdinalIgnoreCase) || 
                                         envLang.Equals("C#", StringComparison.OrdinalIgnoreCase);
                    var actualCSharp = targetLang == TargetLanguage.CSharp;
                    
                    if (expectedCSharp != actualCSharp)
                    {
                        Console.WriteLine($"  ❌ MISMATCH DETECTED!");
                        Console.WriteLine($"     ENV says: {(expectedCSharp ? "CSharp" : "Java")}");
                        Console.WriteLine($"     Settings say: {targetLang}");
                        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
                        Console.WriteLine("❌ QUALITY GATE FAILED: Language mismatch between env var and settings!");
                        Console.WriteLine("   This indicates a bug in configuration loading. Aborting.");
                        Environment.Exit(1);
                    }
                }
                
                Console.WriteLine($"  ✅ Quality Gate PASSED");
                Console.WriteLine("═══════════════════════════════════════════════════════════════════");
                Console.WriteLine("");
                
                Console.WriteLine($"Starting COBOL to {langName} migration with Smart Orchestration...");
                Console.WriteLine($"Target language: {langName}");
                Console.WriteLine($"Output folder: {outputFolder}");

                int? resumeRunId = null;
                if (resume)
                {
                    logger.LogInformation("Resume requested. Fetching latest run...");
                    var latestRun = await migrationRepository.GetLatestRunAsync();
                    if (latestRun != null && latestRun.Status != "Completed")
                    {
                        resumeRunId = latestRun.RunId;
                        logger.LogInformation("Resuming run ID: {RunId} (Status: {Status})", latestRun.RunId, latestRun.Status);
                        Console.WriteLine($"🔄 Resuming from Run ID: {latestRun.RunId}");
                    }
                    else
                    {
                        logger.LogWarning("Resume requested but no active run found. Starting new run.");
                        Console.WriteLine("⚠️  No resumable run found. Starting new run.");
                    }
                }

                // Use SmartMigrationOrchestrator for intelligent file routing
                // - Small files → direct MigrationProcess (fast, no chunking overhead)
                // - Large files → ChunkedMigrationProcess (preserves ALL code, no truncation)
                var smartOrchestrator = new SmartMigrationOrchestrator(
                    responsesApiClient,
                    chatClient,
                    loggerFactory,
                    fileHelper,
                    settings,
                    migrationRepository);

                var migrationStats = await smartOrchestrator.RunAsync(
                    settings.ApplicationSettings.CobolSourceFolder,
                    outputFolder,
                    (status, current, total) =>
                    {
                        Console.WriteLine($"{status} - {current}/{total}");
                    },
                    existingRunId: resumeRunId,
                    businessLogicExtracts: reverseEngResultForMigration?.BusinessLogicExtracts,
                    existingDependencyMap: reverseEngResultForMigration?.DependencyMap,
                    runType: skipReverseEngineering ? "Conversion Only" : "Full Migration");

                Console.WriteLine("Migration process completed successfully.");
                Console.WriteLine($"  📊 Stats: {migrationStats.TotalFiles} files ({migrationStats.DirectFiles} direct, {migrationStats.ChunkedFiles} chunked)");
                if (migrationStats.TotalChunks > 0)
                {
                    Console.WriteLine($"  📦 Total chunks processed: {migrationStats.TotalChunks}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in migration process");
            Environment.Exit(1);
        }
    }

    private static void LoadEnvironmentVariables()
    {
        try
        {
            string currentDir = Directory.GetCurrentDirectory();
            string configDir = Path.Combine(currentDir, "Config");
            string localConfigFile = Path.Combine(configDir, "ai-config.local.env");
            string templateConfigFile = Path.Combine(configDir, "ai-config.env");

            if (File.Exists(localConfigFile))
            {
                LoadEnvFile(localConfigFile);
            }
            else
            {
                Console.WriteLine("💡 Consider creating Config/ai-config.local.env for your personal settings");
                Console.WriteLine("   You can copy from Config/ai-config.local.env.example");
            }

            if (File.Exists(templateConfigFile))
            {
                LoadEnvFile(templateConfigFile);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error loading environment configuration: {ex.Message}");
        }
    }

    private static void LoadEnvFile(string filePath)
    {
        var rawVars = new Dictionary<string, string>();

        foreach (string line in File.ReadAllLines(filePath))
        {
            string trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith('#'))
                continue;

            var parts = trimmedLine.Split('=', 2);
            if (parts.Length == 2)
            {
                string key = parts[0].Trim();
                string value = parts[1].Trim().Trim('"', '\'');
                
                // Store raw value first
                rawVars[key] = value;
                
                // Variable Expansion (Basic)
                // If value contains $VAR or ${VAR}, try to replace from ALREADY loaded Env vars or current file dictionary
                if (value.Contains('$'))
                {
                    value = ExpandVariables(value, rawVars);
                }

                // CRITICAL: Do NOT overwrite environment variables that are already set
                var existingValue = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrEmpty(existingValue))
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
                else if (key == "TARGET_LANGUAGE")
                {
                    Console.WriteLine($"🔒 Preserving TARGET_LANGUAGE from shell: '{existingValue}' (ignoring config file value: '{value}')");
                }
            }
        }
    }

    private static string ExpandVariables(string value, Dictionary<string, string> fileVars)
    {
        // Simple expansion: find $VAR or ${VAR}
        // This is not a full bash emulator but handles common cases
        // We iterate specifically looking for keys we might know
        
        // 1. Check fileVars (local scope first)
        foreach (var kvp in fileVars)
        {
             value = value.Replace($"${{{kvp.Key}}}", kvp.Value)
                          .Replace($"${kvp.Key}", kvp.Value);
        }

        // 2. Check Environment (global scope)
        // We don't iterate all env vars (too many), but we could use regex to find substitutions
        // For now, let's just handle simple known variable patterns if needed, 
        // but typically users only reference vars defined earlier in the SAME file or standard ones.
        return value;
    }

    private static void OverrideSettingsFromEnvironment(AppSettings settings)
    {
        var aiSettings = settings.AISettings ??= new AISettings();
        var applicationSettings = settings.ApplicationSettings ??= new ApplicationSettings();
        
        // Primary deployment (for codex models via Responses API)
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        if (!string.IsNullOrEmpty(endpoint))
        {
            aiSettings.Endpoint = endpoint;
        }

        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            aiSettings.ApiKey = apiKey;
        }

        var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME");
        if (!string.IsNullOrEmpty(deploymentName))
        {
            aiSettings.DeploymentName = deploymentName;
        }

        var modelId = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL_ID");
        if (!string.IsNullOrEmpty(modelId))
        {
            aiSettings.ModelId = modelId;
        }

        // Chat deployment (for gpt-5.1-chat models via Chat Completions API)
        var chatEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_ENDPOINT");
        if (!string.IsNullOrEmpty(chatEndpoint))
        {
            aiSettings.ChatEndpoint = chatEndpoint;
        }

        var chatApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_API_KEY");
        if (!string.IsNullOrEmpty(chatApiKey))
        {
            aiSettings.ChatApiKey = chatApiKey;
        }

        var chatDeploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_DEPLOYMENT_NAME");
        if (!string.IsNullOrEmpty(chatDeploymentName))
        {
            aiSettings.ChatDeploymentName = chatDeploymentName;
        }

        var chatModelId = Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_MODEL_ID");
        if (!string.IsNullOrEmpty(chatModelId))
        {
            aiSettings.ChatModelId = chatModelId;
        }

        // Specialized model IDs
        var cobolModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_COBOL_ANALYZER_MODEL");
        if (!string.IsNullOrEmpty(cobolModel))
        {
            aiSettings.CobolAnalyzerModelId = cobolModel;
        }

        var javaModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_JAVA_CONVERTER_MODEL");
        if (!string.IsNullOrEmpty(javaModel))
        {
            aiSettings.JavaConverterModelId = javaModel;
        }

        var depModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPENDENCY_MAPPER_MODEL");
        if (!string.IsNullOrEmpty(depModel))
        {
            aiSettings.DependencyMapperModelId = depModel;
        }

        var testModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_UNIT_TEST_MODEL");
        if (!string.IsNullOrEmpty(testModel))
        {
            aiSettings.UnitTestModelId = testModel;
        }

        var serviceType = Environment.GetEnvironmentVariable("AZURE_OPENAI_SERVICE_TYPE");
        if (!string.IsNullOrEmpty(serviceType))
        {
            aiSettings.ServiceType = serviceType;
        }

        // AISETTINGS__* env vars (provider-agnostic, set by doctor.sh for all providers).
        // These take priority over the AZURE_OPENAI_* vars above.
        var aisEndpoint = Environment.GetEnvironmentVariable("AISETTINGS__ENDPOINT");
        if (!string.IsNullOrEmpty(aisEndpoint))
            aiSettings.Endpoint = aisEndpoint;

        var aisApiKey = Environment.GetEnvironmentVariable("AISETTINGS__APIKEY");
        if (!string.IsNullOrEmpty(aisApiKey))
            aiSettings.ApiKey = aisApiKey;

        var aisModelId = Environment.GetEnvironmentVariable("AISETTINGS__MODELID");
        if (!string.IsNullOrEmpty(aisModelId))
            aiSettings.ModelId = aisModelId;

        var aisDeployment = Environment.GetEnvironmentVariable("AISETTINGS__DEPLOYMENTNAME");
        if (!string.IsNullOrEmpty(aisDeployment))
            aiSettings.DeploymentName = aisDeployment;

        var aisChatEndpoint = Environment.GetEnvironmentVariable("AISETTINGS__CHATENDPOINT");
        if (!string.IsNullOrEmpty(aisChatEndpoint))
            aiSettings.ChatEndpoint = aisChatEndpoint;

        var aisChatApiKey = Environment.GetEnvironmentVariable("AISETTINGS__CHATAPIKEY");
        if (!string.IsNullOrEmpty(aisChatApiKey))
            aiSettings.ChatApiKey = aisChatApiKey;

        var aisChatModelId = Environment.GetEnvironmentVariable("AISETTINGS__CHATMODELID");
        if (!string.IsNullOrEmpty(aisChatModelId))
            aiSettings.ChatModelId = aisChatModelId;

        var aisChatDeployment = Environment.GetEnvironmentVariable("AISETTINGS__CHATDEPLOYMENTNAME");
        if (!string.IsNullOrEmpty(aisChatDeployment))
            aiSettings.ChatDeploymentName = aisChatDeployment;

        var aisCobolModel = Environment.GetEnvironmentVariable("AISETTINGS__COBOLANALYZERMODELID");
        if (!string.IsNullOrEmpty(aisCobolModel))
            aiSettings.CobolAnalyzerModelId = aisCobolModel;

        var aisJavaModel = Environment.GetEnvironmentVariable("AISETTINGS__JAVACONVERTERMODELID");
        if (!string.IsNullOrEmpty(aisJavaModel))
            aiSettings.JavaConverterModelId = aisJavaModel;

        var aisDependencyModel = Environment.GetEnvironmentVariable("AISETTINGS__DEPENDENCYMAPPERMODELID");
        if (!string.IsNullOrEmpty(aisDependencyModel))
            aiSettings.DependencyMapperModelId = aisDependencyModel;

        var aisTestModel = Environment.GetEnvironmentVariable("AISETTINGS__UNITTESTMODELID");
        if (!string.IsNullOrEmpty(aisTestModel))
            aiSettings.UnitTestModelId = aisTestModel;

        // Context Window Override (Explicitly set context size to avoid magic string detection)
        var contextWindowSize = Environment.GetEnvironmentVariable("AZURE_OPENAI_CONTEXT_WINDOW_SIZE");
        if (!string.IsNullOrEmpty(contextWindowSize) && int.TryParse(contextWindowSize, out int size))
        {
            aiSettings.ContextWindowSize = size;
        }

        if (Environment.GetEnvironmentVariable("COBOL_SOURCE_FOLDER") is { Length: > 0 } cobolSource)
        {
            applicationSettings.CobolSourceFolder = cobolSource;
        }

        if (Environment.GetEnvironmentVariable("JAVA_OUTPUT_FOLDER") is { Length: > 0 } javaOutput)
        {
            applicationSettings.JavaOutputFolder = javaOutput;
        }

        if (Environment.GetEnvironmentVariable("TEST_OUTPUT_FOLDER") is { Length: > 0 } testOutput)
        {
            applicationSettings.TestOutputFolder = testOutput;
        }

        if (Environment.GetEnvironmentVariable("MIGRATION_DB_PATH") is { Length: > 0 } migrationDb)
        {
            applicationSettings.MigrationDatabasePath = migrationDb;
        }

        // Target language selection (from doctor.sh or config file)
        if (Environment.GetEnvironmentVariable("TARGET_LANGUAGE") is { Length: > 0 } targetLang)
        {
            var trimmedLang = targetLang.Trim();
            var isCSharp = trimmedLang.Equals("CSharp", StringComparison.OrdinalIgnoreCase) ||
                           trimmedLang.Equals("C#", StringComparison.OrdinalIgnoreCase);
            applicationSettings.TargetLanguage = isCSharp ? TargetLanguage.CSharp : TargetLanguage.Java;
            
            // Quality Gate: Verify the language was correctly parsed
            Console.WriteLine($"🎯 TARGET_LANGUAGE env var: '{trimmedLang}' → {applicationSettings.TargetLanguage}");
            Console.WriteLine($"🔒 Quality Gate: Language selection = {applicationSettings.TargetLanguage}");
            
            // Additional verification: Check if the env var value matches expected values
            if (!trimmedLang.Equals("Java", StringComparison.OrdinalIgnoreCase) &&
                !trimmedLang.Equals("CSharp", StringComparison.OrdinalIgnoreCase) &&
                !trimmedLang.Equals("C#", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"⚠️  WARNING: TARGET_LANGUAGE='{trimmedLang}' is not a recognized value.");
                Console.WriteLine($"   Expected: 'Java' or 'CSharp'. Falling back to: {applicationSettings.TargetLanguage}");
            }
        }
        else
        {
            Console.WriteLine($"🎯 TARGET_LANGUAGE env var not set, using config default: {applicationSettings.TargetLanguage}");
        }

        // C# output folder (separate from Java output)
        if (Environment.GetEnvironmentVariable("CSHARP_OUTPUT_FOLDER") is { Length: > 0 } csharpOutput)
        {
            applicationSettings.CSharpOutputFolder = csharpOutput;
        }

        // ── Model Profile Overrides (Three-Tier Reasoning) ──────────────────

        // Codex Profile overrides
        var codexProfile = settings.CodexProfile ??= new ModelProfileSettings();

        if (Environment.GetEnvironmentVariable("CODEX_LOW_REASONING_EFFORT") is { Length: > 0 } codexLowEffort)
            codexProfile.LowReasoningEffort = codexLowEffort;
        if (Environment.GetEnvironmentVariable("CODEX_MEDIUM_REASONING_EFFORT") is { Length: > 0 } codexMedEffort)
            codexProfile.MediumReasoningEffort = codexMedEffort;
        if (Environment.GetEnvironmentVariable("CODEX_HIGH_REASONING_EFFORT") is { Length: > 0 } codexHighEffort)
            codexProfile.HighReasoningEffort = codexHighEffort;

        if (Environment.GetEnvironmentVariable("CODEX_MEDIUM_THRESHOLD") is { Length: > 0 } codexMedThresh
            && int.TryParse(codexMedThresh, out var cmtVal))
            codexProfile.MediumThreshold = cmtVal;
        if (Environment.GetEnvironmentVariable("CODEX_HIGH_THRESHOLD") is { Length: > 0 } codexHighThresh
            && int.TryParse(codexHighThresh, out var chtVal))
            codexProfile.HighThreshold = chtVal;

        if (Environment.GetEnvironmentVariable("CODEX_LOW_MULTIPLIER") is { Length: > 0 } codexLowMult
            && double.TryParse(codexLowMult, out var clmVal))
            codexProfile.LowMultiplier = clmVal;
        if (Environment.GetEnvironmentVariable("CODEX_MEDIUM_MULTIPLIER") is { Length: > 0 } codexMedMult
            && double.TryParse(codexMedMult, out var cmmVal))
            codexProfile.MediumMultiplier = cmmVal;
        if (Environment.GetEnvironmentVariable("CODEX_HIGH_MULTIPLIER") is { Length: > 0 } codexHighMult
            && double.TryParse(codexHighMult, out var chmVal))
            codexProfile.HighMultiplier = chmVal;

        if (Environment.GetEnvironmentVariable("CODEX_MIN_OUTPUT_TOKENS") is { Length: > 0 } codexMinTokens
            && int.TryParse(codexMinTokens, out var cminVal))
            codexProfile.MinOutputTokens = cminVal;
        if (Environment.GetEnvironmentVariable("CODEX_MAX_OUTPUT_TOKENS") is { Length: > 0 } codexMaxTokens
            && int.TryParse(codexMaxTokens, out var cmaxVal))
            codexProfile.MaxOutputTokens = cmaxVal;

        if (Environment.GetEnvironmentVariable("CODEX_TIMEOUT_SECONDS") is { Length: > 0 } codexTimeout
            && int.TryParse(codexTimeout, out var ctVal))
            codexProfile.TimeoutSeconds = ctVal;
        if (Environment.GetEnvironmentVariable("CODEX_TOKENS_PER_MINUTE") is { Length: > 0 } codexTpm
            && int.TryParse(codexTpm, out var ctpmVal))
            codexProfile.TokensPerMinute = ctpmVal;
        if (Environment.GetEnvironmentVariable("CODEX_REQUESTS_PER_MINUTE") is { Length: > 0 } codexRpm
            && int.TryParse(codexRpm, out var crpmVal))
            codexProfile.RequestsPerMinute = crpmVal;

        if (Environment.GetEnvironmentVariable("CODEX_PIC_DENSITY_FLOOR") is { Length: > 0 } codexPicFloor
            && double.TryParse(codexPicFloor, out var cpfVal))
            codexProfile.PicDensityFloor = cpfVal;
        if (Environment.GetEnvironmentVariable("CODEX_LEVEL_DENSITY_FLOOR") is { Length: > 0 } codexLevelFloor
            && double.TryParse(codexLevelFloor, out var clfVal))
            codexProfile.LevelDensityFloor = clfVal;

        if (Environment.GetEnvironmentVariable("CODEX_ENABLE_AMPLIFIERS") is { Length: > 0 } codexAmps
            && bool.TryParse(codexAmps, out var caVal))
            codexProfile.EnableAmplifiers = caVal;

        if (Environment.GetEnvironmentVariable("CODEX_EXHAUSTION_MAX_RETRIES") is { Length: > 0 } codexExRetries
            && int.TryParse(codexExRetries, out var cerVal))
            codexProfile.ReasoningExhaustionMaxRetries = cerVal;
        if (Environment.GetEnvironmentVariable("CODEX_EXHAUSTION_RETRY_MULTIPLIER") is { Length: > 0 } codexExMult
            && double.TryParse(codexExMult, out var cemVal))
            codexProfile.ReasoningExhaustionRetryMultiplier = cemVal;

        // ── Speed-profile overrides for ChunkingSettings ────────────────────
        var chunkingSettings = settings.ChunkingSettings ??= new ChunkingSettings();

        if (Environment.GetEnvironmentVariable("CODEX_STAGGER_DELAY_MS") is { Length: > 0 } staggerMs
            && int.TryParse(staggerMs, out var sVal))
            chunkingSettings.ParallelStaggerDelayMs = sVal;
        if (Environment.GetEnvironmentVariable("CODEX_MAX_PARALLEL_CONVERSION") is { Length: > 0 } maxParConv
            && int.TryParse(maxParConv, out var mpcVal))
            chunkingSettings.MaxParallelConversion = mpcVal;
        if (Environment.GetEnvironmentVariable("CODEX_RATE_LIMIT_SAFETY_FACTOR") is { Length: > 0 } safetyFactor
            && double.TryParse(safetyFactor, out var sfVal))
            chunkingSettings.RateLimitSafetyFactor = sfVal;

        // Chat Profile overrides (subset — chat profiles are simpler)
        var chatProfile = settings.ChatProfile ??= new ModelProfileSettings();

        if (Environment.GetEnvironmentVariable("CHAT_TIMEOUT_SECONDS") is { Length: > 0 } chatTimeout
            && int.TryParse(chatTimeout, out var chatTVal))
            chatProfile.TimeoutSeconds = chatTVal;
        if (Environment.GetEnvironmentVariable("CHAT_TOKENS_PER_MINUTE") is { Length: > 0 } chatTpm
            && int.TryParse(chatTpm, out var chatTpmVal))
            chatProfile.TokensPerMinute = chatTpmVal;
        if (Environment.GetEnvironmentVariable("CHAT_MIN_OUTPUT_TOKENS") is { Length: > 0 } chatMinTokens
            && int.TryParse(chatMinTokens, out var chatMinVal))
            chatProfile.MinOutputTokens = chatMinVal;
        if (Environment.GetEnvironmentVariable("CHAT_MAX_OUTPUT_TOKENS") is { Length: > 0 } chatMaxTokens
            && int.TryParse(chatMaxTokens, out var chatMaxVal))
            chatProfile.MaxOutputTokens = chatMaxVal;
    }

    private static bool ValidateAndLoadConfiguration()
    {
        try
        {
            LoadEnvironmentVariables();

            var requiredSettings = new Dictionary<string, string?>
            {
                ["AZURE_OPENAI_ENDPOINT"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
                ["AZURE_OPENAI_DEPLOYMENT_NAME"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME"),
                ["AZURE_OPENAI_MODEL_ID"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL_ID")
            };

            // API Key is optional if using Entra ID / DefaultAzureCredential
            var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKey) && !apiKey.Contains("your-api-key"))
            {
                 // Key provided and looks valid-ish
            }
            else
            {
                // No key or template key - assuming Entra ID
                Console.WriteLine("ℹ️  No valid API Key found. Assuming Microsoft Entra ID (DefaultAzureCredential) authentication.");
            }

            var missingSettings = new List<string>();
            var invalidSettings = new List<string>();

            foreach (var setting in requiredSettings)
            {
                if (string.IsNullOrWhiteSpace(setting.Value))
                {
                    missingSettings.Add(setting.Key);
                }
                else
                {
                    if (setting.Key == "AZURE_OPENAI_ENDPOINT" && !Uri.TryCreate(setting.Value, UriKind.Absolute, out _))
                    {
                        invalidSettings.Add($"{setting.Key} (invalid URL format)");
                    }
                    else if (setting.Key == "AZURE_OPENAI_API_KEY" && setting.Value.Contains("your-api-key"))
                    {
                        invalidSettings.Add($"{setting.Key} (contains template placeholder)");
                    }
                    else if (setting.Key == "AZURE_OPENAI_ENDPOINT" && setting.Value.Contains("your-resource"))
                    {
                        invalidSettings.Add($"{setting.Key} (contains template placeholder)");
                    }
                }
            }

            if (missingSettings.Any() || invalidSettings.Any())
            {
                Console.WriteLine("❌ Configuration Validation Failed");
                Console.WriteLine("=====================================");

                if (missingSettings.Any())
                {
                    Console.WriteLine("Missing required settings:");
                    foreach (var setting in missingSettings)
                    {
                        Console.WriteLine($"  • {setting}");
                    }

                    Console.WriteLine();
                }

                if (invalidSettings.Any())
                {
                    Console.WriteLine("Invalid settings detected:");
                    foreach (var setting in invalidSettings)
                    {
                        Console.WriteLine($"  • {setting}");
                    }

                    Console.WriteLine();
                }

                Console.WriteLine("Configuration Setup Instructions:");
                Console.WriteLine("1. Run: ./setup.sh (for interactive setup)");
                Console.WriteLine("2. Or manually copy Config/ai-config.local.env.example to Config/ai-config.local.env");
                Console.WriteLine("3. Edit Config/ai-config.local.env with your actual Azure OpenAI credentials");
                Console.WriteLine("4. Ensure your model deployment names match your Azure OpenAI setup");
                Console.WriteLine();
                Console.WriteLine("For detailed instructions, see: CONFIGURATION_GUIDE.md");

                return false;
            }

            var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            var modelId = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL_ID");
            var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME");
            // apiKey retrieved above

            Console.WriteLine("✅ Configuration Validation Successful (Agent Framework)");
            Console.WriteLine("=====================================");
            Console.WriteLine($"Endpoint: {endpoint}");
            Console.WriteLine($"Model: {modelId}");
            Console.WriteLine($"Deployment: {deployment}");
            Console.WriteLine($"API Key: {apiKey?.Substring(0, Math.Min(8, apiKey.Length))}... ({apiKey?.Length} chars)");
            Console.WriteLine();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error during configuration validation: {ex.Message}");
            Console.WriteLine("Please check your configuration files and try again.");
            return false;
        }
    }

    private static async Task RunReverseEngineeringAsync(ILoggerFactory loggerFactory, FileHelper fileHelper, SettingsHelper settingsHelper, string cobolSource, string output, string configPath)
    {
        var logger = loggerFactory.CreateLogger("ReverseEngineering");

        try
        {
            logger.LogInformation("Loading settings from {ConfigPath}", configPath);
            AppSettings? loadedSettings = await settingsHelper.LoadSettingsAsync<AppSettings>(configPath);
            var settings = loadedSettings ?? new AppSettings();

            LoadEnvironmentVariables();
            OverrideSettingsFromEnvironment(settings);

            // Initialize Repositories
            var databasePath = settings.ApplicationSettings.MigrationDatabasePath ?? "Data/migration.db";
            if (!Path.IsPathRooted(databasePath))
            {
                databasePath = Path.GetFullPath(databasePath);
            }

            var repositoryLogger = loggerFactory.CreateLogger<SqliteMigrationRepository>();
            var migrationRepository = new SqliteMigrationRepository(databasePath, repositoryLogger);
            await migrationRepository.InitializeAsync();

            // Override with CLI arguments
            if (!string.IsNullOrEmpty(cobolSource))
            {
                settings.ApplicationSettings.CobolSourceFolder = cobolSource;
            }

            if (string.IsNullOrEmpty(settings.ApplicationSettings.CobolSourceFolder))
            {
                logger.LogError("COBOL source folder not specified. Use --source option.");
                Environment.Exit(1);
            }

            // Validate required AI configuration (relaxed for non-Azure providers)
            if (IsAzureOpenAIMode(settings.AISettings))
            {
                if (string.IsNullOrEmpty(settings.AISettings.Endpoint) ||
                    string.IsNullOrEmpty(settings.AISettings.DeploymentName))
                {
                    logger.LogError("Azure OpenAI configuration incomplete. Please ensure endpoint and deployment name are configured.");
                    Environment.Exit(1);
                }
            }
            else if (IsAnthropicMode(settings.AISettings))
            {
                if (string.IsNullOrEmpty(settings.AISettings.ApiKey))
                {
                    logger.LogError("Anthropic configuration incomplete. Please ensure API key is configured.");
                    logger.LogError("Set AISETTINGS__APIKEY in Config/ai-config.local.env or as an environment variable.");
                    Environment.Exit(1);
                }
            }

            // Create EnhancedLogger and ChatLogger early for API tracking
            var enhancedLogger = new EnhancedLogger(
                loggerFactory.CreateLogger<EnhancedLogger>());
            var providerName = GetProviderName(settings.AISettings);
            var chatLogger = new ChatLogger(
                loggerFactory.CreateLogger<ChatLogger>(), providerName: providerName);

            // Get API version from environment or default
            var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2025-04-01-preview";

            // Create ResponsesApiClient for code agents (Azure-only, null in non-Azure modes)
            ResponsesApiClient? responsesApiClient = null;
            if (IsAzureOpenAIMode(settings.AISettings))
            {
                responsesApiClient = new ResponsesApiClient(
                    settings.AISettings.Endpoint,
                    string.Empty, // Forces DefaultAzureCredential
                    settings.AISettings.DeploymentName,
                    loggerFactory.CreateLogger<ResponsesApiClient>(),
                    enhancedLogger,
                    profile: settings.CodexProfile,
                    apiVersion: apiVersion,
                    rateLimitSafetyFactor: settings.ChunkingSettings.RateLimitSafetyFactor);

                logger.LogInformation("ResponsesApiClient initialized for codex model: {DeploymentName} (API: {ApiVersion}, Entra ID)", 
                    settings.AISettings.DeploymentName, apiVersion);
            }

            var chatDeployment = settings.AISettings.ChatDeploymentName ?? settings.AISettings.DeploymentName;
            IChatClient chatClient = CreateChatClientFromSettings(settings.AISettings, logger, forChat: true);

            ConfigureSmartChunking(settings, chatDeployment, logger);

            // Create orchestrator early so agents can use it
            var targetLang = settings.ApplicationSettings.TargetLanguage;
            var chunkingOrchestrator = new CobolToQuarkusMigration.Chunking.ChunkingOrchestrator(
                settings.ChunkingSettings,
                settings.ConversionSettings,
                databasePath,
                loggerFactory.CreateLogger<CobolToQuarkusMigration.Chunking.ChunkingOrchestrator>(),
                targetLang);

            var cobolAnalyzerAgent = CobolAnalyzerAgent.Create(
                responsesApiClient, chatClient,
                loggerFactory.CreateLogger<CobolAnalyzerAgent>(),
                settings.AISettings.CobolAnalyzerModelId,
                enhancedLogger, chatLogger, settings: settings);

            var businessLogicExtractorAgent = BusinessLogicExtractorAgent.Create(
                responsesApiClient, chatClient,
                loggerFactory.CreateLogger<BusinessLogicExtractorAgent>(),
                chatDeployment,
                enhancedLogger, chatLogger,
                chunkingOrchestrator: chunkingOrchestrator, settings: settings);

            var dependencyMapperAgent = DependencyMapperAgent.Create(
                responsesApiClient, chatClient,
                loggerFactory.CreateLogger<DependencyMapperAgent>(),
                settings.AISettings.DependencyMapperModelId ?? settings.AISettings.CobolAnalyzerModelId,
                enhancedLogger, chatLogger, settings: settings);

            // Smart routing: check for large files to decide between chunked vs direct RE
            var cobolFiles = await fileHelper.ScanDirectoryForCobolFilesAsync(settings.ApplicationSettings.CobolSourceFolder);
            var hasLargeFiles = cobolFiles.Any(f => 
                settings.ChunkingSettings.RequiresChunking(f.Content.Length, f.Content.Split('\n').Length));

            ReverseEngineeringResult result;

            if (hasLargeFiles)
            {
                // Use ChunkedReverseEngineeringProcess for large files
                Console.WriteLine("📦 Large files detected - using smart chunked reverse engineering");
                logger.LogInformation("Large files detected, using ChunkedReverseEngineeringProcess");

                // chunkingOrchestrator created in outer scope

                var chunkedProcess = new ChunkedReverseEngineeringProcess(
                    cobolAnalyzerAgent,
                    businessLogicExtractorAgent,
                    dependencyMapperAgent,
                    fileHelper,
                    settings.ChunkingSettings,
                    chunkingOrchestrator,
                    loggerFactory.CreateLogger<ChunkedReverseEngineeringProcess>(),
                    enhancedLogger,
                    databasePath);

                result = await chunkedProcess.RunAsync(
                    settings.ApplicationSettings.CobolSourceFolder,
                    output,
                    (status, current, total) => Console.WriteLine($"{status} - {current}/{total}"));
            }
            else
            {
                // Use standard ReverseEngineeringProcess with agents
                var reverseEngineeringProcess = new ReverseEngineeringProcess(
                    cobolAnalyzerAgent,
                    businessLogicExtractorAgent,
                    dependencyMapperAgent,
                    fileHelper,
                    loggerFactory.CreateLogger<ReverseEngineeringProcess>(),
                    enhancedLogger,
                    migrationRepository);

                Console.WriteLine("Starting reverse engineering process...");
                Console.WriteLine();

                result = await reverseEngineeringProcess.RunAsync(
                    settings.ApplicationSettings.CobolSourceFolder,
                    output,
                    (status, current, total) =>
                    {
                        Console.WriteLine($"{status} - {current}/{total}");
                    });
            }

            Console.WriteLine();
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine("✨ Reverse Engineering Complete!");
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine();
            Console.WriteLine($"📊 Summary:");
            Console.WriteLine($"   • Files Analyzed: {result.TotalFilesAnalyzed}");
            Console.WriteLine($"   • Feature Descriptions: {result.TotalUserStories}");
            Console.WriteLine($"   • Features: {result.TotalFeatures}");
            Console.WriteLine($"   • Business Rules: {result.TotalBusinessRules}");
            Console.WriteLine();
            Console.WriteLine($"📁 Output Location: {Path.GetFullPath(output)}");
            Console.WriteLine("   • reverse-engineering-details.md - Complete analysis with business logic and technical details");
            Console.WriteLine();
            Console.WriteLine("🎯 Next Steps:");
            Console.WriteLine("   1. Review the generated documentation");
            Console.WriteLine("   2. Decide on your modernization strategy");
            Console.WriteLine("   3. Run full migration if desired: dotnet run --source <path> --java-output <path>");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during reverse engineering");
            Environment.Exit(1);
        }
    }
}
