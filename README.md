# JitTest — Catching Test Generator

LLM-driven ephemeral mutation testing for .NET projects. Automatically generates "catching tests" that are designed to fail on realistic mutants, helping detect regressions before code lands in production.

Inspired by [Meta's Catching JiTTest research](https://engineering.fb.com/2026/02/11/developer-tools/the-death-of-traditional-testing-agentic-development-jit-testing-revival/).

## Features

- 🔍 **Automatic diff extraction** from git (staged, uncommitted, or branch comparison)
- 🤖 **LLM-powered intent inference** to understand code changes
- 🧬 **Realistic mutant generation** using domain-specific knowledge
- ✅ **Self-correcting test generation** with compiler feedback
- 🎯 **False positive filtering** with LLM-based assessment
- 📊 **Console and markdown reporting**
- 🏠 **100% local** — runs on Ollama, no cloud dependencies

## Prerequisites

1. **.NET 10.0 SDK** or later
2. **Ollama** running locally with the model:
   ```bash
   ollama pull qwen2.5-coder:32b-instruct-q4_K_M
   ```

## Installation

### Install as Global Tool

```bash
cd AspireWithDapr.JiTTest
dotnet pack -c Release
dotnet tool install --global --add-source ./bin/Release JitTest
```

### Install as Local Tool

```bash
# In your project directory
dotnet new tool-manifest
dotnet tool install --local --add-source path/to/JitTest/bin/Release JitTest
```

## Usage

Navigate to your git repository and run:

```bash
# Test uncommitted changes
jittest --diff-source uncommitted

# Test staged changes
jittest --diff-source staged

# Compare against a branch
jittest --diff-source branch:main

# With verbose output (shows full test code and LLM details)
jittest --diff-source uncommitted --verbose

# Dry run (intent inference only)
jittest --diff-source uncommitted --dry-run
```

### Configuration

Create a `jittest-config.json` in your repository root:

```json
{
  "jittest-config": {
    "ollama-endpoint": "http://localhost:11434/v1",
    "model": "qwen2.5-coder:32b-instruct-q4_K_M",
    "diff-source": "uncommitted",
    "mutate-targets": [
      "**/*.cs"
    ],
    "exclude": [
      "**/Program.cs",
      "**/obj/**",
      "**/bin/**"
    ],
    "max-mutants-per-change": 5,
    "max-retries": 2,
    "confidence-threshold": "MEDIUM",
    "reporters": ["console"],
    "temp-directory": ".jittest-temp"
  }
}
```

## How It Works

1. **Extracts code changes** from your git repository
2. **Infers intent** using the LLM to understand what changed and why
3. **Generates realistic mutants** representing plausible bugs
4. **Creates tests** that pass on original code but fail on mutants
5. **Executes tests** to find "candidate catches"
6. **Assesses catches** to filter false positives
7. **Reports results** in console or markdown

All generated tests are ephemeral and stored in `.jittest-temp/` — never checked into source control.

## Exit Codes

- `0` — No regressions detected
- `1` — Potential regressions found (candidate catches)
- `2` — Configuration or connectivity error

## Uninstall

```bash
# Global tool
dotnet tool uninstall --global JitTest

# Local tool
dotnet tool uninstall JitTest
```

## License

MIT License
