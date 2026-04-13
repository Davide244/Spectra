using Microsoft.Extensions.Logging;

namespace SpectraEngine.Core.Audio;

public sealed class AudioManager
{
    private readonly ILogger<AudioManager> _logger;

    public AudioManager(ILogger<AudioManager> logger)
    {
        _logger = logger;
    }

    public void Initialize()
    {
        _logger.LogInformation("Audio manager initialized");
    }

    public void Shutdown()
    {
        _logger.LogInformation("Audio manager shut down");
    }
}
