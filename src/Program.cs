using CurseForgeProxy;

using Egress;

using Microsoft.Extensions.DependencyInjection.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

ILoggingBuilder logging = builder.Logging
    .SetMinimumLevel(LogLevel.Warning)
    .AddFilter(nameof(CurseForgeProxy), LogLevel.Debug)
    .AddFilter(nameof(Egress), LogLevel.Debug);

IServiceCollection services = logging.Services
    .AddEgressPool(
        static options =>
        {
            options.AddressMode = EgressAddressMode.AssignOnDemand;
            options.SpareAddressCount = 32;
            options.NewAddressRateLimit = 50;
        }
    )
    .AddHttpClient(name: CurseForgeEndpoints.HttpClientName)
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(
        static serviceProvider =>
        {
            SocketsHttpHandler handler = serviceProvider.GetRequiredService<EgressPool>().CreateHttpMessageHandler();
            CurseForgeEndpoints.ConfigureUpstreamHandler(handler);

            return handler;
        }
    )
    .Services;

services.TryAddSingleton(TimeProvider.System);
services.Add(ServiceDescriptor.Singleton<EnvironmentConfiguration, EnvironmentConfiguration>());
services.Add(ServiceDescriptor.Singleton<CloudFrontAddressSelector, CloudFrontAddressSelector>());
services.Add(ServiceDescriptor.Singleton<CurseForgeEndpoints, CurseForgeEndpoints>());

WebApplication app = builder.Build();

app.Services.GetRequiredService<CurseForgeEndpoints>().ConfigurePipeline(app);
app.Run();
