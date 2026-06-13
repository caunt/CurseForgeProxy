namespace CurseForgeProxy;

public sealed class EnvironmentConfiguration
{
    public string? CurseForgeApiKey { get; } = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
}
