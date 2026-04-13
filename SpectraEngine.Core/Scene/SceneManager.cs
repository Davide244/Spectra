using Microsoft.Extensions.Logging;

namespace SpectraEngine.Core.Scene;

public sealed class SceneManager
{
    private readonly ILogger<SceneManager> _logger;

    public SceneManager(ILogger<SceneManager> logger)
    {
        _logger = logger;
    }

    public void Initialize()
    {
        _logger.LogInformation("Scene manager initialized");
    }

    public void Update(double deltaTime)
    {
    }

    public void Shutdown()
    {
        _logger.LogInformation("Scene manager shut down");
    }
}
