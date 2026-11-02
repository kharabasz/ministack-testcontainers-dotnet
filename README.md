# MiniStack.Testcontainers

A [Testcontainers for .NET](https://dotnet.testcontainers.org/) module for [MiniStack](https://github.com/ministackorg/ministack), a free, open-source AWS emulator that serves its AWS services on a single port (4566).

This is a community package. It is not affiliated with or maintained by the Testcontainers project or by MiniStack. It started as [testcontainers/testcontainers-dotnet#1806](https://github.com/testcontainers/testcontainers-dotnet/pull/1806).

## Install

```shell
dotnet add package MiniStack.Testcontainers
```

The package targets `net8.0`, `net9.0`, `net10.0`, `netstandard2.0` and `netstandard2.1`, and depends on the [`Testcontainers`](https://www.nuget.org/packages/Testcontainers) package. The types live in the `Testcontainers.MiniStack` namespace.

## Usage

```csharp
using Testcontainers.MiniStack;

var miniStackContainer = new MiniStackBuilder("ministackorg/ministack:1.5.31")
    .Build();

await miniStackContainer.StartAsync();

var config = new AmazonS3Config
{
    ServiceURL = miniStackContainer.GetConnectionString(),
    AuthenticationRegion = miniStackContainer.GetRegion(),
    ForcePathStyle = true,
};

var credentials = new BasicAWSCredentials(miniStackContainer.GetAccessKey(), miniStackContainer.GetSecretKey());

using var client = new AmazonS3Client(credentials, config);
```

Use `GetConnectionString()` as the AWS SDK `ServiceURL`, and `GetRegion()`, `GetAccessKey()` and `GetSecretKey()` for the region and credentials. By default, MiniStack uses the region `us-east-1` and the credentials `test`/`test`.

The container is ready once `/_ministack/health` returns HTTP 200 and then `/_ministack/ready` returns HTTP 200, which happens after all ready scripts have run.

## Configuration

The builder sets MiniStack's environment variables for the most common options:

```csharp
var miniStackContainer = new MiniStackBuilder("ministackorg/ministack:1.5.31")
    .WithRegion("eu-west-1")
    .WithCredentials("123456789012", "secret")
    .WithPersistence()
    .WithImdsV2Required()
    .Build();
```

- `WithRegion(string)` sets `MINISTACK_REGION`, the region MiniStack uses in the ARNs it generates.
- `WithCredentials(string, string)` sets `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY`. MiniStack treats a 12-digit access key as the account ID.
- `WithPersistence()` sets `PERSIST_STATE=1` and `S3_PERSIST=1`, so service state and S3 objects are written to disk and survive a restart of the same container.
- `WithImdsV2Required()` sets `MINISTACK_IMDS_V2_REQUIRED=1`, so instance metadata requests without a session token are rejected.
- `WithServices(params string[])` sets `SERVICES`, so MiniStack loads only the listed services, e.g. `WithServices("s3", "sqs")`.

`GetMiniStackVersion()` returns the image tag, or `null` for an image referenced only by digest. `GetMiniStackVersionAsync()` returns the version the running server reports, which also resolves tags such as `latest`.

If Testcontainers is configured with a hub image name prefix, the module passes it to MiniStack as `MINISTACK_IMAGE_PREFIX`, so the images MiniStack pulls for real infrastructure come from the same registry.

## Init and ready scripts

MiniStack runs shell (`.sh`) and Python (`.py`) scripts in alphabetical order of their file names. Use `WithInitScript(string)` or `WithInitScriptContent(string, string)` for boot scripts, which run before MiniStack serves requests. Use `WithReadyScript(string)` or `WithReadyScriptContent(string, string)` for ready scripts, which run once MiniStack serves requests and can create AWS resources:

```csharp
var miniStackContainer = new MiniStackBuilder("ministackorg/ministack:1.5.31")
    .WithInitScriptContent("01-boot.sh", "echo booted > /tmp/booted")
    .WithReadyScriptContent("01-create-bucket.sh", "aws s3 mb s3://seeded-bucket")
    .WithReadyScript("02-create-queue.py")
    .Build();
```

The ready scripts can use the AWS CLI (`aws`, `awslocal`) and boto3, which MiniStack preconfigures with its endpoint, region and credentials. If a ready script fails, `StartAsync` throws an `InvalidOperationException`, and the script output is in the container logs.

Older MiniStack images have no `/_ministack/ready` endpoint and return HTTP 404. For those, the container is ready once `/_ministack/health` returns HTTP 200, the module does not wait for ready scripts, and it cannot detect a failed one.

## Real infrastructure

`WithRealInfrastructure()` bind-mounts the Docker socket into the container. It uses the same socket path as the Resource Reaper, including the `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` setting. MiniStack then runs real containers for services such as RDS, ElastiCache, ECS and Lambda:

```csharp
var miniStackContainer = new MiniStackBuilder("ministackorg/ministack:1.5.31")
    .WithRealInfrastructure()
    .WithRdsPublicEndpoint()
    .Build();
```

By default, RDS reports the database container's address on the Docker network, which a test running on a macOS or Windows host cannot reach. `WithRdsPublicEndpoint()` sets `MINISTACK_RDS_PUBLIC_ENDPOINT=1`, so RDS reports `localhost` and the published host port instead. That address works when the tests and the Docker daemon run on the same machine.

> [!WARNING]
> Anything running inside MiniStack, including the code of a Lambda function, gets full control over the host's container engine. Use real infrastructure only on trusted machines and CI runners.

The Resource Reaper does not know about the containers MiniStack starts. When MiniStack is stopped, it removes them itself. Disposing the container kills MiniStack before it can do that, so the module then removes the containers and volumes that carry its `ministack.instance` label. MiniStack builds that label from `MINISTACK_HOSTNAME` (or the container hostname) and the gateway port. Containers of another MiniStack instance on the same Docker daemon are left alone, unless both instances set the same `MINISTACK_HOSTNAME` and port.

## Build and test

```shell
dotnet build -c Release
dotnet test -c Release
```

The tests start real MiniStack containers, so they need a running Docker daemon. The MiniStack image the tests use is pinned in `tests/Testcontainers.MiniStack.Tests/Dockerfile`.

## Releasing

Push a tag `v<version>`, e.g. `v1.0.1`. The `release` workflow builds, tests, packs with that version and pushes to nuget.org through trusted publishing (a nuget.org policy for this repo and `release.yml`).

## License

MIT. The test helpers `TestSession` and `DockerfileParser` are copied from [Testcontainers for .NET](https://github.com/testcontainers/testcontainers-dotnet), also MIT.
