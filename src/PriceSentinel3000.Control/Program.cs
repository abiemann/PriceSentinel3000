using System.Text.Json;
using ModelContextProtocol.Server;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Infrastructure.Automation;

namespace PriceSentinel3000.Control;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            var options = ControlOptions.Parse(args);
            if (options.Help)
            {
                await Console.Error.WriteLineAsync("PriceSentinel3000.Control --mcp [--pipe NAME]\n" +
                    "PriceSentinel3000.Control --command COMMAND [--arguments JSON] [--pipe NAME]\n" +
                    "Commands: status, list_strategies, configure, start, pause, resume, step, stop, run_to_end, results, candles, indicators, events, capture_chart.\n" +
                    "Connects to the visible app started with --automation. Does not launch the app or authenticate a broker.");
                return 0;
            }

            var client = new AutomationPipeClient(options.PipeName);
            if (options.Mcp)
            {
                var serverOptions = AutomationTools.CreateServerOptions(client);
                await using var server = McpServer.Create(new StdioServerTransport(serverOptions), serverOptions);
                await server.RunAsync(shutdown.Token);
                return 0;
            }

            var response = await client.SendAsync(new AutomationRequest(options.Command!, options.Arguments), shutdown.Token);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, AutomationProtocol.JsonOptions));
            return response.Success ? 0 : 1;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            await Console.Error.WriteLineAsync(JsonSerializer.Serialize(
                AutomationResponse.Fail("invalid_arguments", exception.Message), AutomationProtocol.JsonOptions));
            return 2;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(JsonSerializer.Serialize(
                AutomationResponse.Fail("control_failed", exception.Message), AutomationProtocol.JsonOptions));
            return 1;
        }
    }
}
