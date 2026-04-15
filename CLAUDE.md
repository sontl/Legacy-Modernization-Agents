# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Does

AI-powered legacy code modernization framework that converts COBOL source code to Java (Quarkus) or C# (.NET). The pipeline has two phases:
1. **Reverse engineering** — extracts business logic (user stories, rules, features) from COBOL and persists it to SQLite
2. **Code conversion** — generates target code injected with the persisted business logic context

## Commands

```bash
# Run full migration (reverse engineer → convert → launch portal)
./doctor.sh run

# Individual phases
./doctor.sh reverse-eng          # Extract business logic only
./doctor.sh convert-only         # Convert only (uses already-persisted business logic)
./doctor.sh portal               # Launch web portal at http://localhost:5028

# Setup and validation
./doctor.sh setup                # Interactive configuration
./doctor.sh validate             # Check system requirements
./doctor.sh chunking-health      # Validate smart chunking infrastructure
./doctor.sh test                 # Run system validation

# .NET
dotnet build
dotnet test
dotnet run -- [options]

# Docker
docker-compose up -d neo4j       # Start Neo4j only
docker-compose up                # Full environment (portal + Neo4j)
```

## Architecture

### Project Layout

- **`Agents/`** — AI agent implementations
  - `Infrastructure/` — `ResponsesApiClient` (Responses API for Codex) and `ChatApiClient` (Chat Completions API)
  - `Interfaces/` — `ICobolAgent` and other contracts
  - `Prompts/` — Markdown-based system prompts loaded at runtime
  - Agent classes: `CobolAnalyzerAgent`, `BusinessLogicExtractorAgent`, `DependencyMapperAgent`, `JavaConverterAgent`, `CSharpConverterAgent`, chunk-aware variants
- **`Processes/`** — Orchestration workflows
  - `SmartMigrationOrchestrator` — Routes files: small → `MigrationProcess`, large → `ChunkedMigrationProcess`
  - `ReverseEngineeringProcess` / `ChunkedReverseEngineeringProcess`
  - `MigrationProcess` / `ChunkedMigrationProcess`
- **`Chunking/`** — Large-file handling (>150K chars or >3K lines)
  - `Core/ChunkingOrchestrator` — Splits at semantic COBOL boundaries (DIVISION > SECTION > paragraph)
  - 300-line context overlap between chunks; cross-chunk signature registry for consistency
  - Up to 6 parallel chunk workers
- **`Persistence/`** — `SqliteMigrationRepository`, `Neo4jMigrationRepository`, `HybridMigrationRepository`
- **`McpChatWeb/`** — ASP.NET Core web portal (Minimal APIs + SignalR-style live logs, dependency graph, Claude-powered Q&A)
- **`Models/`** — `AppSettings`, `CobolFile`, `BusinessLogic`, `MigrationRun`, etc.
- **`Helpers/`** — `FileHelper`, `TokenHelper`, `RateLimiter`, `EnhancedLogger`

### Dual-API Design

The project uses **two AI endpoints simultaneously** (Azure OpenAI mode):

| API | Used by | Model config key |
|-----|---------|-----------------|
| Azure OpenAI Responses API | All migration agents (code gen) | `ModelId` / `DeploymentName` |
| Azure OpenAI Chat Completions | Portal Q&A + RE reports | `ChatModelId` / `ChatDeploymentName` |

**Three providers** are supported via `AISettings.ServiceType`:

| ServiceType | Responses API | IChatClient | Auth |
|-------------|--------------|-------------|------|
| `AzureOpenAI` (default) | Yes (codex models) | Yes | API key or Entra ID (`az login`) |
| `GitHubCopilot` | No | Yes (Copilot SDK) | Copilot CLI (`gh auth`) |
| `Anthropic` | No | Yes (Anthropic SDK) | API key from console.anthropic.com |
| `ClaudeCode` | No | Yes (claude CLI) | Claude Code subscription (no API key) |

When a non-Azure provider is active, all agents fall back to `IChatClient` — the Responses API (and complexity-aware reasoning) is not available. Provider routing is controlled by `IsAzureOpenAIMode()`, `IsGitHubCopilotMode()`, `IsAnthropicMode()`, and `IsClaudeCodeMode()` helpers in `Program.cs`. The `ClaudeCode` provider wraps the `claude -p` CLI in `ClaudeCodeChatClient`, similar to how `CopilotChatClient` wraps the Copilot CLI.

### Complexity-Aware Reasoning

Agents score COBOL files using weighted pattern matching (EXEC SQL=3, EXEC CICS=4, EXEC DLI=4, etc.) to select reasoning effort (`low`/`medium`/`high`) and scale output token budgets. See `CodexProfile.ComplexityIndicators` in `Config/appsettings.json`.

Four speed profiles control the defaults: **TURBO**, **FAST**, **BALANCED** (default), **THOROUGH**.

### Persistence (SQLite + Neo4j)

- **SQLite** (`Data/migration.db`): `migration_runs`, `cobol_files`, `java_files`, `csharp_files`, `business_logic`, `chunk_metadata`, `signatures`, `type_mappings`
- **Neo4j** (Docker, bolt://localhost:7687): Dependency graph for visualization in the portal

## Configuration

Primary config: `Config/appsettings.json`  
AI credentials: `Config/ai-config.env` (copy from `Config/ai-config.env.example`)

Key settings to tune:
- `AISettings.TokensPerMinute` → match your Azure OpenAI quota (check Model deployments → TPM)
- `ChunkingSettings.MaxParallelChunks` / `MaxParallelConversion` → tune for your TPM quota
- `ChunkingSettings.RateLimitSafetyFactor` → default 0.7 (reserves 30% headroom)
- `ApplicationSettings.TargetLanguage` → `"Java"` or `"CSharp"`
- `ApplicationSettings.CobolSourceFolder` → where COBOL `.cbl`/`.cob` files live

Leave `ApiKey` empty to use Azure AD/Entra ID (`az login`) instead of a key.

## Tech Stack

- **Language/Runtime:** C# / .NET 10.0
- **AI Framework:** Microsoft Agent Framework (`Microsoft.Agents.AI.*`, `Microsoft.Extensions.AI`)
- **AI Providers:** Azure OpenAI (`Azure.AI.OpenAI`), GitHub Copilot SDK (`GitHub.Copilot.SDK`), Anthropic Claude (`Anthropic`), Claude Code CLI (`claude -p`)
- **CLI:** `System.CommandLine` v2.0.0-beta
- **Web:** ASP.NET Core Minimal APIs
- **Databases:** SQLite (`Microsoft.Data.Sqlite`), Neo4j (`Neo4j.Driver`)
- **Auth:** `Azure.Identity` (DefaultAzureCredential)
- **Target outputs:** Java (Quarkus 3.6, Jakarta EE), C# (.NET 8+, ASP.NET Core)
- **Tests:** `CobolToQuarkusMigration.Tests/` (unit/integration), `McpChatWeb.Tests/`
