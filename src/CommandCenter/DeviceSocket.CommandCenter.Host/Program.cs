using DeviceSocket.CommandCenter.Host.Api;
using DeviceSocket.CommandCenter.Host.Commands;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["CommandCenter:HttpUrl"] ?? "http://0.0.0.0:5090");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<GatewayCommandClient>();
builder.Services.AddSingleton<CommandDispatchService>();

var gatewayPort = builder.Configuration.GetValue<int?>("Orleans:GatewayPort") ?? 30000;
builder.Host.UseOrleansClient(client => client.UseLocalhostClustering(gatewayPort));

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.MapCommandApi();
app.Run();
