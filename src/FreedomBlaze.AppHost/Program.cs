var builder = DistributedApplication.CreateBuilder(args);

//builder.AddDockerfile("freedomblaze-phoenixd", "../submodules/phoenixd.NET/Phoenixd.NET/.docker/phoenixd")
//                     .WithBindMount("freedomblaze-phoenixd-config", "/phoenix/.phoenix")
//                     .WithEndpoint(port: 9741, targetPort: 9740)
//                     .PublishAsContainer();

// Automatically provision an Application Insights resource
var insights = builder.AddAzureApplicationInsights("fb-applicationinsights");

builder.AddProject<Projects.FreedomBlaze>(nameof(Projects.FreedomBlaze).ToLower())
    .WithReference(insights)
    .WithExternalHttpEndpoints();

builder.Build().Run();
