using System.Text;
using MirrorPowerAI.Core.Models;

namespace MirrorPowerAI.Benchmark;

internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int InputErrorExitCode = 2;
    private const int ModelErrorExitCode = 3;
    private const int InferenceErrorExitCode = 4;
    private const int CancelledExitCode = 130;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var parseResult = CommandLineParser.Parse(args);
        if (parseResult.ShowHelp)
        {
            Console.Out.WriteLine(CommandLineParser.HelpText);
            return SuccessExitCode;
        }

        if (parseResult.Error is not null || parseResult.Options is null)
        {
            Console.Error.WriteLine($"Error: {parseResult.Error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLineParser.HelpText);
            return InputErrorExitCode;
        }

        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var result = await BenchmarkCommand.RunAsync(
                    parseResult.Options,
                    Console.Out,
                    cancellationSource.Token)
                .ConfigureAwait(false);
            BenchmarkOutput.WriteResult(Console.Out, result);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            Console.Error.WriteLine("Benchmark cancelado.");
            return CancelledExitCode;
        }
        catch (WhisperModelException exception)
        {
            Console.Error.WriteLine($"Error de modelo ({exception.Kind}): {exception.Message}");
            return ModelErrorExitCode;
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine($"Error al obtener el modelo: {exception.Message}");
            return ModelErrorExitCode;
        }
        catch (WhisperBenchmarkException exception)
        {
            Console.Error.WriteLine($"Error de Whisper: {exception.Message}");
            return InferenceErrorExitCode;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Acceso denegado: {exception.Message}");
            return InputErrorExitCode;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"Entrada no válida: {exception.Message}");
            return InputErrorExitCode;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Error de entrada/salida: {exception.Message}");
            return InputErrorExitCode;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Entrada no válida: {exception.Message}");
            return InputErrorExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
