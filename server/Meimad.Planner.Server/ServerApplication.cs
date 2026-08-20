using System.Reflection;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Api.Cases;
using Meimad.Planner.Server.Api.AdministrativeSetup;
using Meimad.Planner.Server.Api.EditMode;
using Meimad.Planner.Server.Api.EventLogging;
using Meimad.Planner.Server.Api.Deletion;
using Meimad.Planner.Server.Api.Downtimes;
using Meimad.Planner.Server.Api.EInk;
using Meimad.Planner.Server.Api.JobPackages;
using Meimad.Planner.Server.Api.Kitaron;
using Meimad.Planner.Server.Api.LegacyImport;
using Meimad.Planner.Server.Api.MachineAssignments;
using Meimad.Planner.Server.Api.Machines;
using Meimad.Planner.Server.Api.MachineTypes;
using Meimad.Planner.Server.Api.Orders;
using Meimad.Planner.Server.Api.ProductionBatches;
using Meimad.Planner.Server.Api.Postprocessors;
using Meimad.Planner.Server.Api.Reports;
using Meimad.Planner.Server.Api.PlanningBoard;
using Meimad.Planner.Server.Api.Timeline;
using Meimad.Planner.Server.Api.TvDashboard;
using Meimad.Planner.Server.Api.WorkingCalendars;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.Deletion;
using Meimad.Planner.Server.Application.Downtimes;
using Meimad.Planner.Server.Application.EInk;
using Meimad.Planner.Server.Application.JobPackages;
using Meimad.Planner.Server.Application.Kitaron;
using Meimad.Planner.Server.Application.LegacyImport;
using Meimad.Planner.Server.Application.MachineAssignments;
using Meimad.Planner.Server.Application.Machines;
using Meimad.Planner.Server.Application.MachineTypes;
using Meimad.Planner.Server.Application.Orders;
using Meimad.Planner.Server.Application.ProductionBatches;
using Meimad.Planner.Server.Application.Postprocessors;
using Meimad.Planner.Server.Application.Reports;
using Meimad.Planner.Server.Application.PlanningBoard;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Application.TvDashboard;
using Meimad.Planner.Server.Application.WorkingCalendars;
using Meimad.Planner.Server.Domain.Timeline;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;

namespace Meimad.Planner.Server;

public static class ServerApplication
{
    public static WebApplication Build(
        string[] args,
        Action<IWebHostBuilder>? configureWebHost = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Configuration
            .AddEnvironmentVariables(prefix: "MEIMAD_")
            .AddCommandLine(args);

        var serverOptions = ServerOptions.FromConfiguration(builder.Configuration);
        var databaseOptions = DatabaseOptions.FromConfiguration(
            builder.Configuration,
            builder.Environment.ContentRootPath);
        var editModeOptions = EditModeOptions.FromConfiguration(builder.Configuration);
        var backupOptions = BackupOptions.FromConfiguration(
            builder.Configuration,
            builder.Environment.ContentRootPath,
            databaseOptions.DatabasePath);
        var tvDashboardOptions = TvDashboardOptions.FromConfiguration(builder.Configuration);
        var eInkOptions = EInkOptions.FromConfiguration(
            builder.Configuration,
            builder.Environment.ContentRootPath);
        var timelineOptions = TimelineOptions.FromConfiguration(builder.Configuration);
        var legacyImportOptions = LegacyImportOptions.FromConfiguration(builder.Configuration);

        builder.Services.AddSingleton(serverOptions);
        builder.Services.AddSingleton(databaseOptions);
        builder.Services.AddSingleton(editModeOptions);
        builder.Services.AddSingleton(backupOptions);
        builder.Services.AddSingleton(tvDashboardOptions);
        builder.Services.AddSingleton(eInkOptions);
        builder.Services.AddSingleton(timelineOptions);
        builder.Services.AddSingleton(legacyImportOptions);
        builder.Services.AddSingleton<SqliteDatabase>();
        builder.Services.AddDataProtection()
            .SetApplicationName("Meimad.Planner.Server");
        builder.Services.AddSingleton<DatabaseMigrator>();
        builder.Services.AddSingleton<SqliteBackupService>();
        builder.Services.AddSingleton<IAdministrativeSetupRepository, SqliteAdministrativeSetupRepository>();
        builder.Services.AddHttpClient<IIsraeliHolidaySource, HebcalIsraeliHolidaySource>(client =>
        {
            client.BaseAddress = new Uri("https://www.hebcal.com/");
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Meimad-Planner/0.1.10");
        });
        builder.Services.AddSingleton<AdministrativeSetupService>();
        builder.Services.AddSingleton<IPlanningDeletionRepository, SqlitePlanningDeletionRepository>();
        builder.Services.AddSingleton<PlanningDeletionService>();
        builder.Services.AddHostedService<DatabaseInitializationService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IEditModeRepository, SqliteEditModeRepository>();
        builder.Services.AddSingleton<EditModeService>();
        builder.Services.AddHostedService<EditModeTimeoutService>();
        builder.Services.AddSingleton<ICaseRepository, SqliteCaseRepository>();
        builder.Services.AddSingleton<CaseService>();
        builder.Services.AddSingleton<ICaseComponentRepository, SqliteCaseComponentRepository>();
        builder.Services.AddSingleton<CaseComponentService>();
        builder.Services.AddSingleton<IOrderRepository, SqliteOrderRepository>();
        builder.Services.AddSingleton<OrderService>();
        builder.Services.AddSingleton<DerivedCaseOrderService>();
        builder.Services.AddSingleton<IProductionBatchRepository, SqliteProductionBatchRepository>();
        builder.Services.AddSingleton<ProductionBatchService>();
        builder.Services.AddSingleton<IMachineRepository, SqliteMachineRepository>();
        builder.Services.AddSingleton<MachineService>();
        builder.Services.AddSingleton<IMachineDowntimeRepository, SqliteMachineDowntimeRepository>();
        builder.Services.AddSingleton<MachineDowntimeService>();
        builder.Services.AddSingleton<IMachineTypeRepository, SqliteMachineTypeRepository>();
        builder.Services.AddSingleton<MachineTypeService>();
        builder.Services.AddSingleton<IPostprocessorRepository, SqlitePostprocessorRepository>();
        builder.Services.AddSingleton<PostprocessorService>();
        builder.Services.AddSingleton<IWorkingCalendarRepository, SqliteWorkingCalendarRepository>();
        builder.Services.AddSingleton<WorkingCalendarService>();
        builder.Services.AddSingleton<IMachineAssignmentRepository, SqliteMachineAssignmentRepository>();
        builder.Services.AddSingleton<MachineAssignmentService>();
        builder.Services.AddSingleton<IPlanningBoardRepository, SqlitePlanningBoardRepository>();
        builder.Services.AddSingleton<PlanningBoardService>();
        builder.Services.AddSingleton<ITimelineSourceRepository, SqliteTimelineSourceRepository>();
        builder.Services.AddSingleton<TimelineCalculationEngine>();
        builder.Services.AddSingleton<TimelineProjectionService>();
        builder.Services.AddSingleton<ITvDashboardRepository, SqliteTvDashboardRepository>();
        builder.Services.AddSingleton<TvDashboardService>();
        builder.Services.AddSingleton<IEInkDeviceRepository, SqliteEInkDeviceRepository>();
        builder.Services.AddSingleton<EInkDeviceService>();
        builder.Services.AddSingleton<IEInkDeviceRegistrationRepository, SqliteEInkDeviceRegistrationRepository>();
        builder.Services.AddSingleton<EInkDeviceRegistrationService>();
        builder.Services.AddSingleton<IJobPackageRepository, SqliteJobPackageRepository>();
        builder.Services.AddSingleton<JobPackageService>();
        builder.Services.AddSingleton<OpenXmlLegacyWorkbookReader>();
        builder.Services.AddSingleton<ILegacyImportRepository, SqliteLegacyImportRepository>();
        builder.Services.AddSingleton<LegacyImportService>();
        builder.Services.AddSingleton<IKitaronConnectionRepository, SqliteKitaronConnectionRepository>();
        builder.Services.AddSingleton<IKitaronMappingRepository, SqliteKitaronMappingRepository>();
        builder.Services.AddSingleton<IKitaronConnectionTester, SqlServerKitaronConnectionTester>();
        builder.Services.AddSingleton<IKitaronSourceReader, SqlServerKitaronSourceReader>();
        builder.Services.AddSingleton<IKitaronSyncRepository, SqliteKitaronSyncRepository>();
        builder.Services.AddSingleton<KitaronConnectionService>();
        builder.Services.AddSingleton<KitaronMappingService>();
        builder.Services.AddSingleton<KitaronSyncService>();
        builder.Services.AddHostedService<KitaronConnectionMonitorService>();
        builder.Services.AddHostedService<KitaronSyncHostedService>();
        builder.Services.AddSingleton<IWeeklyMaterialReportRepository, SqliteWeeklyMaterialReportRepository>();
        builder.Services.AddSingleton<IMaterialReportEmailSender, SmtpMaterialReportEmailSender>();
        builder.Services.AddSingleton<WeeklyMaterialReportService>();
        builder.Services.AddHostedService<WeeklyMaterialReportScheduler>();
        builder.Services.AddSingleton<IWeeklyEmployeeEfficiencyRepository, SqliteWeeklyEmployeeEfficiencyRepository>();
        builder.Services.AddSingleton<IEmployeeEfficiencyEmailSender, SmtpEmployeeEfficiencyEmailSender>();
        builder.Services.AddSingleton<WeeklyEmployeeEfficiencyReportService>();
        builder.Services.AddHostedService<WeeklyEmployeeEfficiencyReportScheduler>();
        builder.Services.AddSingleton<IStructuredEventLogRepository, SqliteStructuredEventLogRepository>();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = serverOptions.ServiceName;
        });

        builder.WebHost.UseUrls(serverOptions.GetListenUrl());
        configureWebHost?.Invoke(builder.WebHost);

        var application = builder.Build();

        application.UseMiddleware<EInkReadOnlyGuardMiddleware>();

        application.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/kitaron-setup")
                && !KitaronConnectionEndpoints.IsLocalRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });

        var tvDashboardRoot = Path.Combine(
            AppContext.BaseDirectory,
            "wwwroot",
            "tv-dashboard");
        application.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = "/tv-dashboard",
            FileProvider = new PhysicalFileProvider(tvDashboardRoot)
        });
        var eInkSimulatorRoot = Path.Combine(
            AppContext.BaseDirectory,
            "wwwroot",
            "eink-simulator");
        application.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = "/eink-simulator",
            FileProvider = new PhysicalFileProvider(eInkSimulatorRoot)
        });
        var kitaronSetupRoot = Path.Combine(
            AppContext.BaseDirectory,
            "wwwroot",
            "kitaron-setup");
        application.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = "/kitaron-setup",
            FileProvider = new PhysicalFileProvider(kitaronSetupRoot)
        });

        application.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            service = serverOptions.ServiceName,
            version = GetServiceVersion(),
            serverTimeUtc = DateTimeOffset.UtcNow
        }));
        application.MapGet("/tv-dashboard/", () => Results.File(
            Path.Combine(tvDashboardRoot, "index.html"),
            "text/html; charset=utf-8"));
        application.MapGet("/eink-simulator/", () => Results.File(
            Path.Combine(eInkSimulatorRoot, "index.html"),
            "text/html; charset=utf-8"));
        application.MapGet("/kitaron-setup/", (HttpContext context) =>
            KitaronConnectionEndpoints.IsLocalRequest(context)
                ? Results.File(
                    Path.Combine(kitaronSetupRoot, "index.html"),
                    "text/html; charset=utf-8")
                : Results.NotFound());
        application.MapCaseEndpoints();
        application.MapAdministrativeSetupEndpoints();
        application.MapEditModeEndpoints();
        application.MapPlanningDeletionEndpoints();
        application.MapOrderEndpoints();
        application.MapProductionBatchEndpoints();
        application.MapMachineEndpoints();
        application.MapMachineDowntimeEndpoints();
        application.MapMachineTypeEndpoints();
        application.MapPostprocessorEndpoints();
        application.MapWorkingCalendarEndpoints();
        application.MapMachineAssignmentEndpoints();
        application.MapPlanningBoardEndpoints();
        application.MapTimelineEndpoints();
        application.MapTvDashboardEndpoints();
        application.MapEInkEndpoints();
        application.MapEInkDeviceRegistrationEndpoints();
        application.MapJobPackageEndpoints();
        application.MapLegacyImportEndpoints();
        application.MapKitaronConnectionEndpoints();
        application.MapWeeklyMaterialReportEndpoints();
        application.MapWeeklyEmployeeEfficiencyReportEndpoints();
        application.MapStructuredEventLogEndpoints();

        RegisterLifecycleLogging(application, serverOptions);

        return application;
    }

    private static string GetServiceVersion()
    {
        return typeof(ServerApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(ServerApplication).Assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static void RegisterLifecycleLogging(
        WebApplication application,
        ServerOptions serverOptions)
    {
        var logger = application.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Meimad.Planner.Server.Lifecycle");

        application.Lifetime.ApplicationStarted.Register(() =>
            logger.LogInformation(
                "Meimad Planner Server started at {ListenUrl} in {EnvironmentName}.",
                serverOptions.GetListenUrl(),
                application.Environment.EnvironmentName));

        application.Lifetime.ApplicationStopping.Register(() =>
            logger.LogInformation("Meimad Planner Server shutdown requested."));

        application.Lifetime.ApplicationStopped.Register(() =>
            logger.LogInformation("Meimad Planner Server stopped."));
    }
}
