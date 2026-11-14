using System.Runtime.CompilerServices;
using Bunit;

namespace PremiereCalendar.ComponentTests;

internal static class BunitWaitConfiguration
{
    // bUnit's WaitForAssertion/WaitForState default to a 1 second budget. With every test case running in parallel
    // (ParallelMode.All) on a shared, loaded host, a render can legitimately take longer than that. A wait returns as
    // soon as its condition holds, so a wider budget only delays genuine failures; explicit per-call timeouts still win.
    [ModuleInitializer]
    internal static void Initialize() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);
}
