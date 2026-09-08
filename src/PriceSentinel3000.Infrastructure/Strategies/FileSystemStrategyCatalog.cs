using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.Infrastructure.Strategies;

public sealed class FileSystemStrategyCatalog : IStrategyCatalog
{
    public const int MaximumFiles = 128;
    public const int MaximumSourceBytes = 256 * 1024;
    public const string SampleFileName = "OriginalConfirmation.thinkscript";
    private const string SampleSeedMarker = ".original-sample-seeded";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> Extensions =
        new([".thinkscript", ".ts", ".txt"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex TestedIntervalComment = new(
        @"^\s*(?:#|//)\s*PriceSentinel\s*:\s*tested-candle-seconds\b(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TestedIntervalValue = new(@"^\s*=\s*(15|30|60|120|300)\s*$");
    private readonly object _gate = new();
    private readonly string? _seedDirectory;
    private Dictionary<string, PinnedStrategy> _pinned = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemStrategyCatalog(string directoryPath, string? seedDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
        _seedDirectory = seedDirectory is null ? null : Path.GetFullPath(seedDirectory);
    }

    public string DirectoryPath { get; }

    public static FileSystemStrategyCatalog CreateDefault() => new(
        AppDataPaths.StrategiesDirectory,
        Path.Combine(AppContext.BaseDirectory, "Strategies"));

    public StrategyCatalogSnapshot Load()
    {
        lock (_gate)
        {
            var entries = new List<PinnedStrategy>
            {
                new(StrategyDescriptor.BuiltIn, null, null),
            };
            var diagnostics = new List<StrategyCatalogDiagnostic>();
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                SeedSample(diagnostics);
                string[] paths = Directory.EnumerateFiles(DirectoryPath)
                    .Take(MaximumFiles + 1).ToArray();
                if (paths.Length > MaximumFiles)
                {
                    diagnostics.Add(new("Strategies folder",
                        $"Keep at most {MaximumFiles} files in this folder before loading external strategies."));
                }
                else
                {
                    foreach (string path in paths.Order(StringComparer.OrdinalIgnoreCase))
                    {
                        LoadFile(path, entries, diagnostics);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new("Strategies folder", exception.Message));
            }

            HashSet<string> duplicateNames = entries.GroupBy(
                    entry => entry.Descriptor.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _pinned = entries.Select(entry =>
                    !entry.Descriptor.IsBuiltIn && duplicateNames.Contains(entry.Descriptor.Name)
                        ? entry with
                        {
                            Descriptor = entry.Descriptor with
                            {
                                Name = $"{entry.Descriptor.Name} ({entry.Descriptor.FileName})",
                            },
                        }
                        : entry)
                .ToDictionary(entry => entry.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
            return new(_pinned.Values.Select(entry => entry.Descriptor).ToArray(), diagnostics.ToArray());
        }
    }

    public PinnedStrategy GetPinned(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            Load();
            return _pinned.TryGetValue(id, out PinnedStrategy? strategy)
                ? strategy
                : throw new InvalidOperationException(
                    "The selected strategy is missing or incompatible. Refresh the strategy list and review its diagnostics.");
        }
    }

    private static void LoadFile(
        string path,
        ICollection<PinnedStrategy> entries,
        ICollection<StrategyCatalogDiagnostic> diagnostics)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".strategy.cs", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(fileName, "C# strategy files are not supported. Use a compatible ThinkScript source file."));
            return;
        }

        string extension = Path.GetExtension(path);
        if (!Extensions.Contains(extension))
        {
            return;
        }

        try
        {
            byte[] bytes = ReadSourceBytes(path);
            string source = Utf8.GetString(bytes);
            string compilerSource = source.StartsWith('\uFEFF') ? source[1..] : source;
            CompiledThinkScript program = ThinkScriptCompiler.Compile(compilerSource);
            foreach (ScriptDiagnostic diagnostic in program.Diagnostics)
            {
                diagnostics.Add(new(fileName, diagnostic.Message, diagnostic.Line, diagnostic.IsError));
            }

            if (!program.IsCompatible)
            {
                return;
            }

            var descriptor = new StrategyDescriptor(
                "script:" + fileName.ToLowerInvariant(),
                fileName.ToLowerInvariant() switch
                {
                    "originalconfirmation.thinkscript" => "Original Confirmation - experimental",
                    "nflxconfirmation.thinkscript" => "Netflix (NFLX) Confirmation - experimental",
                    _ => Path.GetFileNameWithoutExtension(fileName),
                },
                fileName,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                ThinkScriptCompiler.RuntimeVersion)
            {
                TestedCandleIntervalSeconds = ReadTestedInterval(compilerSource, fileName, diagnostics),
            };
            if (entries.Any(entry => string.Equals(
                    entry.Descriptor.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new(fileName, "Another strategy has the same filename ignoring letter case. Rename this file."));
                return;
            }

            entries.Add(new(descriptor, source, program));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            diagnostics.Add(new(fileName, exception.Message));
        }
    }

    private static int? ReadTestedInterval(
        string source,
        string fileName,
        ICollection<StrategyCatalogDiagnostic> diagnostics)
    {
        int? declarationLine = null;
        int? interval = null;
        using var reader = new StringReader(source);
        for (int lineNumber = 1; reader.ReadLine() is { } line; lineNumber++)
        {
            Match declaration = TestedIntervalComment.Match(line);
            if (!declaration.Success)
            {
                continue;
            }

            if (declarationLine.HasValue)
            {
                diagnostics.Add(new(fileName,
                    "Tested candle interval is not specified because this file has multiple PriceSentinel tested-candle-seconds declarations. Keep exactly one declaration.",
                    lineNumber, IsError: false));
                return null;
            }

            declarationLine = lineNumber;
            Match value = TestedIntervalValue.Match(declaration.Groups[1].Value);
            interval = value.Success ? int.Parse(value.Groups[1].Value) : null;
        }

        if (declarationLine.HasValue && !interval.HasValue)
        {
            diagnostics.Add(new(fileName,
                "Tested candle interval is not specified because its declaration is invalid. Use '# PriceSentinel: tested-candle-seconds=60' with 15, 30, 60, 120, or 300 seconds.",
                declarationLine, IsError: false));
        }

        return interval;
    }

    private void SeedSample(ICollection<StrategyCatalogDiagnostic> diagnostics)
    {
        if (_seedDirectory is null)
        {
            return;
        }

        string marker = Path.Combine(DirectoryPath, SampleSeedMarker);
        if (File.Exists(marker))
        {
            return;
        }

        try
        {
            string target = Path.Combine(DirectoryPath, SampleFileName);
            if (!File.Exists(target))
            {
                byte[] bytes = ReadSourceBytes(Path.Combine(_seedDirectory, SampleFileName));
                FileStream? output = null;
                try
                {
                    output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Another catalog instance has already installed or supplied this file.
                }

                using (output)
                {
                    output?.Write(bytes);
                }
            }

            try
            {
                using var seeded = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(marker))
            {
                // Keep a persistent record so deleting the sample is respected after restart.
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new(SampleFileName, $"Could not install the original sample: {exception.Message}"));
        }
    }

    private static byte[] ReadSourceBytes(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Linked strategy files are not supported. Copy the source file into the Strategies folder.");
        }

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[MaximumSourceBytes + 1];
        int count = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > MaximumSourceBytes)
        {
            throw new IOException($"Strategy source must not exceed {MaximumSourceBytes} bytes.");
        }

        Array.Resize(ref bytes, count);
        return bytes;
    }
}
