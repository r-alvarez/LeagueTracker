using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace LeagueTracker.Api.Tests;

internal sealed class TestEnv(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "tests";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Development";
    public string WebRootPath { get; set; } = root;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
