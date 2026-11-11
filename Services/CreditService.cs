using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;

namespace Inmobiscrap.Services;

public enum CreditCheckStatus
{
    /// <summary>Pro o admin: no consume créditos.</summary>
    Unlimited,
    /// <summary>Se descontaron los créditos.</summary>
    Charged,
    Insufficient,
    UserNotFound,
}

public record CreditCheckResult(CreditCheckStatus Status, int CreditsBefore, int Amount, string Plan)
{
    public bool Allowed => Status is CreditCheckStatus.Unlimited or CreditCheckStatus.Charged;
    public int Remaining => CreditsBefore - Amount;
}

public interface ICreditService
{
    Task<CreditCheckResult> TryConsumeAsync(int userId, int amount);
    Task RefundAsync(int userId, int amount);
}

public class CreditService : ICreditService
{
    private readonly ApplicationDbContext _context;

    public CreditService(ApplicationDbContext context) => _context = context;

    public async Task<CreditCheckResult> TryConsumeAsync(int userId, int amount)
    {
        var user = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Plan, u.Role, u.Credits })
            .FirstOrDefaultAsync();

        if (user == null)
            return new CreditCheckResult(CreditCheckStatus.UserNotFound, 0, amount, "base");

        if (user.Plan == "pro" || user.Role == "admin")
            return new CreditCheckResult(CreditCheckStatus.Unlimited, user.Credits, 0, user.Plan);

        // Decremento atómico: solo descuenta si alcanza (evita race conditions)
        var rows = await _context.Users
            .Where(u => u.Id == userId && u.Credits >= amount)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Credits, u => u.Credits - amount));

        return rows == 0
            ? new CreditCheckResult(CreditCheckStatus.Insufficient, user.Credits, amount, user.Plan)
            : new CreditCheckResult(CreditCheckStatus.Charged, user.Credits, amount, user.Plan);
    }

    public async Task RefundAsync(int userId, int amount)
    {
        if (amount <= 0) return;
        await _context.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Credits, u => u.Credits + amount));
    }
}
