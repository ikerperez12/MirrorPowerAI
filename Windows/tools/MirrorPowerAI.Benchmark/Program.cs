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

        if (parseResult.Error is not null
            || (parseResult.Options is null && parseResult.CorpusOptions is null))
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
            if (parseResult.CorpusOptions is { } corpusOptions)
            {
                var corpusResult = await new CorpusBenchmarkCommand()
                    .RunAndWriteAsync(corpusOptions, cancellationSource.Token)
                    .ConfigureAwait(false);
                CorpusBenchmarkOutput.WriteSummary(Console.Out, corpusResult);
            }
            else
            {
                var options = parseResult.Options!;
                var result = await BenchmarkCommand.RunAsync(
                        options,
                        Console.Out,
                        cancellationSource.Token)
                    .ConfigureAwait(false);
                BenchmarkOutput.WriteResult(Console.Out, result, options.ShowTranscript);
            }

            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            Console.Error.WriteLine("Benchmark cancelado.");
            return CancelledExitCode;
        }
        catch (WhisperModelException)
        {
            Console.Error.WriteLine("Error de modelo: no se pudo preparar el modelo verificado.");
            return ModelErrorExitCode;
        }
        catch (CachedWhisperModelException)
        {
            Console.Error.WriteLine("Error de modelo: no hay un modelo local verificado para el benchmark.");
            return ModelErrorExitCode;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("Error de red: no se pudo obtener el modelo verificado.");
            return ModelErrorExitCode;
        }
        catch (WhisperBenchmarkException)
        {
            Console.Error.WriteLine("Error de Whisper: la inferencia local no se pudo completar.");
            return InferenceErrorExitCode;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Acceso denegado al procesar la entrada del benchmark.");
            return InputErrorExitCode;
        }
        catch (InvalidDataException)
        {
            Console.Error.WriteLine("Entrada no válida para el benchmark.");
            return InputErrorExitCode;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Error de entrada/salida al procesar el benchmark.");
            return InputErrorExitCode;
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Argumentos de entrada no válidos para el benchmark.");
            return InputErrorExitCode;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Error inesperado durante el benchmark.");
            return InferenceErrorExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
