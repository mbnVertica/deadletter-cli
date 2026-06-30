# DeadletterCLI — Agent Context

## What this project is

A single-file .NET 10 CLI tool for inspecting Azure Service Bus dead-letter queues.
It is **strictly read-only** — it never modifies, consumes, settles, or deletes any
message or Azure resource. Its sole purpose is to give fast, safe visibility into
dead-letter queue state across multiple named environments (e.g. dev, test, prod).

The tool is published as a single `dl.exe` on the user PATH and is
designed to be called by both humans and opencode agents.

---

## READ-ONLY GUARANTEE — Do not violate this

This is the most important constraint in the project. Every Azure Service Bus
operation performed is non-destructive:

| Operation | SDK method | Effect on queue state |
|---|---|---|
| Discover queues | `GetQueuesAsync()` | None |
| Discover topics | `GetTopicsAsync()` | None |
| Discover subscriptions | `GetSubscriptionsAsync()` | None |
| Read dead letters | `PeekMessagesAsync()` | **None** — no lock, no consume |

`PeekMessagesAsync` is the SDK's dedicated non-destructive read. It returns a
snapshot of messages without acquiring any lock or changing queue state under
any circumstances.

**The following methods must NEVER be added to this codebase:**
- `ReceiveMessageAsync` / `ReceiveMessagesAsync`
- `CompleteMessageAsync`
- `AbandonMessageAsync`
- `DeadLetterMessageAsync`
- `DeferMessageAsync`
- `SendMessageAsync` / `SendMessagesAsync`
- Any `ServiceBusAdministrationClient` write methods (Create, Update, Delete)

If a future requirement seems to need write access, stop and confirm with the
user before proceeding. The read-only guarantee is intentional and non-negotiable.

---

## Project structure

```
DeadletterCLI\
├── Program.cs              — All application logic. Single file by design.
├── DeadletterCLI.csproj    — net10.0-windows, PublishSingleFile=true
├── config.json.example     — Template showing config structure (no secrets)
├── opencode.json           — Blocks agent reads of config.json; allows exe
├── AGENTS.md               — This file
├── .gitignore              — Excludes bin/, obj/
└── [build output]
    bin\Release\net10.0-windows\...
    obj\...
```

### Published output (deployed location)

```
C:\Users\mbn\opencode_tools\bin\
├── dl.exe                  — Single-file published exe (on user PATH)
├── config.json             — Live environment config (managed by the tool)
├── config.json.example     — Template
└── opencode.json           — Permission rules for the bin\ directory
```

---

## Architecture

`Program.cs` contains all logic in a single file, organised into static classes:

| Class | Responsibility |
|---|---|
| `CredentialStore` | Read/write/delete credentials in Windows Credential Manager |
| `ConfigLoader` | Load, save, and resolve `config.json` |
| `Discoverer` | Auto-discover all queues and topic subscriptions via `ServiceBusAdministrationClient` |
| `DeadLetterPeeker` | Peek dead-letter sub-queues using `PeekMessagesAsync` |
| `Printer` | Human-readable console output |
| `JsonPrinter` | Structured JSON output for agent consumption |
| `Program` | Entry point — command dispatch |

---

## Security model

### Connection strings — Windows Credential Manager only

Connection strings are **never stored in any file**. They live exclusively in
Windows Credential Manager, keyed as `DeadletterCLI:<envName>` (e.g.
`DeadletterCLI:dev`). They are encrypted by Windows DPAPI and bound to the
current user account.

The tool sets up credentials interactively:
```powershell
dl store-credential dev
```

### `config.json` — non-sensitive

Contains only environment names. No connection strings, no queue names, no secrets.
The tool owns and manages this file — agents should not edit it directly.

```json
{
  "Environments": [
    { "Name": "dev" },
    { "Name": "prod" }
  ]
}
```

### opencode permission rules

Both `DeadletterCLI\opencode.json` and `bin\opencode.json` block agent reads of
`config.json` and allow the exe to run without approval prompts.

---

## CLI commands

### Setup
```powershell
# Add an environment and store its connection string (upserts config.json)
dl store-credential <name>

# Remove an environment and delete its credential
dl remove-environment <name>
```

### Inspection
```powershell
# List configured environments and credential status
dl list-environments

# Check dead letters in a specific environment (human-readable)
dl --env <name>

# Check dead letters — JSON output (for agent use)
dl --env <name> --format json

# Check ALL environments in one pass (human-readable)
dl check-all

# Check ALL environments — combined JSON output (for agent use)
dl check-all --format json
```

### Exit codes
| Code | Meaning |
|---|---|
| `0` | All queues and subscriptions are clean |
| `1` | Dead letters found, or one or more entities could not be checked |
| `2` | Config/startup error (bad args, missing credential, discovery failure) |

---

## How agents should use this tool

Agents have access to this tool via the global `deadletter-checker` skill at
`C:\Users\mbn\.agents\skills\deadletter-checker\SKILL.md`.

The skill instructs agents to:
1. If the user asks about a specific environment: call `--env <name> --format json`
2. If the user asks to check all environments or does not specify: call `check-all --format json`
3. Parse the JSON output and report findings — never attempt to fix, requeue, or delete anything

---

## How to rebuild and publish

After changing `Program.cs`:

```powershell
# Build (verify it compiles)
dotnet build DeadletterCLI.csproj -c Release

# Publish single-file exe to the deployed location
dotnet publish DeadletterCLI.csproj -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=false -o "C:\Users\mbn\opencode_tools\bin"
```

If the publish fails with "access denied" (exe is running), publish to temp first:
```powershell
dotnet publish DeadletterCLI.csproj -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=false -o "$env:TEMP\opencode\sbpublish"
Rename-Item "C:\Users\mbn\opencode_tools\bin\dl.exe" "dl.exe.old" -Force
Copy-Item "$env:TEMP\opencode\sbpublish\dl.exe" "C:\Users\mbn\opencode_tools\bin\dl.exe" -Force
Remove-Item "C:\Users\mbn\opencode_tools\bin\dl.exe.old" -Force
```

---

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| `Azure.Messaging.ServiceBus` | 7.20.1 | Peek dead-letter queues; discover entities |
| `Meziantou.Framework.Win32.CredentialManager` | 2.0.1 | Read/write Windows Credential Manager |

Target framework: `net10.0-windows` (Windows Credential Manager requires Windows APIs).

---

## What this tool intentionally does NOT do

- Does not send, receive, complete, abandon, defer, or delete messages
- Does not create, update, or delete Service Bus entities (queues, topics, subscriptions)
- Does not expose connection strings in any file, environment variable, or output
- Does not have a default environment — users and agents must always specify `--env`
- Does not requeue or repair dead letters — investigation only
