using Microsoft.Extensions.Logging;
using Inmobiscrap.Services;

namespace Inmobiscrap.Jobs;

public class PropertyVerificationJob
{
    private readonly IPropertyVerificationService _verificationService;
    private readonly IVerificationJobStatus _jobStatus;
    private readonly ILogger<PropertyVerificationJob> _logger;

    public PropertyVerificationJob(
        IPropertyVerificationService verificationService,
        IVerificationJobStatus jobStatus,
        ILogger<PropertyVerificationJob> logger)
    {
        _verificationService = verificationService;
        _jobStatus = jobStatus;
        _logger = logger;
    }

    /// <summary>
    /// Job recurrente de Hangfire: verifica propiedades que no han sido
    /// vistas en 2+ días haciendo ping HTTP a su SourceUrl.
    /// Batch dinámico basado en el total de propiedades activas.
    /// Se ejecuta 2x/día (3:00 AM y 3:00 PM UTC).
    /// </summary>
    public async Task ExecuteAsync()
    {
        _logger.LogInformation("PropertyVerificationJob: Starting verification run...");

        try
        {
            var (verified, sold, active, errors) = await _verificationService
                .VerifyStalePropertiesAsync(staleDays: 2, batchSize: 0);

            _jobStatus.RecordRun(DateTime.UtcNow, verified, sold, active, errors);

            _logger.LogInformation(
                "PropertyVerificationJob: Completed. Verified={Verified}, Sold={Sold}, Active={Active}, Errors={Errors}",
                verified, sold, active, errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PropertyVerificationJob: Failed");
            throw;
        }
    }
}
