var builder = DistributedApplication.CreateBuilder(args);

var phoenixdContainer = builder.AddDockerfile("freedomblaze-phoenixd", "../submodules/phoenixd.NET/Phoenixd.NET/.docker/phoenixd")
                               .WithBindMount("freedomblaze-phoenixd-config", "/phoenix/.phoenix")
                               .WithEndpoint(port: 9741, targetPort: 9740);

builder.AddProject<Projects.FreedomBlaze>("freedomblaze");

builder.Build().Run();
