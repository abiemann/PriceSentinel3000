using System.Security.Cryptography;
using System.Text;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Infrastructure.Strategies;

namespace PriceSentinel3000.Infrastructure.Tests.Strategies;

public sealed class FileSystemStrategyCatalogTests : IDisposable
{
    private const string Source =
        "AddOrder(OrderType.BUY_TO_OPEN, close > close[1]); AddOrder(OrderType.SELL_TO_CLOSE, close < close[1]);";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "PriceSentinel3000.Tests", Guid.NewGuid().ToString("N"));
    private string Scripts => Path.Combine(_directory, "Strategies");

    [Fact]
    public void DirectoryCreationIsLazyAndBuiltInIsAlwaysFirst()
    {
        var catalog = new FileSystemStrategyCatalog(Scripts);
        Assert.False(Directory.Exists(Scripts));

        StrategyCatalogSnapshot snapshot = catalog.Load();

        Assert.True(Directory.Exists(Scripts));
        Assert.Equal(StrategyDescriptor.BuiltIn, Assert.Single(snapshot.Strategies));
        Assert.Equal("Built-In", snapshot.Strategies[0].Name);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Null(catalog.GetPinned(StrategyDescriptor.BuiltInId).Program);
    }

    [Fact]
    public void CompatibleTopLevelSourcesHaveStableIdentityAndRawByteHashes()
    {
        Write("Momentum.thinkscript", Source);
        Write("Second.ts", Source);
        Write("Third.txt", Source);
        Write("ignored.cs", "this is not executable discovery input");
        Directory.CreateDirectory(Path.Combine(Scripts, "Nested"));
        File.WriteAllText(Path.Combine(Scripts, "Nested", "Hidden.ts"), Source);
        var catalog = new FileSystemStrategyCatalog(Scripts);

        StrategyCatalogSnapshot snapshot = catalog.Load();

        Assert.Equal(4, snapshot.Strategies.Count);
        StrategyDescriptor script = Assert.Single(snapshot.Strategies, item => item.Name == "Momentum");
        Assert.Equal("script:momentum.thinkscript", script.Id);
        Assert.Equal(ThinkScriptCompiler.RuntimeVersion, script.RuntimeVersion);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Source))), script.SourceSha256);
        Assert.DoesNotContain(snapshot.Strategies, item => item.Name == "Hidden");
        Assert.NotNull(catalog.GetPinned(script.Id).Program);
    }

    [Fact]
    public void BomIsIncludedInHashAndPinnedSourceButNotCompilerText()
    {
        byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Source)];
        Directory.CreateDirectory(Scripts);
        File.WriteAllBytes(Path.Combine(Scripts, "Bom.ts"), bytes);
        var catalog = new FileSystemStrategyCatalog(Scripts);

        PinnedStrategy pinned = catalog.GetPinned("script:bom.ts");

        Assert.Equal("\uFEFF" + Source, pinned.Source);
        Assert.True(pinned.Program!.IsCompatible);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), pinned.Descriptor.SourceSha256);
    }

    [Theory]
    [InlineData("# PriceSentinel: tested-candle-seconds=15", 15)]
    [InlineData("# PriceSentinel: tested-candle-seconds=30", 30)]
    [InlineData("# PriceSentinel: tested-candle-seconds=60", 60)]
    [InlineData("# PriceSentinel: tested-candle-seconds=120", 120)]
    [InlineData("# PriceSentinel: tested-candle-seconds=300", 300)]
    [InlineData("  #  pRiCeSeNtInEl : TESTED-CANDLE-SECONDS = 60  ", 60)]
    [InlineData("// PriceSentinel: tested-candle-seconds=120", 120)]
    [InlineData("\uFEFF# PriceSentinel: tested-candle-seconds=60", 60)]
    public void TestedIntervalMetadataPreservesCompatibleSourceAndRawByteHash(string comment, int seconds)
    {
        string source = comment + "\r\n" + Source;
        Write("Metadata.ts", source);

        PinnedStrategy pinned = new FileSystemStrategyCatalog(Scripts).GetPinned("script:metadata.ts");

        Assert.Equal(seconds, pinned.Descriptor.TestedCandleIntervalSeconds);
        Assert.Equal(source, pinned.Source);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
            pinned.Descriptor.SourceSha256);
        Assert.True(pinned.Program!.IsCompatible);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Use completed one-minute candles; requires an 85-bar warmup.\n")]
    [InlineData("# An example: PriceSentinel: tested-candle-seconds=60\n")]
    [InlineData("# tested-candle-seconds=60\n")]
    [InlineData("# OtherApp: tested-candle-seconds=60\n")]
    public void MissingMetadataAndOrdinaryCommentsLeaveTestedIntervalUnspecified(string prefix)
    {
        Write("Unspecified.ts", prefix + Source);

        StrategyCatalogSnapshot snapshot = new FileSystemStrategyCatalog(Scripts).Load();

        Assert.All(snapshot.Strategies, descriptor => Assert.Null(descriptor.TestedCandleIntervalSeconds));
        Assert.DoesNotContain(snapshot.Diagnostics,
            diagnostic => diagnostic.Message.StartsWith("Tested candle interval", StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataTextInsideCodeStringsAndTrailingCommentsIsIgnored()
    {
        Write("Code.ts", "AddOrder(OrderType.BUY_TO_OPEN, close > close[1], name = \"# PriceSentinel: tested-candle-seconds=60\");\n"
            + "AddOrder(OrderType.SELL_TO_CLOSE, close < close[1]); # PriceSentinel: tested-candle-seconds=120\n");

        StrategyCatalogSnapshot snapshot = new FileSystemStrategyCatalog(Scripts).Load();

        Assert.Null(Assert.Single(snapshot.Strategies, descriptor => !descriptor.IsBuiltIn).TestedCandleIntervalSeconds);
        Assert.DoesNotContain(snapshot.Diagnostics,
            diagnostic => diagnostic.Message.StartsWith("Tested candle interval", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# PriceSentinel: tested-candle-seconds=10")]
    [InlineData("# PriceSentinel: tested-candle-seconds=one minute")]
    [InlineData("# PriceSentinel: tested-candle-seconds=")]
    [InlineData("# PriceSentinel: tested-candle-seconds 60")]
    [InlineData("# PriceSentinel: tested-candle-seconds=60 trailing prose")]
    [InlineData("# PriceSentinel: tested-candle-seconds=60; def ignored = 1;")]
    public void InvalidMetadataWarnsWithoutExcludingCompatibleScript(string comment)
    {
        Write("InvalidMetadata.ts", "# Header\n" + comment + "\n" + Source);

        StrategyCatalogSnapshot snapshot = new FileSystemStrategyCatalog(Scripts).Load();

        Assert.Null(Assert.Single(snapshot.Strategies, descriptor => !descriptor.IsBuiltIn).TestedCandleIntervalSeconds);
        StrategyCatalogDiagnostic warning = Assert.Single(snapshot.Diagnostics,
            diagnostic => diagnostic.Message.StartsWith("Tested candle interval", StringComparison.Ordinal));
        Assert.Equal("InvalidMetadata.ts", warning.FileName);
        Assert.Equal(2, warning.Line);
        Assert.False(warning.IsError);
    }

    [Theory]
    [InlineData("60", "60")]
    [InlineData("60", "120")]
    [InlineData("invalid", "60")]
    [InlineData("60", "invalid")]
    public void DuplicateDeclarationsWarnAndNeverChooseOne(string first, string second)
    {
        Write("DuplicateMetadata.ts", $"# PriceSentinel: tested-candle-seconds={first}\n"
            + $"// pricesentinel: tested-candle-seconds={second}\n" + Source);

        StrategyCatalogSnapshot snapshot = new FileSystemStrategyCatalog(Scripts).Load();

        Assert.Null(Assert.Single(snapshot.Strategies, descriptor => !descriptor.IsBuiltIn).TestedCandleIntervalSeconds);
        StrategyCatalogDiagnostic warning = Assert.Single(snapshot.Diagnostics,
            diagnostic => diagnostic.Message.StartsWith("Tested candle interval", StringComparison.Ordinal));
        Assert.Contains("multiple", warning.Message);
        Assert.Equal(2, warning.Line);
        Assert.False(warning.IsError);
    }

    [Fact]
    public void PinnedSourceSurvivesEditsWhileNextPinRevalidatesCurrentFile()
    {
        Write("Momentum.ts", Source);
        var catalog = new FileSystemStrategyCatalog(Scripts);
        PinnedStrategy original = catalog.GetPinned("script:momentum.ts");
        string changed = Source.Replace("close[1]", "close[2]", StringComparison.Ordinal);
        Write("Momentum.ts", changed);

        PinnedStrategy refreshed = catalog.GetPinned(original.Descriptor.Id);

        Assert.Equal(Source, original.Source);
        Assert.Equal(changed, refreshed.Source);
        Assert.Equal(original.Descriptor.Id, refreshed.Descriptor.Id);
        Assert.NotEqual(original.Descriptor.SourceSha256, refreshed.Descriptor.SourceSha256);
        Assert.NotSame(original.Program, refreshed.Program);
        Write("Momentum.ts", "public class ArbitraryClrCode {} ");
        Assert.Throws<InvalidOperationException>(() => catalog.GetPinned(original.Descriptor.Id));
        Assert.Equal(Source, original.Source);
        File.Delete(Path.Combine(Scripts, "Momentum.ts"));
        Assert.Throws<InvalidOperationException>(() => catalog.GetPinned(original.Descriptor.Id));
    }

    [Fact]
    public void IncompatibleAndLegacyCSharpSourcesAreDiagnosedAndExcluded()
    {
        Write("Invalid.thinkscript", "plot Buy = UnsupportedPlatformFunction(close); plot Sell = no;");
        Write("Legacy.strategy.cs", "public class Legacy { }");
        var catalog = new FileSystemStrategyCatalog(Scripts);

        StrategyCatalogSnapshot snapshot = catalog.Load();

        Assert.Single(snapshot.Strategies);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.FileName == "Invalid.thinkscript");
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.FileName == "Legacy.strategy.cs");
    }

    [Fact]
    public void DuplicateNamesAndBuiltInNameAreUnambiguous()
    {
        Write("Same.ts", Source);
        Write("Same.txt", Source);
        Write("Built-In.ts", Source);

        StrategyCatalogSnapshot snapshot = new FileSystemStrategyCatalog(Scripts).Load();

        Assert.Equal("Built-In", snapshot.Strategies[0].Name);
        Assert.Contains(snapshot.Strategies, item => item.Name == "Same (Same.ts)");
        Assert.Contains(snapshot.Strategies, item => item.Name == "Same (Same.txt)");
        Assert.Contains(snapshot.Strategies, item => item.Name == "Built-In (Built-In.ts)");
        Assert.Equal(4, snapshot.Strategies.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void LockedInvalidUtf8AndOversizedFilesDoNotHideBuiltInOrOtherScripts()
    {
        Write("Good.ts", Source);
        Write("Locked.ts", Source);
        File.WriteAllBytes(Path.Combine(Scripts, "Invalid.ts"), [0xff, 0xfe, 0, 0]);
        File.WriteAllBytes(Path.Combine(Scripts, "Huge.ts"), new byte[FileSystemStrategyCatalog.MaximumSourceBytes + 1]);
        using var locked = new FileStream(Path.Combine(Scripts, "Locked.ts"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);

        StrategyCatalogSnapshot snapshot = new FileSystemStrategyCatalog(Scripts).Load();

        Assert.Equal(2, snapshot.Strategies.Count);
        Assert.Equal(3, snapshot.Diagnostics.Count(diagnostic => diagnostic.IsError));
        Assert.Contains(snapshot.Strategies, item => item.Name == "Good");
    }

    [Fact]
    public void ExcessFileCountDisablesExternalDiscoveryWithAnExplicitDiagnostic()
    {
        for (int index = 0; index <= FileSystemStrategyCatalog.MaximumFiles; index++)
        {
            Write($"Script{index}.ts", Source);
        }

        StrategyCatalogSnapshot snapshot = new FileSystemStrategyCatalog(Scripts).Load();

        Assert.Equal(StrategyDescriptor.BuiltIn, Assert.Single(snapshot.Strategies));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("at most 128"));
    }

    [Fact]
    public void OnlyOriginalPackagedSampleIsSeededAndUserEditsAndDeletionArePreserved()
    {
        string packaged = Path.Combine(_directory, "Packaged");
        Directory.CreateDirectory(packaged);
        File.WriteAllText(Path.Combine(packaged, FileSystemStrategyCatalog.SampleFileName), Source);
        File.WriteAllText(Path.Combine(packaged, "Unrelated.ts"), Source);
        var catalog = new FileSystemStrategyCatalog(Scripts, packaged);

        StrategyDescriptor sample = Assert.Single(catalog.Load().Strategies, item => !item.IsBuiltIn);
        Assert.Equal("Original Confirmation - experimental", sample.Name);
        Assert.Equal("script:originalconfirmation.thinkscript", sample.Id);
        Assert.Equal(FileSystemStrategyCatalog.SampleFileName, sample.FileName);
        Assert.False(File.Exists(Path.Combine(Scripts, "Unrelated.ts")));
        string edited = Source.Replace("close[1]", "close[2]", StringComparison.Ordinal);
        Write(FileSystemStrategyCatalog.SampleFileName, edited);
        catalog.Load();

        Assert.Equal(edited, File.ReadAllText(Path.Combine(Scripts, FileSystemStrategyCatalog.SampleFileName)));
        File.Delete(Path.Combine(Scripts, FileSystemStrategyCatalog.SampleFileName));
        Assert.Single(catalog.Load().Strategies);
        Assert.Single(new FileSystemStrategyCatalog(Scripts, packaged).Load().Strategies);
        Assert.False(File.Exists(Path.Combine(Scripts, FileSystemStrategyCatalog.SampleFileName)));
    }

    [Fact]
    public void ExistingSampleIsMarkedSeededWithoutOverwritingEvenIfPackageIsUnavailable()
    {
        string edited = Source.Replace("close[1]", "close[2]", StringComparison.Ordinal);
        Write(FileSystemStrategyCatalog.SampleFileName, edited);
        string packaged = Path.Combine(_directory, "MissingPackage");
        StrategyCatalogSnapshot snapshot = new FileSystemStrategyCatalog(Scripts, packaged).Load();

        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.IsError);
        Assert.Equal(edited, File.ReadAllText(Path.Combine(Scripts, FileSystemStrategyCatalog.SampleFileName)));
        File.Delete(Path.Combine(Scripts, FileSystemStrategyCatalog.SampleFileName));
        Assert.Single(new FileSystemStrategyCatalog(Scripts, packaged).Load().Strategies);
    }

    [Fact]
    public void FailedSampleReadCanBeRetriedAfterPackageBecomesAvailable()
    {
        string packaged = Path.Combine(_directory, "Packaged");
        var catalog = new FileSystemStrategyCatalog(Scripts, packaged);
        Assert.Contains(catalog.Load().Diagnostics, diagnostic => diagnostic.Message.Contains("Could not install"));
        Directory.CreateDirectory(packaged);
        File.WriteAllText(Path.Combine(packaged, FileSystemStrategyCatalog.SampleFileName), Source);

        Assert.Equal(2, catalog.Load().Strategies.Count);
    }

    [Fact]
    public void UnavailableFolderStillProvidesBuiltInAndDiagnostic()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Scripts, "This path is a file.");

        StrategyCatalogSnapshot snapshot = new FileSystemStrategyCatalog(Scripts).Load();

        Assert.Equal(StrategyDescriptor.BuiltIn, Assert.Single(snapshot.Strategies));
        Assert.Single(snapshot.Diagnostics);
    }

    private void Write(string name, string source)
    {
        Directory.CreateDirectory(Scripts);
        File.WriteAllText(Path.Combine(Scripts, name), source, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
