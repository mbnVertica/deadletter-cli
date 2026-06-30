// =============================================================================
// SECURITY GUARANTEE: This tool is STRICTLY READ-ONLY with respect to Azure.
//
// The only Azure Service Bus operations performed are:
//   - GetQueuesAsync()         — list queue names         (no modification)
//   - GetTopicsAsync()         — list topic names         (no modification)
//   - GetSubscriptionsAsync()  — list subscription names  (no modification)
//   - PeekMessagesAsync()      — peek dead-letter messages (no lock acquired,
//                                no consume, no settle, no abandon, no delete)
//
// No message is ever received, locked, completed, abandoned, deferred,
// dead-lettered, or deleted by this tool. This is enforced by using
// PeekMessagesAsync exclusively — it is the SDK's non-destructive read
// primitive and cannot modify queue state under any circumstances.
//
// DO NOT add ReceiveMessageAsync, CompleteMessageAsync, AbandonMessageAsync,
// DeadLetterMessageAsync, DeferMessageAsync, or any send/management write
// operations to this file. This tool must remain permanently read-only.
// =============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Meziantou.Framework.Win32;

// ---------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------

record SubscriptionEntry(string Topic, string Subscription);

record EnvironmentConfig(string Name);

record Config(EnvironmentConfig[] Environments);

record DeadLetterMessage(
    long SequenceNumber,
    string? DeadLetterReason,
    string? DeadLetterErrorDescription,
    string? MessageId,
    DateTimeOffset EnqueuedTime
);

record DeadLetterResult(string DisplayName, List<DeadLetterMessage> Messages, string? ErrorMessage);

// JSON output shapes
record JsonMessage(
    long SequenceNumber,
    string? Reason,
    string? ErrorDescription,
    string EnqueuedTime
);

record JsonQueue(
    string Name,
    int DeadLetterCount,
    string? CheckError,
    List<JsonMessage> Messages
);

record JsonOutput(
    string Environment,
    List<JsonQueue> Queues,
    int TotalDeadLetters,
    int AffectedQueues,
    int ErrorQueues
);

record JsonAllOutput(
    List<JsonOutput> Environments,
    int TotalDeadLetters,
    int AffectedEnvironments,
    int ErrorEnvironments
);

// ---------------------------------------------------------------------------
// Credential storage (Windows Credential Manager, keyed per environment)
// ---------------------------------------------------------------------------

static class CredentialStore
{
    private const string KeyPrefix = "DeadletterCLI:";

    private static string Key(string envName) => $"{KeyPrefix}{envName}";

    public static void RunStoreCommand(string envName)
    {
        var configPath = ConfigLoader.ConfigPath();
        var config     = ConfigLoader.LoadOrEmpty(configPath);

        // Upsert: add environment to config if not already present
        bool isNew = !config.Environments.Any(e =>
            e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase));

        if (isNew)
        {
            var updated = config.Environments.Append(new EnvironmentConfig(envName)).ToArray();
            ConfigLoader.Save(configPath, new Config(updated));
            Console.WriteLine($"Environment '{envName}' added to config.json.");
        }

        var key = Key(envName);
        Console.WriteLine($"DeadletterCLI — Store Connection String for '{envName}'");
        Console.WriteLine(new string('=', 52));
        Console.WriteLine("Paste your Azure Service Bus connection string below.");
        Console.WriteLine("Input is hidden and will be stored in Windows Credential Manager.");
        Console.WriteLine("Press Enter when done. Press Ctrl+C to cancel.");
        Console.WriteLine();
        Console.Write("Connection string: ");

        var value = ReadMasked();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(value))
        {
            Console.Error.WriteLine("ERROR: No connection string entered. Nothing was stored.");
            // Roll back config addition if we just added it
            if (isNew)
            {
                var rollback = config.Environments.Where(e =>
                    !e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase)).ToArray();
                ConfigLoader.Save(configPath, new Config(rollback));
                Console.Error.WriteLine($"Environment '{envName}' removed from config.json (rolled back).");
            }
            Environment.Exit(2);
        }

        if (!value.StartsWith("Endpoint=sb://", StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("WARNING: This does not look like a Service Bus connection string.");
            Console.WriteLine("         Expected format: Endpoint=sb://namespace.servicebus.windows.net/;...");
            Console.ResetColor();
            Console.Write("Store it anyway? [y/N]: ");
            var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (confirm != "y")
            {
                Console.WriteLine("Cancelled. Nothing was stored.");
                if (isNew)
                {
                    var rollback = config.Environments.Where(e =>
                        !e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase)).ToArray();
                    ConfigLoader.Save(configPath, new Config(rollback));
                    Console.WriteLine($"Environment '{envName}' removed from config.json (rolled back).");
                }
                Environment.Exit(0);
            }
        }

        try
        {
            CredentialManager.WriteCredential(key, envName, value, CredentialPersistence.LocalMachine);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Credential stored successfully under key '{key}'.");
            Console.ResetColor();
            Console.WriteLine("You can view or delete it in:");
            Console.WriteLine("  Control Panel > Credential Manager > Windows Credentials");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to store credential: {ex.Message}");
            Environment.Exit(2);
        }
    }

    public static string? Read(string envName)
    {
        try
        {
            return CredentialManager.ReadCredential(Key(envName))?.Password;
        }
        catch { return null; }
    }

    public static bool HasCredential(string envName) => Read(envName) is not null;

    public static void Delete(string envName)
    {
        try { CredentialManager.DeleteCredential(Key(envName)); }
        catch { /* already gone */ }
    }

    private static string ReadMasked()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Remove(sb.Length - 1, 1); }
            else if (k.KeyChar != '\0') sb.Append(k.KeyChar);
        }
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
// Configuration (config.json — no sensitive data)
// ---------------------------------------------------------------------------

static class ConfigLoader
{
    private const string ConfigFileName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented          = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ConfigPath()
        => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

    // Load or return an empty config if the file does not exist yet.
    public static Config LoadOrEmpty(string path)
    {
        if (!File.Exists(path))
            return new Config([]);

        try
        {
            var json = File.ReadAllText(path);
            var cfg  = JsonSerializer.Deserialize<Config>(json, JsonOptions);
            return cfg ?? new Config([]);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"ERROR: Could not parse {ConfigFileName}: {ex.Message}");
            Environment.Exit(2);
            return null!;
        }
    }

    // Load and require at least one environment to exist.
    public static Config Load(string path)
    {
        var config = LoadOrEmpty(path);

        if (config.Environments is not { Length: > 0 })
        {
            Console.Error.WriteLine("ERROR: No environments configured.");
            Console.Error.WriteLine("       Run 'dl store-credential <name>' to add one.");
            Environment.Exit(2);
        }

        return config;
    }

    public static void Save(string path, Config config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }

    // Resolve a named environment and its connection string, or exit with a clear error.
    public static (EnvironmentConfig env, string connectionString) Resolve(
        Config config, string envName)
    {
        var env = config.Environments.FirstOrDefault(e =>
            e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase));

        if (env is null)
        {
            Console.Error.WriteLine($"ERROR: Environment '{envName}' is not configured.");
            Console.Error.WriteLine( "       Run 'dl store-credential <name>' to add it.");
            Console.Error.WriteLine( "       Run 'dl list-environments' to see what is available.");
            Environment.Exit(2);
        }

        var connectionString = CredentialStore.Read(envName);
        if (connectionString is null)
        {
            Console.Error.WriteLine($"ERROR: No credential found for environment '{envName}'.");
            Console.Error.WriteLine($"       Run 'dl store-credential {envName}' to set it up.");
            Environment.Exit(2);
        }

        return (env!, connectionString!);
    }
}

// ---------------------------------------------------------------------------
// Auto-discovery (requires Manage claim on the connection string)
// ---------------------------------------------------------------------------

static class Discoverer
{
    public static async Task<(string[] Queues, SubscriptionEntry[] Subscriptions)> DiscoverAsync(
        string connectionString, string envName, bool jsonMode)
    {
        ServiceBusAdministrationClient adminClient;
        try
        {
            adminClient = new ServiceBusAdministrationClient(connectionString);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Could not create administration client: {ex.Message}");
            Environment.Exit(2);
            return default!;
        }

        if (!jsonMode)
            Console.Write($"Discovering entities in '{envName}'...");

        var queues        = new List<string>();
        var subscriptions = new List<SubscriptionEntry>();

        try
        {
            await foreach (var q in adminClient.GetQueuesAsync())
                queues.Add(q.Name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("ERROR: Auto-discovery of queues failed.");
            Console.Error.WriteLine("       Ensure your connection string has the Manage claim (e.g. RootManageSharedAccessKey).");
            Console.Error.WriteLine($"       Details: {ex.Message}");
            Environment.Exit(2);
        }

        try
        {
            await foreach (var topic in adminClient.GetTopicsAsync())
                await foreach (var sub in adminClient.GetSubscriptionsAsync(topic.Name))
                    subscriptions.Add(new SubscriptionEntry(topic.Name, sub.SubscriptionName));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("ERROR: Auto-discovery of topics/subscriptions failed.");
            Console.Error.WriteLine("       Ensure your connection string has the Manage claim (e.g. RootManageSharedAccessKey).");
            Console.Error.WriteLine($"       Details: {ex.Message}");
            Environment.Exit(2);
        }

        if (!jsonMode)
        {
            var ns = ExtractNamespace(connectionString);
            Console.WriteLine();
            Console.WriteLine($"Discovered {queues.Count + subscriptions.Count} entity/entities in {ns}");
            Console.WriteLine();
        }

        return (queues.ToArray(), subscriptions.ToArray());
    }

    private static string ExtractNamespace(string connectionString)
    {
        const string prefix = "Endpoint=sb://";
        var start = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "unknown namespace";
        start += prefix.Length;
        var end = connectionString.IndexOf('/', start);
        return end < 0 ? connectionString[start..] : connectionString[start..end];
    }
}

// ---------------------------------------------------------------------------
// Dead-letter peek (read-only — PeekMessagesAsync never consumes messages)
// ---------------------------------------------------------------------------

static class DeadLetterPeeker
{
    // PeekMessagesAsync is a non-destructive read — it returns a snapshot of messages
    // without acquiring any lock or changing queue state in any way.
    // This is the ONLY receive-side method used in this codebase.
    private static async Task<List<DeadLetterMessage>> PeekAllAsync(ServiceBusReceiver receiver)
    {
        var messages = new List<DeadLetterMessage>();
        long? fromSeq = null;
        while (true)
        {
            var batch = fromSeq.HasValue
                ? await receiver.PeekMessagesAsync(100, fromSeq.Value)
                : await receiver.PeekMessagesAsync(100);

            if (batch.Count == 0) break;

            foreach (var msg in batch)
            {
                messages.Add(new DeadLetterMessage(
                    msg.SequenceNumber,
                    msg.DeadLetterReason,
                    msg.DeadLetterErrorDescription,
                    msg.MessageId,
                    msg.EnqueuedTime
                ));
            }

            fromSeq = batch[^1].SequenceNumber + 1;
        }
        return messages;
    }

    public static async Task<DeadLetterResult> PeekQueueAsync(ServiceBusClient client, string queueName)
    {
        try
        {
            // READ-ONLY: PeekMessagesAsync never locks, consumes, or settles messages.
            // ReceiveMode.PeekLock is the default and is irrelevant for peek operations —
            // no lock is acquired. It is kept here for explicitness only.
            await using var receiver = client.CreateReceiver(queueName, new ServiceBusReceiverOptions
            {
                SubQueue    = SubQueue.DeadLetter,
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });
            return new DeadLetterResult(queueName, await PeekAllAsync(receiver), null);
        }
        catch (Exception ex)
        {
            return new DeadLetterResult(queueName, [], ex.Message);
        }
    }

    public static async Task<DeadLetterResult> PeekSubscriptionAsync(
        ServiceBusClient client, SubscriptionEntry entry)
    {
        var displayName = $"{entry.Topic} > {entry.Subscription}";
        try
        {
            // READ-ONLY: same guarantee as PeekQueueAsync — no lock, no consume, no settle.
            await using var receiver = client.CreateReceiver(
                entry.Topic, entry.Subscription, new ServiceBusReceiverOptions
                {
                    SubQueue    = SubQueue.DeadLetter,
                    ReceiveMode = ServiceBusReceiveMode.PeekLock
                });
            return new DeadLetterResult(displayName, await PeekAllAsync(receiver), null);
        }
        catch (Exception ex)
        {
            return new DeadLetterResult(displayName, [], ex.Message);
        }
    }
}

// ---------------------------------------------------------------------------
// Output — human-readable
// ---------------------------------------------------------------------------

static class Printer
{
    private const string ErrorIndent = "                        "; // 24 chars, aligns under error text
    private const int    WrapWidth   = 80;                         // usable columns before wrapping

    private static void WriteWrappedError(string text)
    {
        // First line: label already written by caller, so we just fill the remaining width.
        // Subsequent lines: indented to align under the first character of the error text.
        int firstLineWidth = WrapWidth - ErrorIndent.Length;
        if (text.Length <= firstLineWidth)
        {
            Console.WriteLine(text);
            return;
        }

        // Word-wrap: break on spaces where possible.
        var words    = text.Split(' ');
        var line     = new System.Text.StringBuilder();
        bool isFirst = true;
        int  limit   = firstLineWidth;

        foreach (var word in words)
        {
            if (line.Length == 0)
            {
                line.Append(word);
            }
            else if (line.Length + 1 + word.Length <= limit)
            {
                line.Append(' ').Append(word);
            }
            else
            {
                Console.WriteLine(line.ToString());
                line.Clear().Append(isFirst ? ErrorIndent : "").Append(word);
                if (isFirst) { isFirst = false; limit = WrapWidth; }
                else         { line.Clear().Append(ErrorIndent).Append(word); limit = WrapWidth; }
            }
        }
        if (line.Length > 0) Console.WriteLine(line.ToString());
    }

    public static void PrintResult(DeadLetterResult result, int nameWidth)
    {
        var name = result.DisplayName.PadRight(nameWidth);

        if (result.ErrorMessage is not null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"[ERR]  {name}");
            Console.ResetColor();
            Console.WriteLine($" — {result.ErrorMessage}");
            return;
        }

        if (result.Messages.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("[OK]   ");
            Console.ResetColor();
            Console.WriteLine($"{name} — 0 dead letters");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("[WARN] ");
        Console.ResetColor();
        Console.WriteLine($"{name} — {result.Messages.Count} dead letter(s)");

        // Group by identical (Reason, first line of ErrorDescription)
        var groups = result.Messages
            .GroupBy(m => (
                Reason: m.DeadLetterReason ?? "",
                Error:  m.DeadLetterErrorDescription?.Split('\n')[0].Trim() ?? ""
            ))
            .ToList();

        var displayed = groups.Take(10).ToList();
        int hidden    = groups.Count - displayed.Count;

        Console.WriteLine();
        for (int i = 0; i < displayed.Count; i++)
        {
            var group   = displayed[i];
            var msgs    = group.ToList();
            var reason  = group.Key.Reason is { Length: > 0 } r ? r : "(no reason)";
            var errLine = group.Key.Error  is { Length: > 0 } e ? e : null;

            Console.WriteLine("         ─────────────────────────────────────────────────");

            if (msgs.Count == 1)
            {
                var m        = msgs[0];
                var enqueued = m.EnqueuedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"         #{i + 1,-4} Seq:{m.SequenceNumber}  Enqueued:{enqueued}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"               Reason: {reason}");
                if (errLine is not null)
                {
                    Console.Write("               Error:  ");
                    Console.ForegroundColor = ConsoleColor.White;
                    WriteWrappedError(errLine);
                }
                Console.ResetColor();
            }
            else
            {
                var ordered  = msgs.OrderBy(m => m.SequenceNumber).ToList();
                var firstSeq = ordered.First().SequenceNumber;
                var lastSeq  = ordered.Last().SequenceNumber;
                var firstEnq = ordered.First().EnqueuedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                var lastEnq  = ordered.Last().EnqueuedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"         {msgs.Count}x   Seq range: {firstSeq} – {lastSeq}  |  First: {firstEnq}  Last: {lastEnq}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"               Reason: {reason}");
                if (errLine is not null)
                {
                    Console.Write("               Error:  ");
                    Console.ForegroundColor = ConsoleColor.White;
                    WriteWrappedError(errLine);
                }
                Console.ResetColor();
            }
        }

        if (hidden > 0)
        {
            Console.WriteLine("         ─────────────────────────────────────────────────");
            Console.WriteLine($"         ... and {hidden} more group(s) not shown");
        }

        Console.WriteLine("         ─────────────────────────────────────────────────");
        Console.WriteLine();
    }

    public static void PrintSummary(int totalDead, int affected, int errors)
    {
        Console.WriteLine();
        if (errors > 0)
            Console.WriteLine($"Summary: {errors} entry/entries could not be checked — verify your connection string.");

        if (totalDead == 0 && errors == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Summary: All queues and subscriptions are clean.");
            Console.ResetColor();
        }
        else if (totalDead > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Summary: {affected} entry/entries affected, {totalDead} total dead letter(s).");
            Console.ResetColor();
        }
    }
}

// ---------------------------------------------------------------------------
// Output — JSON
// ---------------------------------------------------------------------------

static class JsonPrinter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Print(string envName, DeadLetterResult[] results)
    {
        var queues = results.Select(r => new JsonQueue(
            r.DisplayName,
            r.Messages.Count,
            r.ErrorMessage,
            r.Messages.Select(m => new JsonMessage(
                m.SequenceNumber,
                m.DeadLetterReason,
                m.DeadLetterErrorDescription,
                m.EnqueuedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            )).ToList()
        )).ToList();

        Console.WriteLine(JsonSerializer.Serialize(new JsonOutput(
            envName,
            queues,
            results.Sum(r => r.Messages.Count),
            results.Count(r => r.Messages.Count > 0),
            results.Count(r => r.ErrorMessage is not null)
        ), Options));
    }

    public static JsonOutput BuildOutput(string envName, DeadLetterResult[] results)
    {
        var queues = results.Select(r => new JsonQueue(
            r.DisplayName,
            r.Messages.Count,
            r.ErrorMessage,
            r.Messages.Select(m => new JsonMessage(
                m.SequenceNumber,
                m.DeadLetterReason,
                m.DeadLetterErrorDescription,
                m.EnqueuedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            )).ToList()
        )).ToList();

        return new JsonOutput(
            envName,
            queues,
            results.Sum(r => r.Messages.Count),
            results.Count(r => r.Messages.Count > 0),
            results.Count(r => r.ErrorMessage is not null)
        );
    }

    public static void PrintAll(List<JsonOutput> envOutputs)
    {
        var all = new JsonAllOutput(
            envOutputs,
            envOutputs.Sum(e => e.TotalDeadLetters),
            envOutputs.Count(e => e.AffectedQueues > 0),
            envOutputs.Count(e => e.ErrorQueues > 0)
        );
        Console.WriteLine(JsonSerializer.Serialize(all, Options));
    }
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var configPath = ConfigLoader.ConfigPath();

        // ---- -h / --help ---------------------------------------------------
        if (args.Length == 0 ||
            args[0].Equals("-h",     StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("help",   StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("dl — Azure Service Bus dead-letter inspector");
            Console.WriteLine();
            Console.WriteLine("USAGE");
            Console.WriteLine("  dl --env <name> [--format json]   Check dead letters in a specific environment");
            Console.WriteLine("  dl check-all [--format json]      Check all configured environments");
            Console.WriteLine("  dl list-environments              List environments and credential status");
            Console.WriteLine("  dl store-credential <name>        Add an environment and store its connection string");
            Console.WriteLine("  dl remove-environment <name>      Remove an environment and delete its credential");
            Console.WriteLine("  dl -h                             Show this help");
            Console.WriteLine();
            Console.WriteLine("OPTIONS");
            Console.WriteLine("  --format json   Output structured JSON instead of human-readable text");
            Console.WriteLine();
            Console.WriteLine("EXIT CODES");
            Console.WriteLine("  0   All queues clean — no dead letters");
            Console.WriteLine("  1   Dead letters found, or one or more queues could not be checked");
            Console.WriteLine("  2   Configuration or startup error");
            return 0;
        }

        // ---- store-credential <env> ----------------------------------------
        if (args.Length > 0 && args[0].Equals("store-credential", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("ERROR: Usage: dl store-credential <environment-name>");
                return 2;
            }
            CredentialStore.RunStoreCommand(args[1]);
            return 0;
        }

        // ---- remove-environment <env> --------------------------------------
        if (args.Length > 0 && args[0].Equals("remove-environment", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("ERROR: Usage: dl remove-environment <environment-name>");
                return 2;
            }
            var removeEnvName = args[1];
            var config        = ConfigLoader.LoadOrEmpty(configPath);
            var exists        = config.Environments.Any(e =>
                e.Name.Equals(removeEnvName, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                Console.Error.WriteLine($"ERROR: Environment '{removeEnvName}' is not configured.");
                return 2;
            }

            var remaining = config.Environments
                .Where(e => !e.Name.Equals(removeEnvName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ConfigLoader.Save(configPath, new Config(remaining));
            CredentialStore.Delete(removeEnvName);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Environment '{removeEnvName}' removed from config.json and credential deleted.");
            Console.ResetColor();
            return 0;
        }

        // ---- check-all [--format json] ------------------------------------
        if (args.Length > 0 && args[0].Equals("check-all", StringComparison.OrdinalIgnoreCase))
        {
            bool allJsonMode = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
            if (!allJsonMode)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i].Equals("--format", StringComparison.OrdinalIgnoreCase) &&
                        args[i + 1].Equals("json",   StringComparison.OrdinalIgnoreCase))
                    { allJsonMode = true; break; }
                }
            }

            var allConfig = ConfigLoader.Load(configPath);
            var envOutputs = new List<JsonOutput>();
            int overallExit = 0;

            foreach (var envEntry in allConfig.Environments)
            {
                var cs = CredentialStore.Read(envEntry.Name);
                if (cs is null)
                {
                    if (!allJsonMode)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[SKIP] {envEntry.Name} — no credential stored, skipping.");
                        Console.ResetColor();
                    }
                    else
                    {
                        // Include a skipped environment in JSON with an error marker
                        envOutputs.Add(new JsonOutput(
                            envEntry.Name,
                            [],
                            0, 0, 1
                        ));
                    }
                    overallExit = 1;
                    continue;
                }

                if (!allJsonMode)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"--- {envEntry.Name} ---");
                    Console.ResetColor();
                }

                var (envQueues, envSubs) = await Discoverer.DiscoverAsync(cs, envEntry.Name, allJsonMode);

                if (envQueues.Length == 0 && envSubs.Length == 0)
                {
                    if (!allJsonMode)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  Namespace is empty — no queues or subscriptions found.");
                        Console.ResetColor();
                    }
                    else
                    {
                        envOutputs.Add(new JsonOutput(envEntry.Name, [], 0, 0, 0));
                    }
                    continue;
                }

                await using var envClient = new ServiceBusClient(cs);
                var envResults = await Task.WhenAll(
                    envQueues.Select(q => DeadLetterPeeker.PeekQueueAsync(envClient, q))
                    .Concat(envSubs.Select(s => DeadLetterPeeker.PeekSubscriptionAsync(envClient, s)))
                );

                if (allJsonMode)
                {
                    envOutputs.Add(JsonPrinter.BuildOutput(envEntry.Name, envResults));
                }
                else
                {
                    int nw = envResults.Max(r => r.DisplayName.Length);
                    foreach (var result in envResults)
                        Printer.PrintResult(result, nw);

                    Printer.PrintSummary(
                        envResults.Sum(r => r.Messages.Count),
                        envResults.Count(r => r.Messages.Count > 0),
                        envResults.Count(r => r.ErrorMessage is not null)
                    );
                    Console.WriteLine();
                }

                if (envResults.Sum(r => r.Messages.Count) > 0 || envResults.Any(r => r.ErrorMessage is not null))
                    overallExit = 1;
            }

            if (allJsonMode)
                JsonPrinter.PrintAll(envOutputs);

            return overallExit;
        }

        // ---- list-environments ---------------------------------------------
        if (args.Length > 0 && args[0].Equals("list-environments", StringComparison.OrdinalIgnoreCase))        {
            var config = ConfigLoader.LoadOrEmpty(configPath);

            if (config.Environments.Length == 0)
            {
                Console.WriteLine("No environments configured.");
                Console.WriteLine("Run 'dl store-credential <name>' to add one.");
                return 0;
            }

            Console.WriteLine("Configured environments:");
            foreach (var envEntry in config.Environments)
            {
                var hasCred = CredentialStore.HasCredential(envEntry.Name);
                Console.Write($"  {envEntry.Name.PadRight(20)}");
                if (hasCred)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[credential: stored]");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[credential: MISSING — run 'dl store-credential {envEntry.Name}']");
                }
                Console.ResetColor();
            }
            return 0;
        }

        // ---- main check: --env <name> [--format json] ----------------------
        bool jsonMode = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        if (!jsonMode)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--format", StringComparison.OrdinalIgnoreCase) &&
                    args[i + 1].Equals("json",   StringComparison.OrdinalIgnoreCase))
                { jsonMode = true; break; }
            }
        }

        string? envName = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--env", StringComparison.OrdinalIgnoreCase))
            { envName = args[i + 1]; break; }
        }

        if (envName is null)
        {
            Console.Error.WriteLine("ERROR: No environment specified.");
            Console.Error.WriteLine("       Usage: dl --env <name> [--format json]");
            Console.Error.WriteLine("       Run 'dl list-environments' to see available environments.");
            return 2;
        }

        var cfg                        = ConfigLoader.Load(configPath);
        var (_, connectionString)      = ConfigLoader.Resolve(cfg, envName);
        var (queues, subscriptions)    = await Discoverer.DiscoverAsync(connectionString, envName, jsonMode);

        if (queues.Length == 0 && subscriptions.Length == 0)
        {
            if (!jsonMode)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Summary: Namespace is empty — no queues or subscriptions found.");
                Console.ResetColor();
            }
            else
            {
                JsonPrinter.Print(envName, []);
            }
            return 0;
        }

        await using var client = new ServiceBusClient(connectionString);

        var results = await Task.WhenAll(
            queues.Select(q => DeadLetterPeeker.PeekQueueAsync(client, q))
            .Concat(subscriptions.Select(s => DeadLetterPeeker.PeekSubscriptionAsync(client, s)))
        );

        if (jsonMode)
        {
            JsonPrinter.Print(envName, results);
        }
        else
        {
            int nameWidth = results.Max(r => r.DisplayName.Length);
            foreach (var result in results)
                Printer.PrintResult(result, nameWidth);

            Printer.PrintSummary(
                results.Sum(r => r.Messages.Count),
                results.Count(r => r.Messages.Count > 0),
                results.Count(r => r.ErrorMessage is not null)
            );
        }

        return results.Sum(r => r.Messages.Count) > 0 || results.Any(r => r.ErrorMessage is not null) ? 1 : 0;
    }
}
