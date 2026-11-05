namespace VirtuousGateway.CiMigrationRehearsal;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var context = RehearsalPolicy.ValidateExecutionContext(args, Environment.GetEnvironmentVariable);
        if (context is null)
        {
            Console.Error.WriteLine("V0001_CI_REHEARSAL_REFUSED_OUTSIDE_REVIEWED_DISPATCH");
            return 3;
        }

        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        var artifacts = RehearsalPolicy.LoadAndValidateArtifacts(
            Path.Combine(artifactDirectory, "V0001.sql"),
            Path.Combine(artifactDirectory, "V0001.manifest.json"));

        await CiMigrationRehearsal.RunAsync(context.BootstrapPassword, artifacts);
        Console.WriteLine("V0001_CI_REHEARSAL_ROLLED_BACK_AND_VERIFIED");
        return 0;
    }
}
