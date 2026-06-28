using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace VGA.FileUploadWorker;

public static class FailedUploadRecoveryCli
{
    public const string CommandName = "recover-failed-uploads";

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseArgs(args);
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Configuration.SetBasePath(AppContext.BaseDirectory);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        builder.Services.AddSerilog((_, lc) => lc.WriteTo.Console());
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog();

        RegisterServices(builder.Services, builder.Configuration);

        using var host = builder.Build();
        await SqliteSchema.EnsureCreatedAsync(
            host.Services.GetRequiredService<ISqliteConnectionProvider>(),
            cancellationToken).ConfigureAwait(false);

        var recovery = host.Services.GetRequiredService<FailedUploadRecoveryService>();
        var plan = await recovery.BuildPlanAsync(options.RecoveryOptions, cancellationToken).ConfigureAwait(false);

        PrintPlan(plan);

        if (plan.Items.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No hay intentos que recuperar con los filtros indicados.");
            return 0;
        }

        if (!options.Execute)
        {
            Console.WriteLine();
            Console.WriteLine("Modo vista previa (dry-run). Para ejecutar, añada --execute.");
            return 0;
        }

        if (!options.Yes)
        {
            Console.WriteLine();
            Console.Write("¿Continuar con la recuperación? (s/N): ");
            var answer = Console.ReadLine()?.Trim();
            if (!string.Equals(answer, "s", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(answer, "si", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(answer, "sí", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Operación cancelada.");
                return 0;
            }
        }

        var result = await recovery.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"Recuperación completada: {result.SuccessCount} OK, {result.FailureCount} fallidos.");

        foreach (var itemResult in result.ItemResults.Where(r => !r.Succeeded))
        {
            Console.WriteLine($"  FALLO [{itemResult.Item.GroupKey}]: {itemResult.ErrorDetail}");
        }

        return result.FailureCount > 0 ? 1 : 0;
    }

    private static void RegisterServices(IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddSingleton<ISqliteConnectionProvider, SqliteConnectionProvider>();
        services.AddSingleton<IPendingImportRetryStore, SqlitePendingImportRetryStore>();
        services.Configure<DocumentRelationMysqlOptions>(configuration.GetSection(DocumentRelationMysqlOptions.SectionName));
        services.Configure<DocumentByFileMysqlOptions>(configuration.GetSection(DocumentByFileMysqlOptions.SectionName));
        services.Configure<DocumentPaymentUploadMysqlOptions>(configuration.GetSection(DocumentPaymentUploadMysqlOptions.SectionName));
        services.Configure<UploadOptions>(configuration.GetSection(UploadOptions.SectionName));
        services.AddSingleton<IDocumentRelationMysqlConnectionProvider, DocumentRelationMysqlConnectionProvider>();
        services.AddSingleton<IDocumentByFileCorreccionService, DocumentByFileCorreccionService>();
        services.AddSingleton<IDocumentPaymentUploadQueryService, DocumentPaymentUploadQueryService>();
        services.AddSingleton<FailedUploadRecoveryService>();
    }

    private static CliOptions ParseArgs(string[] args)
    {
        var result = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase))
            {
                result.ShowHelp = true;
                continue;
            }

            if (string.Equals(arg, "--execute", StringComparison.OrdinalIgnoreCase))
            {
                result.Execute = true;
                continue;
            }

            if (string.Equals(arg, "--yes", StringComparison.OrdinalIgnoreCase))
            {
                result.Yes = true;
                continue;
            }

            if (string.Equals(arg, "--channel", StringComparison.OrdinalIgnoreCase))
            {
                result.RecoveryOptions = result.RecoveryOptions with
                {
                    ChannelFilter = ReadNextValue(args, ref i, "--channel"),
                };
                continue;
            }

            if (string.Equals(arg, "--error-contains", StringComparison.OrdinalIgnoreCase))
            {
                result.RecoveryOptions = result.RecoveryOptions with
                {
                    ErrorContainsFilter = ReadNextValue(args, ref i, "--error-contains"),
                };
            }
        }

        return result;
    }

    private static string ReadNextValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
            throw new ArgumentException($"Falta valor para {flag}.");
        index++;
        return args[index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Uso: VGA.FileUploadWorker recover-failed-uploads [opciones]

            Recupera intentos con CloudLastError: deshabilita documentbyfile (Enabled=0),
            mueve archivos de PROCESADOS a la carpeta original cuando aplica, y limpia reintentos SQLite.

            Opciones:
              (sin flags)              Vista previa (dry-run); no modifica nada
              --execute                Ejecuta desactivaciones y movimientos
              --yes                    Omite confirmación interactiva
              --channel <nombre>       Filtra por canal (EFECTIVO, TPV, DB, ...)
              --error-contains <texto> Filtra por substring en CloudLastError
              -h, --help               Muestra esta ayuda

            Recomendación: detener el servicio Windows antes de --execute.
            """);
    }

    private static void PrintPlan(RecoveryPlan plan)
    {
        Console.WriteLine($"Raíz de escaneo: {plan.RootPath}");
        Console.WriteLine($"Intentos a recuperar: {plan.Items.Count}");
        Console.WriteLine(new string('-', 120));

        var index = 1;
        foreach (var item in plan.Items)
        {
            Console.WriteLine($"[{index}] Grupo: {item.GroupKey}");
            Console.WriteLine($"    PaymentUploadId: {(item.PaymentUploadId?.ToString() ?? "(null)")}");
            Console.WriteLine($"    Canal: {item.Channel}");
            Console.WriteLine($"    Archivo original: {item.OriginalFileName}");
            Console.WriteLine($"    Origen: {(string.IsNullOrWhiteSpace(item.ProcessedFullPath) ? "ya en canal" : item.ProcessedFullPath)}");
            Console.WriteLine($"    Destino: {item.DestinationPath}");
            Console.WriteLine($"    DocumentByFileIds: {string.Join(", ", item.DocumentByFileIds)}");
            Console.WriteLine($"    Acción: {item.Action} — {item.ActionReason}");
            Console.WriteLine($"    Origen existe: {item.SourceFileExists}; Destino ocupado: {item.DestinationExists}");
            Console.WriteLine($"    CloudLastError: {item.CloudLastError}");
            Console.WriteLine(new string('-', 120));
            index++;
        }
    }

    private sealed class CliOptions
    {
        public bool ShowHelp { get; set; }
        public bool Execute { get; set; }
        public bool Yes { get; set; }
        public RecoveryOptions RecoveryOptions { get; set; } = new();
    }
}
