using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

return await ReplayExport.RunAsync(args);

internal static class ReplayExport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(180);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("ReplayExport --manifest FILE --control EXE --output DIRECTORY [--pipe NAME] [--resume]\n" +
                "Uses the existing MCP/stdio bridge. Does not launch the desktop app, refresh scripts, restore settings, or retry mutations.\n" +
                "--resume skips only fully validated completed exports; existing unavailable/partial files require a revised manifest.");
            return 0;
        }
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            bool resume = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--resume") { Require(!resume, "Duplicate --resume."); resume = true; continue; }
                Require(args[i] is "--manifest" or "--control" or "--output" or "--pipe", $"Unknown option: {args[i]}");
                Require(i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal), $"Missing value: {args[i]}");
                string option = args[i++];
                Require(options.TryAdd(option, args[i]), $"Duplicate option: {option}");
            }
            foreach (string required in new[] { "--manifest", "--control", "--output" })
                Require(options.ContainsKey(required), $"Missing {required}.");
            string control = Path.GetFullPath(options["--control"]);
            Require(File.Exists(control), "Control executable does not exist.");
            string output = Path.GetFullPath(options["--output"]);
            JsonElement manifest = ReadJson(options["--manifest"]);
            var jobs = ReadJobs(manifest);
            JsonElement settings = manifest.GetProperty("settings");
            Require(settings.ValueKind == JsonValueKind.Object, "settings must be an object.");
            Require(!settings.TryGetProperty("symbol", out _) && !settings.TryGetProperty("replayDate", out _) && !settings.TryGetProperty("strategyId", out _),
                "symbol, replayDate, and strategyId belong in jobs, not shared settings.");
            // Fail before connecting if a prior export would be overwritten or cannot be resumed.
            foreach (Job job in jobs)
            {
                Require(!File.Exists(Path.Combine(output, "unavailable-" + job.FileName)),
                    $"Existing unavailable export for {job.Label}; omit that job from the next manifest.");
                string file = Path.Combine(output, job.FileName);
                if (File.Exists(file))
                {
                    Require(resume, $"Existing export: {file}. Use --resume to validate and skip completed runs.");
                    ValidateExport(ReadJson(file), job, SettingsFor(settings, job));
                }
            }
            Directory.CreateDirectory(output);
            string batch = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
            string transcript = Path.Combine(output, $"transcript-{batch}.ndjson");
            SaveNew(Path.Combine(output, $"manifest-{batch}.json"), manifest);
            var serverArguments = new List<string> { "--mcp" };
            if (options.TryGetValue("--pipe", out string? pipe)) { serverArguments.Add("--pipe"); serverArguments.Add(pipe); }
            using var connectTimeout = new CancellationTokenSource(CallTimeout);
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = control, Arguments = serverArguments, Name = "PriceSentinel sequential Replay research exporter",
            }), cancellationToken: connectTimeout.Token);
            connectTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            var session = new Bridge(client, transcript);
            JsonElement initial = await session.StatusAsync();
            session.Owner = initial.GetProperty("processId").GetInt32();
            RequireIdle(initial);
            string initialPath = Path.Combine(output, "initial-status.json");
            if (!File.Exists(initialPath)) SaveNew(initialPath, initial);
            SaveNew(Path.Combine(output, $"status-before-{batch}.json"), initial);
            JsonElement catalog = await session.CallAsync("list_strategies", new { refresh = false });
            SaveNew(Path.Combine(output, $"strategies-{batch}.json"), catalog);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Job job in jobs)
            {
                JsonElement[] matches = catalog.GetProperty("strategies").EnumerateArray()
                    .Where(item => Text(item, "id") == job.StrategyId).ToArray();
                Require(matches.Length == 1 && !Flag(matches[0], "isBuiltIn"), $"Compatible external strategy is not listed: {job.StrategyId}");
                string hash = Text(matches[0], "sourceSha256");
                Require(hash.Length == 64, $"Missing catalog script hash: {job.StrategyId}");
                Require(job.SourceSha256 is null || job.SourceSha256 == hash, $"Manifest/catalog source hash mismatch: {job.StrategyId}");
                hashes[job.StrategyId] = hash;
                string existing = Path.Combine(output, job.FileName);
                if (File.Exists(existing)) ValidateExport(ReadJson(existing), job, SettingsFor(settings, job), hash);
            }
            int index = 0;
            foreach (Job job in jobs)
            {
                index++;
                string destination = Path.Combine(output, job.FileName);
                if (File.Exists(destination)) { Console.WriteLine($"[{index}/{jobs.Count}] validated existing {job.Label}"); continue; }
                Console.WriteLine($"[{index}/{jobs.Count}] starting {job.Label}");
                JsonElement requestSettings = SettingsFor(settings, job);
                using var runTimeout = new CancellationTokenSource(RunTimeout);
                try
                {
                    CancellationToken token = runTimeout.Token;
                    JsonElement before = await session.StatusAsync(token);
                    RequireIdle(before);
                    JsonElement configured = await session.CallAsync("configure", new { mode = "Replay", settings = requestSettings }, token);
                    session.ValidateStatus(configured);
                    CheckSettings(configured.GetProperty("settings"), requestSettings);
                    JsonElement beforeStart = await session.StatusAsync(token);
                    RequireIdle(beforeStart);
                    RequireReplay(beforeStart);
                    CheckSettings(beforeStart.GetProperty("settings"), requestSettings);
                    JsonElement started = await session.RawCallAsync("start", new { fast = true }, token);
                    // A synchronous startup failure may have no result. Read status once; never retry start.
                    JsonElement status = Flag(started, "success") ? started.GetProperty("result").Clone() : await session.StatusAsync(token);
                    session.ValidateStatus(status);
                    RequireReplay(status);
                    string operation = Text(status, "operationId");
                    Require(operation.Length > 0 && operation != Text(beforeStart, "operationId"), "Start did not establish a new operation identity.");
                    if (!Flag(started, "success"))
                        Require(Text(started, "errorCode") == "start_failed" && IsNoHistory(status, job), "Start failed; inspect transcript and status before further action.");
                    int lastProgress = -1;
                    DateTimeOffset lastMessage = DateTimeOffset.MinValue;
                    while (true)
                    {
                        session.ValidateStatus(status);
                        RequireReplay(status);
                        Require(Text(status, "operationId") == operation, "Operation identity changed during Replay.");
                        CheckSettings(status.GetProperty("settings"), requestSettings);
                        string state = Text(status, "operationState");
                        if (state is "completed" or "failed") break;
                        Require(state is "starting" or "running", $"Unexpected Replay state: {state}; no automatic resume/stop was issued.");
                        int progress = status.GetProperty("processedObservations").GetInt32();
                        if (progress != lastProgress && DateTimeOffset.UtcNow - lastMessage >= TimeSpan.FromSeconds(15))
                        {
                            Console.WriteLine($"  {job.Label}: {progress}/{status.GetProperty("totalObservations").GetInt32()} observations");
                            lastProgress = progress; lastMessage = DateTimeOffset.UtcNow;
                        }
                        await Task.Delay(250, token);
                        status = await session.StatusAsync(token);
                    }
                    if (IsNoHistory(status, job))
                    {
                        SaveNew(Path.Combine(output, "unavailable-" + job.FileName), new { job = job.Export(), requestSettings, status });
                        Console.WriteLine($"[{index}/{jobs.Count}] unavailable {job.Label}: explicit no-history response");
                        continue;
                    }
                    Require(Text(status, "operationState") == "completed", $"Replay failed: {Text(status, "operationError")}");
                    RequireIdle(status);
                    string sessionId = Text(status, "sessionId");
                    Require(sessionId.Length > 0, "Completed run has no session ID.");
                    JsonElement results = await session.CallAsync("results", new { }, token);
                    JsonElement indicators = await session.CallAsync("indicators", new { sessionId }, token);
                    StreamExport source = await session.PagesAsync("candles", sessionId, "source", token);
                    StreamExport strategy = await session.PagesAsync("candles", sessionId, "strategy", token);
                    StreamExport events = await session.PagesAsync("events", sessionId, null, token);
                    JsonElement after = await session.StatusAsync(token);
                    RequireReplay(after); RequireIdle(after);
                    Require(Text(after, "operationId") == operation && Text(after, "sessionId") == sessionId && Text(after, "operationState") == "completed", "Session changed during export.");
                    CheckSettings(after.GetProperty("settings"), requestSettings);
                    JsonElement export = JsonSerializer.SerializeToElement(new { job = job.Export(), requestSettings, status, results, indicators, source, strategy, events }, JsonOptions);
                    ValidateExport(export, job, requestSettings, hashes[job.StrategyId]);
                    SaveNew(destination, export);
                    Console.WriteLine($"[{index}/{jobs.Count}] saved {job.Label}: source={source.Records.Count}, strategy={strategy.Records.Count}, events={events.Records.Count}");
                }
                catch (Exception exception)
                {
                    // An uncertain mutation can still be executing. Capture state read-only and stop the batch.
                    JsonElement? lastStatus = null;
                    string? statusError = null;
                    try { lastStatus = await session.StatusAsync(); }
                    catch (Exception inspection) { statusError = inspection.Message; }
                    SaveNew(Path.Combine(output, $"failed-{Path.GetFileNameWithoutExtension(job.FileName)}-{batch}.json"),
                        new { job = job.Export(), requestSettings, error = exception.ToString(), lastStatus, statusError });
                    throw;
                }
            }
            Console.WriteLine($"Batch finished. Initial settings remain in {initialPath}. No settings restoration was performed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Stopped: {exception.Message}\nNo mutation was retried. Inspect the app status and saved transcript before continuing.");
            return 1;
        }
    }

    private static List<Job> ReadJobs(JsonElement manifest)
    {
        var jobs = new List<Job>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in manifest.GetProperty("jobs").EnumerateArray())
        {
            string symbol = Text(item, "symbol"), date = Text(item, "date"), profile = Text(item, "profile"), id = Text(item, "strategyId");
            Require(Regex.IsMatch(symbol, "^[A-Z][A-Z0-9.]{0,9}$", RegexOptions.CultureInvariant), "Job symbol must be uppercase and filename-safe.");
            Require(DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "Job date must use yyyy-MM-dd.");
            Require(Regex.IsMatch(profile, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant), "Job profile must be filename-safe.");
            Require(id.StartsWith("script:", StringComparison.Ordinal), "Each job requires an external script strategyId.");
            string? hash = item.TryGetProperty("sourceSha256", out JsonElement value) ? value.GetString()?.ToLowerInvariant() : null;
            Require(hash is null || Regex.IsMatch(hash, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant), "Invalid sourceSha256.");
            var job = new Job(symbol, date, profile, id, hash);
            Require(names.Add(job.FileName), $"Duplicate job filename: {job.FileName}");
            jobs.Add(job);
        }
        Require(jobs.Count > 0, "Manifest jobs must not be empty.");
        return jobs;
    }

    private static JsonElement SettingsFor(JsonElement settings, Job job)
    {
        var patch = settings.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        patch.Add("symbol", JsonSerializer.SerializeToElement(job.Symbol));
        patch.Add("replayDate", JsonSerializer.SerializeToElement(job.Date));
        patch.Add("strategyId", JsonSerializer.SerializeToElement(job.StrategyId));
        return JsonSerializer.SerializeToElement(patch);
    }

    private static void ValidateExport(JsonElement run, Job job, JsonElement expectedSettings, string? expectedHash = null)
    {
        JsonElement savedJob = run.GetProperty("job");
        foreach (var pair in new Dictionary<string, string> { ["symbol"] = job.Symbol, ["date"] = job.Date, ["profile"] = job.Profile, ["strategyId"] = job.StrategyId })
            Require(Text(savedJob, pair.Key) == pair.Value, $"Export job mismatch: {pair.Key}.");
        JsonElement status = run.GetProperty("status"), results = run.GetProperty("results");
        RequireReplay(status); RequireIdle(status);
        Require(Text(status, "operationState") == "completed" && Text(results, "outcome") == "COMPLETED" && Text(results, "mode") == "Replay", "Export is not a completed Replay.");
        Require(Text(status, "operationId").Length > 0, "Export has no operation ID.");
        string id = Text(status, "sessionId");
        Require(id.Length > 0 && Text(results, "sessionId") == id && Text(run.GetProperty("indicators"), "sessionId") == id, "Export session identity mismatch.");
        CheckSettings(status.GetProperty("settings"), expectedSettings);
        CheckSettings(run.GetProperty("requestSettings"), expectedSettings);
        CheckSettings(expectedSettings, run.GetProperty("requestSettings"));
        JsonElement pinned = results.GetProperty("settings").GetProperty("Strategy");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pinned.GetProperty("Source").GetString()!))).ToLowerInvariant();
        Require(Text(pinned, "Id") == job.StrategyId && Text(pinned, "SourceSha256") == hash && Text(status.GetProperty("strategy"), "sourceSha256") == hash,
            "Pinned script identity/hash mismatch.");
        Require((expectedHash is null || hash == expectedHash) && (job.SourceSha256 is null || hash == job.SourceSha256), "Script hash differs from current catalog or frozen manifest.");
        int source = ValidateStream(run.GetProperty("source"), id), strategy = ValidateStream(run.GetProperty("strategy"), id), events = ValidateStream(run.GetProperty("events"), id);
        Require(source > 0 && source == events && source == status.GetProperty("processedObservations").GetInt32() && source == status.GetProperty("totalObservations").GetInt32()
            && source == results.GetProperty("summary").GetProperty("quoteCount").GetInt32() && strategy == status.GetProperty("completedStrategyBars").GetInt32(), "Export stream/status counts disagree.");
    }

    private static int ValidateStream(JsonElement stream, string sessionId)
    {
        JsonElement records = stream.GetProperty("records"), pages = stream.GetProperty("pages");
        int count = records.GetArrayLength();
        Require(pages.GetArrayLength() > 0, "Stream has no page metadata.");
        long cursor = 0;
        int index = 0;
        foreach (JsonElement page in pages.EnumerateArray())
        {
            Require(Text(page, "sessionId") == sessionId && Text(page, "mode") == "Replay" && !Flag(page, "truncated"), "Stream is truncated or has a different session/mode.");
            Require(page.GetProperty("firstAvailableSequence").GetInt64() == (count == 0 ? 0 : 1), "Stream does not begin at sequence 1.");
            long next = page.GetProperty("nextSequence").GetInt64();
            bool more = Flag(page, "hasMore");
            Require(next >= cursor && next <= count && (next > cursor || count == 0), "Invalid page cursor progression.");
            Require(more == (++index < pages.GetArrayLength()), "Page termination metadata is inconsistent.");
            cursor = next;
        }
        Require(cursor == count, "Final page cursor does not equal the record count.");
        long sequence = 0;
        foreach (JsonElement record in records.EnumerateArray())
            Require(record.GetProperty("sequence").GetInt64() == ++sequence && !Flag(record, "omitted"), "Stream has an omitted record or sequence gap.");
        return count;
    }

    private static void CheckSettings(JsonElement actual, JsonElement expected)
    {
        foreach (JsonProperty setting in expected.EnumerateObject())
        {
            Require(actual.TryGetProperty(setting.Name, out JsonElement value), $"Missing setting: {setting.Name}.");
            bool equal = value.ValueKind == JsonValueKind.Number && setting.Value.ValueKind == JsonValueKind.Number
                ? value.GetDecimal() == setting.Value.GetDecimal() : JsonElement.DeepEquals(value, setting.Value);
            Require(equal, $"Setting mismatch: {setting.Name}.");
        }
    }

    private static bool IsNoHistory(JsonElement status, Job job) => Text(status, "operationState") == "failed"
        && Text(status, "operationError").StartsWith($"Robinhood returned no {job.Symbol} trades", StringComparison.Ordinal)
        && Text(status, "sessionId").Length == 0 && status.GetProperty("totalObservations").GetInt32() == 0
        && status.GetProperty("processedObservations").GetInt32() == 0 && !Flag(status, "running") && !Flag(status, "starting");

    private static void RequireIdle(JsonElement state) => Require(!Flag(state, "running") && !Flag(state, "starting") && !Flag(state, "paused"), "App has an active/paused session; no mutation performed.");
    private static void RequireReplay(JsonElement state) => Require(Text(state, "selectedMode") == "Replay" && Text(state, "effectiveMode") == "Replay", "App is no longer configured for Replay.");
    private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static string Text(JsonElement obj, string name) => obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static bool Flag(JsonElement obj, string name) => obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    private static JsonElement ReadJson(string file) { using var document = JsonDocument.Parse(File.ReadAllText(file)); return document.RootElement.Clone(); }
    private static void SaveNew(string file, object value)
    {
        // Write-through and CreateNew preserve prior evidence; a partial write cannot pass resume validation.
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, value, JsonOptions);
        stream.Flush(true);
    }

    private sealed record Job(string Symbol, string Date, string Profile, string StrategyId, string? SourceSha256)
    {
        internal string FileName => $"{Symbol}-{Date}-{Profile}.json";
        internal string Label => $"{Symbol}/{Date}/{Profile}";
        internal object Export() => new { symbol = Symbol, date = Date, profile = Profile, strategyId = StrategyId, id = StrategyId, sourceSha256 = SourceSha256 };
    }

    private sealed record StreamExport(List<JsonElement> Records, List<Dictionary<string, JsonElement>> Pages);

    private sealed class Bridge(McpClient client, string transcript)
    {
        internal int? Owner { get; set; }
        internal void ValidateStatus(JsonElement state)
        {
            Require(Owner is null || state.GetProperty("processId").GetInt32() == Owner, "Controlled app process changed.");
            Require(!Text(state, "selectedMode").Equals("Live", StringComparison.OrdinalIgnoreCase)
                && !Text(state, "effectiveMode").Equals("Live", StringComparison.OrdinalIgnoreCase), "LIVE context detected; no mutation performed.");
        }
        internal async Task<JsonElement> StatusAsync(CancellationToken token = default)
        {
            JsonElement result = await CallAsync("status", new { }, token);
            ValidateStatus(result);
            return result;
        }
        internal async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default)
        {
            JsonElement response = await RawCallAsync(tool, arguments, token);
            Require(Flag(response, "success"), $"{tool} failed ({Text(response, "errorCode")}): {Text(response, "error")}");
            return response.GetProperty("result").Clone();
        }
        internal async Task<JsonElement> RawCallAsync(string tool, object arguments, CancellationToken token)
        {
            JsonElement input = JsonSerializer.SerializeToElement(arguments);
            var dictionary = input.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(CallTimeout);
            DateTimeOffset at = DateTimeOffset.UtcNow;
            try
            {
                CallToolResult result = await client.CallToolAsync(tool, dictionary, cancellationToken: timeout.Token);
                Require(result.StructuredContent.HasValue, $"{tool} returned no structured MCP response.");
                JsonElement response = result.StructuredContent!.Value.Clone();
                // JsonElement numeric tokens are written directly, without a double conversion.
                File.AppendAllText(transcript, JsonSerializer.Serialize(new { at, transport = "MCP/stdio", tool, arguments = input, response }, JsonOptions) + Environment.NewLine);
                return response;
            }
            catch (Exception exception)
            {
                File.AppendAllText(transcript, JsonSerializer.Serialize(new { at, transport = "MCP/stdio", tool, arguments = input, error = exception.ToString() }, JsonOptions) + Environment.NewLine);
                throw;
            }
        }
        internal async Task<StreamExport> PagesAsync(string tool, string sessionId, string? kind, CancellationToken token)
        {
            var records = new List<JsonElement>();
            var pages = new List<Dictionary<string, JsonElement>>();
            long cursor = 0;
            while (true)
            {
                var request = new Dictionary<string, object?> { ["sessionId"] = sessionId, ["afterSequence"] = cursor, ["limit"] = 100 };
                if (kind is not null) request.Add("kind", kind);
                JsonElement page = await CallAsync(tool, request, token);
                Require(Text(page, "sessionId") == sessionId && Text(page, "mode") == "Replay" && !Flag(page, "truncated"), "Page session/mode mismatch or truncation.");
                JsonElement items = page.GetProperty("records");
                foreach (JsonElement record in items.EnumerateArray())
                {
                    Require(record.GetProperty("sequence").GetInt64() == records.Count + 1L && !Flag(record, "omitted"), "Page has a missing/omitted record.");
                    records.Add(record.Clone());
                }
                long next = page.GetProperty("nextSequence").GetInt64();
                bool more = Flag(page, "hasMore");
                Require(next == records.Count && (next > cursor || !more), "Page cursor failed to advance consistently.");
                pages.Add(page.EnumerateObject().Where(p => p.Name != "records").ToDictionary(p => p.Name, p => p.Value.Clone()));
                cursor = next;
                if (!more) break;
            }
            return new StreamExport(records, pages);
        }
    }
}
