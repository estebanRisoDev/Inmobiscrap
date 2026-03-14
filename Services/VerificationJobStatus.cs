namespace Inmobiscrap.Services;

public interface IVerificationJobStatus
{
    void RecordRun(DateTime ranAt, int verified, int sold, int active, int errors);
    VerificationJobRun? LastRun { get; }
}

public record VerificationJobRun(
    DateTime RanAt,
    int Verified,
    int Sold,
    int Active,
    int Errors
);

public class VerificationJobStatus : IVerificationJobStatus
{
    private VerificationJobRun? _lastRun;

    public VerificationJobRun? LastRun => _lastRun;

    public void RecordRun(DateTime ranAt, int verified, int sold, int active, int errors)
    {
        _lastRun = new VerificationJobRun(ranAt, verified, sold, active, errors);
    }
}
