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
            if (application is not null)
            {
                application.Logger.LogCritical(exception, "Meimad Planner Server terminated unexpectedly.");
            }
            else
            {
                Console.Error.WriteLine($"Meimad Planner Server failed to start: {exception.Message}");
            }

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
