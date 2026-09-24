using PluginBuilder.Builds.Services;

namespace PluginBuilder.BuildBroker;

// Only operator-authored diagnostics without secrets or host paths may use this type.
internal sealed class PublicBuildException(string message) : BuildServiceException(message);
