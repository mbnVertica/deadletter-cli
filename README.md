# DeadletterCLI (`dl`)

A read-only Azure Service Bus dead-letter queue inspector. Connects to one or more Service Bus namespaces, auto-discovers all queues and topic subscriptions, and peeks their dead-letter sub-queues to report any failed messages.

Designed to be called by both humans (colored console output) and AI agents (`--format json`).

> **Read-only guarantee:** This tool never modifies, consumes, locks, settles, or deletes any message or Azure resource. It uses `PeekMessagesAsync` exclusively — the SDK's non-destructive read primitive.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows)
- Windows (uses Windows Credential Manager to store connection strings)
- An Azure Service Bus connection string with the **Manage** claim (e.g. `RootManageSharedAccessKey`) — required for auto-discovery of queues and topics

---

## Build & Install

Clone the repo, then publish a single-file executable to a folder on your PATH:

```powershell
dotnet publish DeadletterCLI.csproj -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=false -o "C:\tools\dl"
```

Add `C:\tools\dl` to your PATH, then verify:

```powershell
dl -h
```

> If the publish fails with "access denied" because `dl.exe` is already running, publish to a temp folder first and then copy the exe over.

---

## Setup

Add an environment and store its connection string in Windows Credential Manager:

```powershell
dl store-credential dev
dl store-credential prod
```

The command will prompt you to paste the connection string. Input is masked and the value is stored encrypted via Windows DPAPI — it is never written to any file.

The tool manages a `config.json` alongside the exe that tracks environment names (no secrets). See `config.json.example` for the format.

To remove an environment:

```powershell
dl remove-environment dev
```

---

## Usage

### List configured environments

```powershell
dl list-environments
```

### Check a specific environment

```powershell
# Human-readable (colored console output)
dl --env prod

# Structured JSON (for scripts or agent use)
dl --env prod --format json
```

### Check all environments at once

```powershell
# Human-readable
dl check-all

# JSON
dl check-all --format json
```

### Help

```powershell
dl -h
```

---

## Example Output

### Human-readable (`dl --env dev`)

```
Discovering entities in 'dev'...
Discovered 5 entity/entities in acme-dev.servicebus.windows.net

[WARN] audit-log                    — 1 dead letter(s)

         ─────────────────────────────────────────────────
         #1   Seq:1048  Enqueued:2026-06-28 09:14:52
               Reason: MaxDeliveryCountExceeded
               Error:  Object reference not set to an instance of an object.
         ─────────────────────────────────────────────────

[ERR]  events > inventory-service   — The messaging entity 'acme-dev/subscriptions/inventory-service'
                                      could not be found. HTTP status code: NotFound.
[OK]   notifications                — 0 dead letters
[WARN] orders                       — 3 dead letter(s)

         ─────────────────────────────────────────────────
         3x   Seq range: 2201 – 2203  |  First: 2026-06-27 14:01  Last: 2026-06-27 14:09
               Reason: MaxDeliveryCountExceeded
               Error:  Failed to deserialize message body: unexpected token 'null' at path $.CustomerId.
         ─────────────────────────────────────────────────

[WARN] payments                     — 1 dead letter(s)

         ─────────────────────────────────────────────────
         #1   Seq:879   Enqueued:2026-06-29 22:47:03
               Reason: ProcessingFailed
               Error:  Timeout waiting for downstream service 'fraud-check' after 30000ms.
         ─────────────────────────────────────────────────


Summary: 1 entry/entries could not be checked — verify your connection string.
Summary: 3 entry/entries affected, 5 total dead letter(s).
```

### JSON (`dl --env dev --format json`)

```json
{
  "Environment": "dev",
  "Queues": [
    {
      "Name": "audit-log",
      "DeadLetterCount": 1,
      "Messages": [
        {
          "SequenceNumber": 1048,
          "Reason": "MaxDeliveryCountExceeded",
          "ErrorDescription": "Object reference not set to an instance of an object.\n   at Acme.Audit.AuditLogHandler.HandleAsync(ServiceBusReceivedMessage msg) in AuditLogHandler.cs:line 47\n   at Acme.Audit.Worker.ExecuteAsync(CancellationToken ct) in Worker.cs:line 82",
          "EnqueuedTime": "2026-06-28 09:14:52 +02:00"
        }
      ]
    },
    {
      "Name": "events > inventory-service",
      "DeadLetterCount": 0,
      "CheckError": "The messaging entity 'acme-dev/subscriptions/inventory-service' could not be found. HTTP status code: NotFound.",
      "Messages": []
    },
    {
      "Name": "notifications",
      "DeadLetterCount": 0,
      "Messages": []
    },
    {
      "Name": "orders",
      "DeadLetterCount": 3,
      "Messages": [
        {
          "SequenceNumber": 2201,
          "Reason": "MaxDeliveryCountExceeded",
          "ErrorDescription": "Failed to deserialize message body: unexpected token 'null' at path $.CustomerId.\n   at Acme.Orders.Serialization.MessageDeserializer.Deserialize(BinaryData body) in MessageDeserializer.cs:line 31\n   at Acme.Orders.OrderHandler.HandleAsync(ServiceBusReceivedMessage msg) in OrderHandler.cs:line 58",
          "EnqueuedTime": "2026-06-27 14:01:33 +02:00"
        },
        {
          "SequenceNumber": 2202,
          "Reason": "MaxDeliveryCountExceeded",
          "ErrorDescription": "Failed to deserialize message body: unexpected token 'null' at path $.CustomerId.\n   at Acme.Orders.Serialization.MessageDeserializer.Deserialize(BinaryData body) in MessageDeserializer.cs:line 31\n   at Acme.Orders.OrderHandler.HandleAsync(ServiceBusReceivedMessage msg) in OrderHandler.cs:line 58",
          "EnqueuedTime": "2026-06-27 14:05:17 +02:00"
        },
        {
          "SequenceNumber": 2203,
          "Reason": "MaxDeliveryCountExceeded",
          "ErrorDescription": "Failed to deserialize message body: unexpected token 'null' at path $.CustomerId.\n   at Acme.Orders.Serialization.MessageDeserializer.Deserialize(BinaryData body) in MessageDeserializer.cs:line 31\n   at Acme.Orders.OrderHandler.HandleAsync(ServiceBusReceivedMessage msg) in OrderHandler.cs:line 58",
          "EnqueuedTime": "2026-06-27 14:09:02 +02:00"
        }
      ]
    },
    {
      "Name": "payments",
      "DeadLetterCount": 1,
      "Messages": [
        {
          "SequenceNumber": 879,
          "Reason": "ProcessingFailed",
          "ErrorDescription": "Timeout waiting for downstream service 'fraud-check' after 30000ms.\n   at Acme.Payments.FraudCheckClient.CheckAsync(PaymentMessage msg) in FraudCheckClient.cs:line 94\n   at Acme.Payments.PaymentHandler.HandleAsync(ServiceBusReceivedMessage msg) in PaymentHandler.cs:line 63",
          "EnqueuedTime": "2026-06-29 22:47:03 +02:00"
        }
      ]
    }
  ],
  "TotalDeadLetters": 5,
  "AffectedQueues": 3,
  "ErrorQueues": 1
}
```

---

## Exit Codes

| Code | Meaning |
|------|---------|
| `0`  | All queues and subscriptions are clean |
| `1`  | Dead letters found, or one or more entities could not be checked |
| `2`  | Configuration or startup error (bad args, missing credential, discovery failure) |

---

## JSON Output Shape

Single environment (`dl --env <name> --format json`):

```json
{
  "Environment": "prod",
  "Queues": [
    {
      "Name": "orders",
      "DeadLetterCount": 2,
      "Messages": [
        {
          "SequenceNumber": 42,
          "Reason": "MaxDeliveryCountExceeded",
          "ErrorDescription": "Value cannot be null. (Parameter 'source')\n   at Orders.MessageHandler.ProcessAsync(ServiceBusReceivedMessage msg)\n   at Azure.Messaging.ServiceBus.ServiceBusProcessor.ProcessMessageAsync()",
          "EnqueuedTime": "2025-06-01 08:32:11 +02:00"
        }
      ]
    }
  ],
  "TotalDeadLetters": 2,
  "AffectedQueues": 1,
  "ErrorQueues": 0
}
```

All environments (`dl check-all --format json`) wraps the above in an `Environments` array with aggregate totals.

---

## How It Works

- **Credentials** — Connection strings live exclusively in Windows Credential Manager, keyed as `DeadletterCLI:<envName>`. They are encrypted by Windows DPAPI and never appear in any file or output.
- **Auto-discovery** — On each run, `ServiceBusAdministrationClient` enumerates all queues, topics, and subscriptions in the namespace. No manual queue list needed.
- **Peeking** — `PeekMessagesAsync` reads dead-letter sub-queues non-destructively. No lock is acquired, no message state is changed.

---

## What This Tool Does NOT Do

- Does not receive, complete, abandon, defer, or delete messages
- Does not create, update, or delete Service Bus entities
- Does not expose connection strings in any file, environment variable, or output
- Does not requeue or repair dead letters — inspection only

---

## Dependencies

| Package | Version |
|---------|---------|
| `Azure.Messaging.ServiceBus` | 7.20.1 |
| `Meziantou.Framework.Win32.CredentialManager` | 2.0.1 |
