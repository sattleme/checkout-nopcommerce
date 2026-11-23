using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nop.Core.Infrastructure;
using Nop.Plugin.Payments.Sattleme.Services;

namespace Nop.Plugin.Misc.SattlemeE2e;

/// <summary>Registers the fake after the payment method's own services, so that it is the one resolved.</summary>
public class NopStartup : INopStartup
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<E2eGateway>();
        services.AddSingleton<ISattlemeGateway>(provider => provider.GetRequiredService<E2eGateway>());
    }

    /// <inheritdoc/>
    public void Configure(IApplicationBuilder application)
    {
    }

    /// <summary>After the payment method's (3000).</summary>
    public int Order => 3001;
}