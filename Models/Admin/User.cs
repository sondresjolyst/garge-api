using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

public class User : IdentityUser
{
    [Required]
    [MaxLength(50)]
    public required string FirstName { get; set; }
    [Required]
    [MaxLength(50)]
    public required string LastName { get; set; }
    public string? EmailVerificationCodeHash { get; set; }
    public DateTime? EmailVerificationCodeExpiration { get; set; }
    public string? PasswordResetCodeHash { get; set; }
    public DateTime? PasswordResetCodeExpiration { get; set; }
    public int PasswordResetAttempts { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public DateTime? TermsAcceptedAt { get; set; }
    [MaxLength(20)]
    public string? TermsVersion { get; set; }
    [MaxLength(45)]
    public string? TermsAcceptedIp { get; set; }

    /// <summary>
    /// When set, the user has objected (GDPR Art. 21) to retention of their sensor history after their
    /// subscription lapses: once they have no subscription coverage, their suspended sensors become
    /// eligible for the 6-month purge. Null (default) keeps history for the lifetime of the claim under
    /// legitimate interest, so a returning seasonal user still has their year-over-year data.
    /// </summary>
    public DateTime? DataRetentionOptOutAt { get; set; }
}
