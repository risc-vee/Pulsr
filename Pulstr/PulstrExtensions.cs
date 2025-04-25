using Microsoft.Extensions.DependencyInjection;

namespace Pulstr
{
    public static class PulstrExtensions
    {
        public static IServiceCollection AddPulstr(this IServiceCollection services)
        {
            // Register the Pulstr singleton service with the DI container.
            services.AddSingleton(typeof(Pulstr<>));
            
            // Return the modified service collection for further configuration if needed.
            return services;
        }
    }
}
