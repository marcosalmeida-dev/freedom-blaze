var builder = DistributedApplication.CreateBuilder(args);

//builder.AddDockerfile("freedomblaze-phoenixd", "../submodules/phoenixd.NET/Phoenixd.NET/.docker/phoenixd")
//                     .WithBindMount("freedomblaze-phoenixd-config", "/phoenix/.phoenix")
//                     .WithEndpoint(port: 9741, targetPort: 9740)
//                     .PublishAsContainer();

builder.AddProject<Projects.FreedomBlaze>(nameof(Projects.FreedomBlaze).ToLower()).WithExternalHttpEndpoints();

builder.Build().Run();
