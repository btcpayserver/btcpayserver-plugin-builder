using Xunit;

namespace PluginBuilder.Tests;

[CollectionDefinition(nameof(NonParallelizableCollectionDefinition), DisableParallelization = true)]
public class NonParallelizableCollectionDefinition;
