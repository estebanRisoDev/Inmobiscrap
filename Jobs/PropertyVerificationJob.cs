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
    /// vistas en 3+ días haciendo ping HTTP a su SourceUrl.
    /// Batch de 50 por ejecución para no saturar.
    /// </summary>
    public async Task ExecuteAsync()
    {
        _logger.LogInformation("PropertyVerificationJob: Starting daily verification...");

        try
        {
            var (verified, sold, active, errors) = await _verificationService
                .VerifyStalePropertiesAsync(staleDays: 3, batchSize: 50);

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
