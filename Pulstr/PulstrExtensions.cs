using Microsoft.Extensions.DependencyInjection;

namespace Pulstr
{
    public static class PulstrExtensions
    {
        /// <summary>
        /// Registers a <see cref="Pulstr{TEvent}"/> singleton that broadcasts messages of type <see cref="{TEvent}"/>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection AddPulstr<T>(this IServiceCollection services)
        {
            // Register the Pulstr singleton service with the DI container.
            services.AddSingleton(typeof(Pulstr<T>));
            
            // Return the modified service collection for further configuration if needed.
            return services;
        }
    }
}
