using ScanRelay.Service;

var app = ServiceHost.Build(args);
await ServiceHost.StartCoreAsync(app);
await app.RunAsync();
