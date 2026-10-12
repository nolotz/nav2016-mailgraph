using System;
using System.Threading.Tasks;

namespace NavMailGraph.Internal
{
    /// <summary>
    /// Sync-over-async bridge for the C/AL-facing API. The work is queued to the thread pool so no caller
    /// SynchronizationContext is ever captured (the NAV service tier has none, but the CLI and tests may).
    /// Exceptions are unwrapped (no AggregateException).
    /// </summary>
    internal static class AsyncHelper
    {
        internal static T RunSync<T>(Func<Task<T>> func)
        {
            if (func == null)
            {
                throw new ArgumentNullException(nameof(func));
            }

            return Task.Run(func).GetAwaiter().GetResult();
        }

        internal static void RunSync(Func<Task> func)
        {
            if (func == null)
            {
                throw new ArgumentNullException(nameof(func));
            }

            Task.Run(func).GetAwaiter().GetResult();
        }
    }
}
