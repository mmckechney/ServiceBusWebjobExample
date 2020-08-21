using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace ServiceBusWebJobCore
{
    class Program
    {
        static async Task Main()
        {
            var builder = new HostBuilder();
            builder.ConfigureWebJobs(b =>
            {
                b.AddAzureStorageCoreServices();
                b.AddServiceBus(sbOptions =>
                {
                    sbOptions.MessageHandlerOptions.AutoComplete = false;
                    sbOptions.MessageHandlerOptions.MaxAutoRenewDuration = new TimeSpan(0,0,60);
                    sbOptions.MessageHandlerOptions.MaxConcurrentCalls = 16;
                });
            });

            builder.ConfigureAppConfiguration(c =>
            {

                c.AddJsonFile("appsettings.json");

            });
            var host = builder.Build();
            using (host)
            {
                await host.RunAsync();
            }
        }
    }
}
