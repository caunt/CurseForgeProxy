using CurseForgeProxy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<EnvironmentConfiguration>();
builder.Services.AddSingleton<CurseForgeEndpoints>();

builder.Services
    .AddEgressPool()
    .AddHttpClient(name: CurseForgeEndpoints.HttpClientName)
    .UseEgressPool();

var app = builder.Build();

app.Services
    .GetRequiredService<CurseForgeEndpoints>()
    .ConfigureRoutes(app);

app.Run();

public partial class Program;
