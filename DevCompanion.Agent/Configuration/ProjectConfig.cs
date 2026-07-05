namespace DevCompanion.Agent.Configuration;

public class ProjectConfig
{
    public string RootPath { get; set; } = string.Empty;
    public string LocalhostUrl { get; set; } = "http://localhost:5001";
    public string SwaggerPath { get; set; } = "/swagger/v1/swagger.json";
    public bool EnableGitTracking { get; set; } = true;
    public bool EnableShadowTesting { get; set; } = true;
}
