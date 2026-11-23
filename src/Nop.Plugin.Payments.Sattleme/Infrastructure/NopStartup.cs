using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nop.Core.Infrastructure;
using Nop.Plugin.Payments.Sattleme.Factories;
using Nop.Plugin.Payments.Sattleme.Services;

namespace Nop.Plugin.Payments.Sattleme.Infrastructure;

/// <summary>
/// The plugin's services. The SDK's clients live for the application (one per integration key, as
/// the SDK's README asks), everything else for a request, as nopCommerce's own services.
/// </summary>
public partial class NopStartup : INopStartup
{
    /// <summary>Registers the services, and the filter that keeps a customer from cancelling an order a payment may still come for.</summary>
    public virtual void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SdkLogger>();
        services.AddSingleton<ISattlemeGateway, SattlemeGateway>();

        services.AddScoped<CustomerMessages>();
        services.AddScoped<IntegrationKeys>();
        services.AddScoped<InvoiceMaker>();
        services.AddScoped<OrderNotes>();
        services.AddScoped<PaymentChecker>();
        services.AddScoped<PaymentModelFactory>();
        services.AddScoped<PersonalDataShredder>();
        services.AddScoped<PluginResources>();
        services.AddScoped<SattlemeStore>();
        services.AddScoped<CancelOrderFilter>();

        services.Configure<MvcOptions>(options => options.Filters.AddService<CancelOrderFilter>());
    }

    /// <summary>Nothing to add to the request pipeline.</summary>
    public virtual void Configure(IApplicationBuilder application)
    {
    }

    /// <summary>After nopCommerce's own services.</summary>
    public int Order => 3000;
}