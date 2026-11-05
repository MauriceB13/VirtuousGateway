namespace VirtuousGateway.MigrationRehearsal;

internal static class Program
{
    internal static Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            Console.Error.WriteLine("This rehearsal accepts no arguments.");
            return Task.FromResult(2);
        }

        Console.Error.WriteLine("V0001_REHEARSAL_DISABLED_NO_INDEPENDENT_TARGET_ATTESTATION");
        return Task.FromResult(3);
    }
}
