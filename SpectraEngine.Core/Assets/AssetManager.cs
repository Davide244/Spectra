using Microsoft.Extensions.Logging;

namespace SpectraEngine.Core.Assets;

public sealed class AssetManager
{
    private readonly ILogger<AssetManager> _logger;

    public AssetManager(ILogger<AssetManager> logger)
    {
        _logger = logger;
    }

    public void Initialize()
    {
        _logger.LogInformation("Asset manager initialized");
    }

    public void Shutdown()
    {
        _logger.LogInformation("Asset manager shut down");
    }
}
