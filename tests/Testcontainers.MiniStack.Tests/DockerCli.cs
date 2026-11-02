namespace DotNet.Testcontainers.Commons;

/// <summary>
/// Trait names copied from the Testcontainers for .NET test suite, so the
/// tests can be filtered the same way, e.g. <c>--filter DockerPlatform=Linux</c>.
/// </summary>
public static class DockerCli
{
    public enum DockerPlatform
    {
        Linux,
    }
}
