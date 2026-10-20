using System;

if (args.Length != 2 || args[0] != "-XshowSettings:properties" || args[1] != "-version")
{
    return 2;
}

Console.Error.WriteLine("    java.version = 21.0.99-synthetic");
Console.Error.WriteLine("    java.vendor = Servitium Synthetic Fixture");
Console.Error.WriteLine("    java.vm.name = Servitium Test VM");
Console.Error.WriteLine("    os.arch = amd64");
return 0;

namespace Servitium.JavaProbeFixture
{
    internal static class FixtureDescriptor;
}
