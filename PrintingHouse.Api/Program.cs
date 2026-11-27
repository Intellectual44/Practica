using PrintingHouse.Api;

var app = PrintHost.Build(args);
await PrintHost.Initialize(app);
await app.RunAsync();

public partial class Program;
