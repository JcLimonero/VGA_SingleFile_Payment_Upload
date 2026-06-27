using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using VGA.FileUploadWorker;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, loggerConfiguration) =>
        loggerConfiguration.ReadFrom.Configuration(builder.Configuration));

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog();

    builder.Services.AddSingleton<ISqliteConnectionProvider, SqliteConnectionProvider>();
    builder.Services.AddSingleton<IFileUploadRepository, SqliteFileUploadRepository>();
    builder.Services.AddSingleton<IFileObtainLogWriter, SqliteFileObtainLogWriter>();
    builder.Services.AddSingleton<IPendingImportRetryStore, SqlitePendingImportRetryStore>();
    builder.Services.Configure<DocumentRelationMysqlOptions>(builder.Configuration.GetSection(DocumentRelationMysqlOptions.SectionName));
    builder.Services.Configure<DocumentByFileMysqlOptions>(builder.Configuration.GetSection(DocumentByFileMysqlOptions.SectionName));
    builder.Services.Configure<BackblazeUploadOptions>(builder.Configuration.GetSection(BackblazeUploadOptions.SectionName));
    builder.Services.AddHttpClient(BackblazeUploadClient.HttpClientName, (sp, client) =>
    {
        var o = sp.GetRequiredService<IOptions<BackblazeUploadOptions>>().Value;
        var seconds = Math.Clamp(o.TimeoutSeconds, 5, 600);
        client.Timeout = TimeSpan.FromSeconds(seconds);
    });
    builder.Services.AddSingleton<IBackblazeUploadClient, BackblazeUploadClient>();
    builder.Services.AddSingleton<IDocumentRelationViewGate, DocumentRelationViewGate>();
    builder.Services.AddSingleton<IDocumentRelationMysqlConnectionProvider, DocumentRelationMysqlConnectionProvider>();
    builder.Services.Configure<DocumentPaymentUploadMysqlOptions>(builder.Configuration.GetSection(DocumentPaymentUploadMysqlOptions.SectionName));
    builder.Services.AddSingleton<IDocumentPaymentUploadTracker, DocumentPaymentUploadTracker>();
    builder.Services.AddSingleton<IDocumentByFileInserter, DocumentByFileInserter>();
    builder.Services.AddSingleton<IDocumentByFileCorreccionService, DocumentByFileCorreccionService>();
    builder.Services.AddSingleton<ImportRollbackService>();
    builder.Services.AddSingleton<PaymentFileImportService>();
    builder.Services.AddSingleton<CorrectionFolderProcessor>();
    builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection(UploadOptions.SectionName));
    builder.Services.AddHostedService<FolderUploadBackgroundService>();

    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "VGA Single File Payment Upload";
        });
    }

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "El worker se detuvo por error al arrancar.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
