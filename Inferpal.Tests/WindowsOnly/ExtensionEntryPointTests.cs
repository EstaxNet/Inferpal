using System;
using System.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The extension entry point is created by the extensibility host, by reflection over its constructors. The Visual
/// Studio 2022 host lists the type initializer among them and invokes it: "Type initializer was not callable", the
/// extension never exists, and no command or tool window of the out-of-process half works — while the in-process
/// half loads and nothing in Visual Studio's own log says why (only the ServiceHub log of the extensibility host).
/// </summary>
public class ExtensionEntryPointTests
{
    [Fact]
    public void EveryExtensionEntryPoint_HasNoTypeInitializer()
    {
        var entryPoints = typeof(InferpalExtension).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && DerivesFromTheSdkExtension(t))
            .ToList();

        // Witness: the scan reads something, otherwise the rule below is green on nothing.
        Assert.Contains(typeof(InferpalExtension), entryPoints);

        var withInitializer = entryPoints.Where(t => t.TypeInitializer is not null).Select(t => t.FullName).ToList();
        Assert.True(withInitializer.Count == 0,
            "A static constructor (or a static member with an initializer) on an extension entry point makes the "
            + "Visual Studio 2022 host fail to create it: " + string.Join(", ", withInitializer));
    }

    // The SDK's Extension type is not referenced at compile time here (its package is private to the VSIX project):
    // the base chain is read by name.
    private static bool DerivesFromTheSdkExtension(Type t)
    {
        for (var b = t.BaseType; b is not null; b = b.BaseType)
            if (b.FullName == "Microsoft.VisualStudio.Extensibility.Extension") return true;
        return false;
    }

    [Fact]
    public void TheEntryPoint_HasThePublicParameterlessConstructorTheHostCalls()
        => Assert.NotNull(typeof(InferpalExtension).GetConstructor(Type.EmptyTypes));
}
