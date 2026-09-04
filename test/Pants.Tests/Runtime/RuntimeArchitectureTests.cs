using System.Reflection;

namespace Cntryl.Pants.Runtime;

public sealed class RuntimeArchitectureTests
{
    [Fact]
    public void ShouldOpenCoordinatorAsynchronouslyThroughCompositionRoot()
    {
        var constructors = typeof(Actor).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var open = Assert.Single(typeof(Actor).GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            static method => method.Name == nameof(Actor.OpenAsync));

        Assert.All(constructors, static constructor => Assert.True(constructor.IsPrivate));
        Assert.Equal(typeof(ValueTask<Actor>), open.ReturnType);
        Assert.Equal(typeof(PantsDatabase).Assembly, typeof(RuntimePlan).Assembly);
        Assert.Equal(typeof(PantsDatabase).Assembly, typeof(RuntimeComposition).Assembly);
    }

    [Theory]
    [InlineData(typeof(WalRuntimeService), typeof(ILocalWalStore))]
    [InlineData(typeof(FlushRuntimeService), typeof(ILocalFlushStore))]
    [InlineData(typeof(CompactionRuntimeService), typeof(ILocalCompactionStore))]
    public void ShouldDependOnFocusedStoragePorts(Type serviceType, Type portType)
    {
        var parameterTypes = serviceType.GetConstructors()
            .SelectMany(static constructor => constructor.GetParameters())
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(portType, parameterTypes);
        Assert.DoesNotContain(typeof(LocalDiskStore), parameterTypes);
    }

    [Fact]
    public void ShouldKeepDerivedRuntimePolicyOutOfPublicOptions()
    {
        var publicProperties = typeof(PantsOpenOptions).GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("MemoryBudgetBytes", publicProperties);
        Assert.DoesNotContain("MemtableSizeLimitBytes", publicProperties);
        Assert.DoesNotContain("RuntimeResponseTimeout", publicProperties);
        Assert.Contains(nameof(PantsOpenOptions.Runtime), publicProperties);
        Assert.Contains(nameof(PantsOpenOptions.Memory), publicProperties);
        Assert.Contains(nameof(PantsOpenOptions.Lease), publicProperties);
    }
}
