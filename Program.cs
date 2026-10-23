using System.Text.Json.Serialization;
using BelediyeCBS;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Servisler ---
builder.Services.AddRazorPages();

builder.Services.AddDbContext<AppDb>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Default"),
    npgsql => npgsql.UseNetTopologySuite()));

builder.Services.AddHttpClient<AiClassifier>(client =>
    client.Timeout = TimeSpan.FromSeconds(8));

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// --- Başlangıç: tablolar, PostGIS eklentisi ve demo veri ---
await app.InitializeDatabaseAsync();

// --- Uç noktalar ---
app.MapRazorPages();
app.MapApi();

app.Run();