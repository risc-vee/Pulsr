using Microsoft.Extensions.DependencyInjection;

namespace Pulsr
{
    public static class PulsrExtensions
    {
        /// <summary>
        /// Registers a <see cref="Pulsr{TEvent}"/> singleton that broadcasts messages of type <see cref="{TEvent}"/>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection AddPulstr<T>(this IServiceCollection services)
        {
            // Register the Pulstr singleton service with the DI container.
            services.AddSingleton(typeof(Pulsr<T>));
            
            // Return the modified service collection for further configuration if needed.
            return services;
        }
    }
}
