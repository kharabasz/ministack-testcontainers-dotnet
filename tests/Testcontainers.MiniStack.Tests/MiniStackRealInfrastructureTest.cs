namespace Testcontainers.MiniStack;

public sealed class MiniStackRealInfrastructureTest : IAsyncLifetime
{
    private const string InstanceLabel = "ministack.instance";

    // # --8<-- [start:RealInfrastructureMiniStackContainer]
    private readonly MiniStackContainer _miniStackContainer = new MiniStackBuilder(TestSession.GetImageFromDockerfile())
        .WithRealInfrastructure()
        .WithRdsPublicEndpoint()
        .Build();
    // # --8<-- [end:RealInfrastructureMiniStackContainer]

    public async ValueTask InitializeAsync()
    {
        await _miniStackContainer.StartAsync()
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return _miniStackContainer.DisposeAsync();
    }

    private AWSCredentials Credentials => new BasicAWSCredentials(_miniStackContainer.GetAccessKey(), _miniStackContainer.GetSecretKey());

    private TConfig GetConfig<TConfig>()
        where TConfig : ClientConfig, new()
    {
        var config = new TConfig();
        config.ServiceURL = _miniStackContainer.GetConnectionString();
        config.AuthenticationRegion = _miniStackContainer.GetRegion();
        return config;
    }

    [Fact]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    public async Task InvokeReturnsFunctionResult()
    {
        // Given
        const string handler = "def handler(event, context):\n    return {'echo': event['name']}\n";

        using var client = new AmazonLambdaClient(Credentials, GetConfig<AmazonLambdaConfig>());

        var functionName = Guid.NewGuid().ToString("N");

        var createFunctionRequest = new CreateFunctionRequest();
        createFunctionRequest.FunctionName = functionName;
        createFunctionRequest.Runtime = Amazon.Lambda.Runtime.Python312;
        createFunctionRequest.Handler = "handler.handler";
        createFunctionRequest.Role = "arn:aws:iam::000000000000:role/lambda";
        createFunctionRequest.Code = new FunctionCode { ZipFile = CreateZipArchive("handler.py", handler) };

        var invokeRequest = new InvokeRequest();
        invokeRequest.FunctionName = functionName;
        invokeRequest.Payload = "{\"name\":\"ministack\"}";

        // When
        _ = await client.CreateFunctionAsync(createFunctionRequest, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        await WaitUntilAsync(async () => Amazon.Lambda.State.Active.Equals((await client.GetFunctionConfigurationAsync(functionName, TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).State))
            .ConfigureAwait(true);

        var invokeResponse = await client.InvokeAsync(invokeRequest, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // Then
        Assert.Null(invokeResponse.FunctionError);
        Assert.Equal("{\"echo\": \"ministack\"}", Encoding.UTF8.GetString(invokeResponse.Payload.ToArray()));
    }

    [Fact]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    public async Task CreateDbInstanceReturnsReachablePublicEndpoint()
    {
        // Given
        using var client = new AmazonRDSClient(Credentials, GetConfig<AmazonRDSConfig>());

        var createDbInstanceRequest = new CreateDBInstanceRequest();
        createDbInstanceRequest.DBInstanceIdentifier = "db-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        createDbInstanceRequest.DBInstanceClass = "db.t3.micro";
        createDbInstanceRequest.Engine = "postgres";
        createDbInstanceRequest.MasterUsername = "admin";
        createDbInstanceRequest.MasterUserPassword = "password";
        createDbInstanceRequest.DBName = "postgres";
        createDbInstanceRequest.AllocatedStorage = 20;

        // When
        var createDbInstanceResponse = await client.CreateDBInstanceAsync(createDbInstanceRequest, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var endpoint = createDbInstanceResponse.DBInstance.Endpoint;

        // Then
        Assert.Equal("localhost", endpoint.Address);

        await WaitUntilAsync(async () =>
            {
                using var tcpClient = new TcpClient();

                try
                {
                    await tcpClient.ConnectAsync(_miniStackContainer.Hostname, endpoint.Port.GetValueOrDefault(), TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);

                    return true;
                }
                catch (SocketException)
                {
                    return false;
                }
            })
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    public async Task DisposeRemovesOnlyOwnNestedContainers()
    {
        // Given
        using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.Empty).Build();

        var miniStackContainer = new MiniStackBuilder(TestSession.GetImageFromDockerfile())
            .WithRealInfrastructure()
            .Build();

        // Stands in for a nested container that another MiniStack instance created on the same Docker daemon.
        var foreignContainer = await dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = TestSession.GetImageFromDockerfile(),
            Entrypoint = new[] { "sleep", "300" },
            Labels = new Dictionary<string, string> { { "ministack", "rds" }, { InstanceLabel, "foreign:4566" } },
        }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        try
        {
            await miniStackContainer.StartAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            var containerInspectResponse = await dockerClient.Containers.InspectContainerAsync(miniStackContainer.Id, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            var instanceId = containerInspectResponse.Config.Hostname + ":" + MiniStackBuilder.MiniStackPort;

            var config = new AmazonElastiCacheConfig();
            config.ServiceURL = miniStackContainer.GetConnectionString();
            config.AuthenticationRegion = miniStackContainer.GetRegion();

            using var client = new AmazonElastiCacheClient(new BasicAWSCredentials(miniStackContainer.GetAccessKey(), miniStackContainer.GetSecretKey()), config);

            var createCacheClusterRequest = new CreateCacheClusterRequest();
            createCacheClusterRequest.CacheClusterId = Guid.NewGuid().ToString("N").Substring(0, 20);
            createCacheClusterRequest.Engine = "redis";
            createCacheClusterRequest.CacheNodeType = "cache.t3.micro";
            createCacheClusterRequest.NumCacheNodes = 1;

            _ = await client.CreateCacheClusterAsync(createCacheClusterRequest, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            // MiniStack creates the Redis container in the background when it has to pull the image first.
            await WaitUntilAsync(async () => (await ListContainersAsync(dockerClient, instanceId)
                    .ConfigureAwait(false)).Any())
                .ConfigureAwait(true);

            var nestedContainersBeforeDispose = await ListContainersAsync(dockerClient, instanceId)
                .ConfigureAwait(true);

            // When
            await miniStackContainer.DisposeAsync()
                .ConfigureAwait(true);

            var nestedContainersAfterDispose = await ListContainersAsync(dockerClient, instanceId)
                .ConfigureAwait(true);

            var foreignContainerInspectResponse = await dockerClient.Containers.InspectContainerAsync(foreignContainer.ID, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            // Then
            Assert.NotEmpty(nestedContainersBeforeDispose);
            Assert.Empty(nestedContainersAfterDispose);
            Assert.Equal(foreignContainer.ID, foreignContainerInspectResponse.ID);
        }
        finally
        {
            await miniStackContainer.DisposeAsync()
                .ConfigureAwait(true);

            await dockerClient.Containers.RemoveContainerAsync(foreignContainer.ID, new ContainerRemoveParameters { Force = true }, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    public async Task StartRemovesOnlyNestedResourcesOfRemovedInstances()
    {
        // Given
        using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.Empty).Build();

        // Stands in for a MiniStack container that the Resource Reaper removed after the test host died.
        var removedInstance = await CreateContainerAsync(dockerClient, new Dictionary<string, string>())
            .ConfigureAwait(true);

        await dockerClient.Containers.RemoveContainerAsync(removedInstance, new ContainerRemoveParameters { Force = true }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // Stands in for a MiniStack container that still exists.
        var existingInstance = await CreateContainerAsync(dockerClient, new Dictionary<string, string>())
            .ConfigureAwait(true);

        var orphanedInstanceId = removedInstance.Substring(0, 12) + ":4566";

        var orphanedContainer = await CreateContainerAsync(dockerClient, new Dictionary<string, string> { { "ministack", "lambda" }, { InstanceLabel, orphanedInstanceId } })
            .ConfigureAwait(true);

        var existingInstanceContainer = await CreateContainerAsync(dockerClient, new Dictionary<string, string> { { "ministack", "lambda" }, { InstanceLabel, existingInstance.Substring(0, 12) + ":4566" } })
            .ConfigureAwait(true);

        // A MiniStack running on the host machine labels its containers with the machine name.
        var hostInstanceContainer = await CreateContainerAsync(dockerClient, new Dictionary<string, string> { { "ministack", "lambda" }, { InstanceLabel, "ministack-host:4566" } })
            .ConfigureAwait(true);

        var orphanedVolume = await dockerClient.Volumes.CreateAsync(new VolumesCreateParameters { Name = Guid.NewGuid().ToString("N"), Labels = new Dictionary<string, string> { { InstanceLabel, orphanedInstanceId } } }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var miniStackContainer = new MiniStackBuilder(TestSession.GetImageFromDockerfile())
            .WithRealInfrastructure()
            .Build();

        miniStackContainer.OrphanGracePeriod = TimeSpan.Zero;

        try
        {
            // When
            await miniStackContainer.StartAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            // Then
            Assert.False(await ContainerExistsAsync(dockerClient, orphanedContainer).ConfigureAwait(true));
            Assert.False(await VolumeExistsAsync(dockerClient, orphanedVolume.Name).ConfigureAwait(true));
            Assert.True(await ContainerExistsAsync(dockerClient, existingInstanceContainer).ConfigureAwait(true));
            Assert.True(await ContainerExistsAsync(dockerClient, hostInstanceContainer).ConfigureAwait(true));
        }
        finally
        {
            await miniStackContainer.DisposeAsync()
                .ConfigureAwait(true);

            foreach (var id in new[] { orphanedContainer, existingInstanceContainer, hostInstanceContainer, existingInstance })
            {
                await RemoveContainerIfExistsAsync(dockerClient, id)
                    .ConfigureAwait(true);
            }

            if (await VolumeExistsAsync(dockerClient, orphanedVolume.Name).ConfigureAwait(true))
            {
                await dockerClient.Volumes.RemoveAsync(orphanedVolume.Name, null, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            }
        }
    }

    [Theory]
    [Trait(nameof(DockerCli.DockerPlatform), nameof(DockerCli.DockerPlatform.Linux))]
    [InlineData(true, 120)]
    [InlineData(false, 0)]
    public async Task StartKeepsOrphanWhenTooYoungOrSweepIsDisabled(bool sweepOrphans, int gracePeriodSeconds)
    {
        // Given
        using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.Empty).Build();

        var removedInstance = await CreateContainerAsync(dockerClient, new Dictionary<string, string>())
            .ConfigureAwait(true);

        await dockerClient.Containers.RemoveContainerAsync(removedInstance, new ContainerRemoveParameters { Force = true }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var orphanedContainer = await CreateContainerAsync(dockerClient, new Dictionary<string, string> { { "ministack", "lambda" }, { InstanceLabel, removedInstance.Substring(0, 12) + ":4566" } })
            .ConfigureAwait(true);

        var miniStackContainer = new MiniStackBuilder(TestSession.GetImageFromDockerfile())
            .WithRealInfrastructure()
            .WithOrphanSweep(sweepOrphans)
            .Build();

        miniStackContainer.OrphanGracePeriod = TimeSpan.FromSeconds(gracePeriodSeconds);

        try
        {
            // When
            await miniStackContainer.StartAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            // Then
            Assert.True(await ContainerExistsAsync(dockerClient, orphanedContainer).ConfigureAwait(true));
        }
        finally
        {
            await miniStackContainer.DisposeAsync()
                .ConfigureAwait(true);

            await RemoveContainerIfExistsAsync(dockerClient, orphanedContainer)
                .ConfigureAwait(true);
        }
    }

    private static async Task<string> CreateContainerAsync(Docker.DotNet.IDockerClient dockerClient, IDictionary<string, string> labels)
    {
        var createContainerResponse = await dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = TestSession.GetImageFromDockerfile(),
            Entrypoint = new[] { "sleep", "300" },
            Labels = labels,
        }, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return createContainerResponse.ID;
    }

    private static async Task<bool> ContainerExistsAsync(Docker.DotNet.IDockerClient dockerClient, string id)
    {
        var filters = new Dictionary<string, IDictionary<string, bool>> { { "id", new Dictionary<string, bool> { { id, true } } } };
        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = filters }, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return containers.Any(container => id.Equals(container.ID, StringComparison.Ordinal));
    }

    private static async Task<bool> VolumeExistsAsync(Docker.DotNet.IDockerClient dockerClient, string name)
    {
        var filters = new Dictionary<string, IDictionary<string, bool>> { { "name", new Dictionary<string, bool> { { name, true } } } };
        var volumes = await dockerClient.Volumes.ListAsync(new VolumesListParameters { Filters = filters }, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return (volumes.Volumes ?? new List<VolumeResponse>()).Any(volume => name.Equals(volume.Name, StringComparison.Ordinal));
    }

    private static async Task RemoveContainerIfExistsAsync(Docker.DotNet.IDockerClient dockerClient, string id)
    {
        if (await ContainerExistsAsync(dockerClient, id).ConfigureAwait(false))
        {
            await dockerClient.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Task<IList<ContainerListResponse>> ListContainersAsync(Docker.DotNet.IDockerClient dockerClient, string instanceId)
    {
        var filters = new Dictionary<string, IDictionary<string, bool>> { { "label", new Dictionary<string, bool> { { InstanceLabel + "=" + instanceId, true } } } };
        return dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = filters }, TestContext.Current.CancellationToken);
    }

    private static MemoryStream CreateZipArchive(string entryName, string content)
    {
        var memoryStream = new MemoryStream();

        using (var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            using var streamWriter = new StreamWriter(zipArchive.CreateEntry(entryName).Open());
            streamWriter.Write(content);
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var timeout = DateTime.UtcNow.AddMinutes(2);

        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > timeout)
            {
                throw new TimeoutException("The condition was not met within 2 minutes.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
    }
}
