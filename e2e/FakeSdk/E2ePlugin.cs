using Nop.Services.Common;
using Nop.Services.Plugins;

namespace Nop.Plugin.Misc.SattlemeE2e;

/// <summary>The test-only plugin: installed, it replaces the payment method's SDK (NopStartup).</summary>
public class E2ePlugin : BasePlugin, IMiscPlugin
{
}