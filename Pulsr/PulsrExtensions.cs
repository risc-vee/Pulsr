using Microsoft.Extensions.DependencyInjection;

namespace Pulsr
{
    public static class PulsrExtensions
    {
        /// <summary>
        /// Registers a <see cref="Pulsr{TMessage}"/> singleton that broadcasts messages of type <see cref="{TMessage}"/>
        /// </summary>
        /// <typeparam name="TMessage">The type of messages to broadcast.</typeparam>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection AddPulstr<TMessage>(this IServiceCollection services)
        {
            // Register the Pulstr singleton service with the DI container.
            services.AddSingleton(typeof(Pulsr<TMessage>));
            
            // Return the modified service collection for further configuration if needed.
            return services;
        }
    }
}
