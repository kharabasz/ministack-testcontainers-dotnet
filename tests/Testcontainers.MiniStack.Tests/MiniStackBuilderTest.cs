namespace Testcontainers.MiniStack;

public sealed class MiniStackBuilderTest
{
    [Theory]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    [InlineData("")]
    [InlineData(" ")]
    public void ThrowsArgumentExceptionWhenRegionIsEmpty(string region)
    {
        var miniStackBuilder = new MiniStackBuilder(TestSession.GetImageFromDockerfile()).WithRegion(region);
        Assert.Throws<ArgumentException>(miniStackBuilder.Build);
    }

    [Theory]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    [InlineData("", MiniStackBuilder.SecretKey)]
    [InlineData(MiniStackBuilder.AccessKey, "")]
    public void ThrowsArgumentExceptionWhenCredentialsAreIncomplete(string accessKey, string secretKey)
    {
        var miniStackBuilder = new MiniStackBuilder(TestSession.GetImageFromDockerfile()).WithCredentials(accessKey, secretKey);
        Assert.Throws<ArgumentException>(miniStackBuilder.Build);
    }

    [Fact]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    public void AccessorsReturnDefaultsWhenNotConfigured()
    {
        var miniStackContainer = new MiniStackBuilder(TestSession.GetImageFromDockerfile()).Build();
        Assert.Equal(MiniStackBuilder.Region, miniStackContainer.GetRegion());
        Assert.Equal(MiniStackBuilder.AccessKey, miniStackContainer.GetAccessKey());
        Assert.Equal(MiniStackBuilder.SecretKey, miniStackContainer.GetSecretKey());
    }

    [Fact]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    public void ThrowsArgumentExceptionWhenServicesAreEmpty()
    {
        var miniStackBuilder = new MiniStackBuilder(TestSession.GetImageFromDockerfile()).WithServices();
        Assert.Throws<ArgumentException>(miniStackBuilder.Build);
    }

    [Theory]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    [ClassData(typeof(InvalidScriptConfigurations))]
    public void ThrowsArgumentExceptionWhenScriptIsInvalid(MiniStackBuilder miniStackBuilder)
    {
        Assert.Throws<ArgumentException>(miniStackBuilder.Build);
    }

    private sealed class InvalidScriptConfigurations : TheoryData<MiniStackBuilder>
    {
        public InvalidScriptConfigurations()
        {
            var miniStackBuilder = new MiniStackBuilder(TestSession.GetImageFromDockerfile());
            Add(miniStackBuilder.WithInitScript("01-boot.txt"));
            Add(miniStackBuilder.WithInitScriptContent("scripts/01-boot.sh", string.Empty));
            Add(miniStackBuilder.WithInitScriptContent("01-boot.sh", null));
            Add(miniStackBuilder.WithReadyScript("01-seed.js"));
            Add(miniStackBuilder.WithReadyScriptContent("scripts\\01-seed.sh", string.Empty));
            Add(miniStackBuilder.WithReadyScriptContent(string.Empty, string.Empty));
            Add(miniStackBuilder.WithReadyScriptContent("01-seed.py", null));
            Add(miniStackBuilder.WithReadyScript("02-create-queue.py").WithReadyScriptContent("02-create-queue.py", string.Empty));
            Add(miniStackBuilder.WithInitScriptContent("01-boot.sh", string.Empty).WithInitScriptContent("01-boot.sh", string.Empty));
        }
    }

    [Fact]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    public void SameFileNameIsAllowedForAnInitAndAReadyScript()
    {
        var miniStackBuilder = new MiniStackBuilder(TestSession.GetImageFromDockerfile()).WithInitScriptContent("01.sh", string.Empty).WithReadyScriptContent("01.sh", string.Empty);
        _ = miniStackBuilder.Build();
    }

    [Theory]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    [InlineData(0)]
    [InlineData(-1)]
    public void ThrowsArgumentOutOfRangeExceptionWhenMemoryLimitIsNotPositive(long memoryLimit)
    {
        var miniStackBuilder = new MiniStackBuilder(TestSession.GetImageFromDockerfile()).WithMemoryLimit(memoryLimit);
        Assert.Throws<ArgumentOutOfRangeException>(miniStackBuilder.Build);
    }

    [Fact]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    public async Task MemoryLimitIsSetWithoutSwap()
    {
        // Given
        const long memoryLimit = 512L * 1024 * 1024;

        using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.Empty).Build();

        await using var miniStackContainer = new MiniStackBuilder(TestSession.GetImageFromDockerfile())
            .WithMemoryLimit(memoryLimit)
            .Build();

        // When
        await miniStackContainer.StartAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var containerInspectResponse = await dockerClient.Containers.InspectContainerAsync(miniStackContainer.Id, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // Then
        Assert.Equal(memoryLimit, containerInspectResponse.HostConfig.Memory);
        Assert.Equal(memoryLimit, containerInspectResponse.HostConfig.MemorySwap);
    }

    [Fact]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    public async Task StartThrowsDescriptiveExceptionWhenOomKilled()
    {
        // Given
        await using var miniStackContainer = new MiniStackBuilder(TestSession.GetImageFromDockerfile())
            .WithMemoryLimit(16L * 1024 * 1024)
            .Build();

        // When
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => miniStackContainer.StartAsync(TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        // Then
        Assert.Equal("MiniStack was OOM-killed (memory limit 16 MiB).", exception.Message);
        Assert.NotNull(exception.InnerException);
    }
}
