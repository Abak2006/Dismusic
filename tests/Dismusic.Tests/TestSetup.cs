using System.Runtime.CompilerServices;
using Dismusic.Core;

namespace Dismusic.Tests;

internal static class TestSetup
{
    /// <summary>Keep test runs out of the real app log.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Log.FilePath = Path.Combine(Path.GetTempPath(), "dismusic-tests.log");
}
