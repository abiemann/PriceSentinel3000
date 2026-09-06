using System.Text.Json;

namespace PriceSentinel3000.Control;

public sealed record ControlOptions(bool Mcp, bool Help, string? PipeName, string? Command, JsonElement Arguments)
{
    public static ControlOptions Parse(string[] args)
    {
        bool mcp = false;
        bool help = false;
        string? pipe = null;
        string? command = null;
        string? arguments = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (!seen.Add(option))
            {
                throw new ArgumentException($"Duplicate option: {option}");
            }

            switch (option)
            {
                case "--mcp": mcp = true; break;
                case "--help" or "-h": help = true; break;
                case "--pipe": pipe = ReadValue(); break;
                case "--command": command = ReadValue(); break;
                case "--arguments": arguments = ReadValue(); break;
                default: throw new ArgumentException($"Unknown option: {option}");
            }

            string ReadValue() => ++index < args.Length && !string.IsNullOrWhiteSpace(args[index])
                ? args[index]
                : throw new ArgumentException($"A value is required after {option}.");
        }

        if (!help && (mcp == (command is not null) || (mcp && arguments is not null)))
        {
            throw new ArgumentException("Use either --mcp or --command COMMAND; --arguments is only valid with --command.");
        }

        if (command == "list_strategies")
        {
            command = "strategies";
        }

        if (command is not null && command is not
            ("status" or "strategies" or "configure" or "start" or "pause" or "resume" or "step" or "stop" or "run_to_end" or "results"))
        {
            throw new ArgumentException($"Unknown automation command: {command}");
        }

        using var document = JsonDocument.Parse(arguments ?? "{}");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("--arguments must be a JSON object.");
        }

        return new ControlOptions(mcp, help, pipe, command, document.RootElement.Clone());
    }
}
