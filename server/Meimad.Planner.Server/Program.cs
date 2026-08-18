namespace Meimad.Planner.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        WebApplication? application = null;

        try
        {
            application = ServerApplication.Build(args);
            await application.RunAsync();
            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                application?.Logger.LogCritical(exception, "Meimad Planner Server terminated unexpectedly.");
            }
            catch (Exception loggingException) when (loggingException is not OperationCanceledException)
            {
                // The Windows Event Log provider can already be disposed after host startup fails.
            }

            Console.Error.WriteLine($"Meimad Planner Server failed to start: {exception}");

            return 1;
        }
        finally
        {
            if (application is not null)
            {
                await application.DisposeAsync();
            }
        }
    }
}
